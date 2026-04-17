using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CLCKTY.App.Services;

public sealed class StatsService
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLCKTY");

    private static readonly string StatsFilePath = Path.Combine(DataFolder, "stats.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private StatsData _data;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public StatsService()
    {
        _data = LoadFromDisk();
        CleanupOldDailyData();
    }

    // ── Click Tracking ──────────────────────────────────────────────────

    public void RecordKeyboardClick(int virtualKey)
    {
        lock (_sync)
        {
            _data.TotalKeyboardClicks++;
            _data.SessionKeyboardClicks++;
            IncrementDailyCounter(true);

            // Track per-key counts for secret achievements
            var keyName = virtualKey.ToString("X2");
            if (!_data.KeyPressCounts.ContainsKey(keyName))
                _data.KeyPressCounts[keyName] = 0;
            _data.KeyPressCounts[keyName]++;

            AddXp(1, true);
            CheckAchievements();
            ThrottleSave();
        }
    }

    public void RecordMouseClick(int inputCode)
    {
        lock (_sync)
        {
            _data.TotalMouseClicks++;
            _data.SessionMouseClicks++;
            IncrementDailyCounter(false);

            AddXp(1, false);
            CheckAchievements();
            ThrottleSave();
        }
    }

    // ── XP & Level ──────────────────────────────────────────────────────

    public int Level
    {
        get { lock (_sync) return _data.Level; }
    }

    public long CurrentXp
    {
        get { lock (_sync) return _data.CurrentXp; }
    }

    public long XpToNextLevel
    {
        get { lock (_sync) return CalculateXpForLevel(_data.Level + 1); }
    }

    public string MainTitle
    {
        get { lock (_sync) return _data.MainTitle ?? "Newbie Clacker"; }
        set { lock (_sync) { _data.MainTitle = value; SaveToDisk(); } }
    }

    public string SecondaryTitle
    {
        get { lock (_sync) return _data.SecondaryTitle ?? ""; }
        set { lock (_sync) { _data.SecondaryTitle = value; SaveToDisk(); } }
    }

    // ── Stats ───────────────────────────────────────────────────────────

    public long TotalClackity
    {
        get { lock (_sync) return _data.TotalKeyboardClicks + _data.TotalMouseClicks; }
    }

    public long TotalKeyboardClicks
    {
        get { lock (_sync) return _data.TotalKeyboardClicks; }
    }

    public long TotalMouseClicks
    {
        get { lock (_sync) return _data.TotalMouseClicks; }
    }

    public long TodayClackity
    {
        get
        {
            lock (_sync)
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                long count = 0;
                if (_data.DailyKeyboardClicks.TryGetValue(today, out var kb)) count += kb;
                if (_data.DailyMouseClicks.TryGetValue(today, out var ms)) count += ms;
                return count;
            }
        }
    }

    public long ThisWeekClackity
    {
        get
        {
            lock (_sync)
            {
                var now = DateTime.Now;
                var startOfWeek = now.AddDays(-(int)now.DayOfWeek);
                long count = 0;
                for (var d = startOfWeek.Date; d <= now.Date; d = d.AddDays(1))
                {
                    var key = d.ToString("yyyy-MM-dd");
                    if (_data.DailyKeyboardClicks.TryGetValue(key, out var kb)) count += kb;
                    if (_data.DailyMouseClicks.TryGetValue(key, out var ms)) count += ms;
                }
                return count;
            }
        }
    }

    public long ThisMonthClackity
    {
        get
        {
            lock (_sync)
            {
                var now = DateTime.Now;
                var prefix = now.ToString("yyyy-MM");
                long count = 0;
                foreach (var kvp in _data.DailyKeyboardClicks)
                {
                    if (kvp.Key.StartsWith(prefix)) count += kvp.Value;
                }
                foreach (var kvp in _data.DailyMouseClicks)
                {
                    if (kvp.Key.StartsWith(prefix)) count += kvp.Value;
                }
                return count;
            }
        }
    }

    // ── Titles & Achievements ───────────────────────────────────────────

    public IReadOnlyList<UnlockedTitle> UnlockedTitles
    {
        get { lock (_sync) return _data.UnlockedTitles.ToList(); }
    }

    public IReadOnlyList<TitleDefinition> AllTitleDefinitions => TitleDefinitions;

    // ── Title Definitions ───────────────────────────────────────────────

    public static readonly IReadOnlyList<TitleDefinition> TitleDefinitions = new List<TitleDefinition>
    {
        // F-Tier (easy)
        new("first_click", "First Click", "Make your first click", "F", 0),
        new("baby_clacker", "Baby Clacker", "Reach 100 total clicks", "F", 100),
        new("getting_started", "Getting Started", "Reach 500 total clicks", "F", 500),

        // E-Tier
        new("keyboard_warrior_e", "Keyboard Warrior", "1,000 keyboard clicks", "E", 1_000),
        new("mouse_hunter_e", "Mouse Hunter", "1,000 mouse clicks", "E", 1_000),
        new("casual_clacker", "Casual Clacker", "2,500 total clicks", "E", 2_500),
        new("warming_up", "Warming Up", "5,000 total clicks", "E", 5_000),

        // D-Tier
        new("dedicated_d", "Dedicated Typist", "10,000 keyboard clicks", "D", 10_000),
        new("click_addict_d", "Click Addict", "10,000 mouse clicks", "D", 10_000),
        new("double_trouble", "Double Trouble", "Level 10 reached", "D", 0),
        new("clack_machine", "Clack Machine", "25,000 total clicks", "D", 25_000),

        // C-Tier
        new("keyboard_knight", "Keyboard Knight", "50,000 keyboard clicks", "C", 50_000),
        new("mouse_master_c", "Mouse Master", "50,000 mouse clicks", "C", 50_000),
        new("century_clacker", "Century Clacker", "100,000 total clicks", "C", 100_000),
        new("daily_grinder", "Daily Grinder", "10,000 clicks in a single day", "C", 0),

        // B-Tier
        new("keyboard_samurai", "Keyboard Samurai", "200,000 keyboard clicks", "B", 200_000),
        new("mouse_ninja", "Mouse Ninja", "200,000 mouse clicks", "B", 200_000),
        new("half_million", "Half Millionaire", "500,000 total clicks", "B", 500_000),
        new("level_25", "Ascended", "Level 25 reached", "B", 0),

        // A-Tier
        new("keyboard_legend", "Keyboard Legend", "1,000,000 keyboard clicks", "A", 1_000_000),
        new("mouse_emperor", "Mouse Emperor", "1,000,000 mouse clicks", "A", 1_000_000),
        new("millionaire", "Millionaire Clacker", "1,000,000 total clicks", "A", 1_000_000),
        new("week_warrior", "Week Warrior", "50,000 clicks in a single week", "A", 0),

        // S-Tier
        new("keyboard_god", "Keyboard God", "5,000,000 keyboard clicks", "S", 5_000_000),
        new("mouse_overlord", "Mouse Overlord", "5,000,000 mouse clicks", "S", 5_000_000),
        new("level_50", "Transcendent", "Level 50 reached", "S", 0),

        // SS-Tier
        new("keyboard_titan", "Keyboard Titan", "25,000,000 keyboard clicks", "SS", 25_000_000),
        new("mouse_titan", "Mouse Titan", "25,000,000 mouse clicks", "SS", 25_000_000),
        new("fifty_million", "Fifty Million Strong", "50,000,000 total clicks", "SS", 50_000_000),

        // SSS-Tier
        new("keyboard_immortal", "Keyboard Immortal", "100,000,000 keyboard clicks", "SSS", 100_000_000),
        new("mouse_immortal", "Mouse Immortal", "100,000,000 mouse clicks", "SSS", 100_000_000),
        new("level_100", "The Eternal", "Level 100 reached", "SSS", 0),

        // EX-Tier (nearly impossible)
        new("keyboard_universe", "Keyboard Universe", "500,000,000 keyboard clicks", "EX", 500_000_000),
        new("mouse_universe", "Mouse Universe", "500,000,000 mouse clicks", "EX", 500_000_000),
        new("billion_clacker", "Billion Clacker", "1,000,000,000 total clicks", "EX", 1_000_000_000),
        new("level_200", "The Absolute", "Level 200 reached", "EX", 0),

        // Secret achievements
        new("secret_spacebar_king", "Spacebar King", "[SECRET] Press spacebar 50,000 times", "A", 0, true),
        new("secret_enter_master", "Enter Master", "[SECRET] Press Enter 25,000 times", "S", 0, true),
        new("secret_backspace_eraser", "The Eraser", "[SECRET] Press Backspace 30,000 times", "S", 0, true),
        new("secret_night_owl", "Night Owl", "[SECRET] Click 1,000 times between midnight and 4 AM", "A", 0, true),
        new("secret_speed_demon", "Speed Demon", "[SECRET] 500+ clicks in under 60 seconds", "SS", 0, true),
        new("secret_one_key_wonder", "One Key Wonder", "[SECRET] Press a single key 100,000 times", "SSS", 0, true),
    };

    // ── Internal ────────────────────────────────────────────────────────

    private void AddXp(int amount, bool isKeyboard)
    {
        _data.CurrentXp += amount;

        while (_data.CurrentXp >= CalculateXpForLevel(_data.Level + 1))
        {
            _data.CurrentXp -= CalculateXpForLevel(_data.Level + 1);
            _data.Level++;
            CheckLevelAchievements();
        }
    }

    /// <summary>
    /// XP formula: base 100, x10 scaling every 10 levels.
    /// Level 1→2: 100 XP, Level 11→12: 1,000 XP, Level 21→22: 10,000 XP, etc.
    /// </summary>
    private static long CalculateXpForLevel(int level)
    {
        if (level <= 1) return 100;
        var tier = (level - 1) / 10;
        var baseCost = 100L;
        for (var i = 0; i < tier; i++)
        {
            baseCost *= 10;
        }
        return baseCost + (level * 10);
    }

    private void CheckAchievements()
    {
        var total = _data.TotalKeyboardClicks + _data.TotalMouseClicks;
        var kb = _data.TotalKeyboardClicks;
        var ms = _data.TotalMouseClicks;

        TryUnlock("first_click", total >= 1);
        TryUnlock("baby_clacker", total >= 100);
        TryUnlock("getting_started", total >= 500);

        TryUnlock("keyboard_warrior_e", kb >= 1_000);
        TryUnlock("mouse_hunter_e", ms >= 1_000);
        TryUnlock("casual_clacker", total >= 2_500);
        TryUnlock("warming_up", total >= 5_000);

        TryUnlock("dedicated_d", kb >= 10_000);
        TryUnlock("click_addict_d", ms >= 10_000);
        TryUnlock("clack_machine", total >= 25_000);

        TryUnlock("keyboard_knight", kb >= 50_000);
        TryUnlock("mouse_master_c", ms >= 50_000);
        TryUnlock("century_clacker", total >= 100_000);

        TryUnlock("keyboard_samurai", kb >= 200_000);
        TryUnlock("mouse_ninja", ms >= 200_000);
        TryUnlock("half_million", total >= 500_000);

        TryUnlock("keyboard_legend", kb >= 1_000_000);
        TryUnlock("mouse_emperor", ms >= 1_000_000);
        TryUnlock("millionaire", total >= 1_000_000);

        TryUnlock("keyboard_god", kb >= 5_000_000);
        TryUnlock("mouse_overlord", ms >= 5_000_000);

        TryUnlock("keyboard_titan", kb >= 25_000_000);
        TryUnlock("mouse_titan", ms >= 25_000_000);
        TryUnlock("fifty_million", total >= 50_000_000);

        TryUnlock("keyboard_immortal", kb >= 100_000_000);
        TryUnlock("mouse_immortal", ms >= 100_000_000);

        TryUnlock("keyboard_universe", kb >= 500_000_000);
        TryUnlock("mouse_universe", ms >= 500_000_000);
        TryUnlock("billion_clacker", total >= 1_000_000_000);

        // Daily grinder
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        long todayTotal = 0;
        if (_data.DailyKeyboardClicks.TryGetValue(today, out var tkb)) todayTotal += tkb;
        if (_data.DailyMouseClicks.TryGetValue(today, out var tms)) todayTotal += tms;
        TryUnlock("daily_grinder", todayTotal >= 10_000);

        // Week warrior
        TryUnlock("week_warrior", ThisWeekClackity >= 50_000);

        // Secret: spacebar (0x20)
        if (_data.KeyPressCounts.TryGetValue("20", out var spaceCount))
            TryUnlock("secret_spacebar_king", spaceCount >= 50_000);

        // Secret: enter (0x0D)
        if (_data.KeyPressCounts.TryGetValue("0D", out var enterCount))
            TryUnlock("secret_enter_master", enterCount >= 25_000);

        // Secret: backspace (0x08)
        if (_data.KeyPressCounts.TryGetValue("08", out var bsCount))
            TryUnlock("secret_backspace_eraser", bsCount >= 30_000);

        // Secret: one key wonder
        foreach (var kvp in _data.KeyPressCounts)
        {
            if (kvp.Value >= 100_000)
            {
                TryUnlock("secret_one_key_wonder", true);
                break;
            }
        }

        // Secret: night owl - check current hour
        var hour = DateTime.Now.Hour;
        if (hour >= 0 && hour < 4)
        {
            _data.NightOwlClicks++;
            TryUnlock("secret_night_owl", _data.NightOwlClicks >= 1_000);
        }

        // Secret: speed demon - track via session burst
        _data.BurstClickTimestamps.Add(DateTime.UtcNow.Ticks);
        var cutoff = DateTime.UtcNow.AddSeconds(-60).Ticks;
        _data.BurstClickTimestamps.RemoveAll(t => t < cutoff);
        TryUnlock("secret_speed_demon", _data.BurstClickTimestamps.Count >= 500);
    }

    private void CheckLevelAchievements()
    {
        TryUnlock("double_trouble", _data.Level >= 10);
        TryUnlock("level_25", _data.Level >= 25);
        TryUnlock("level_50", _data.Level >= 50);
        TryUnlock("level_100", _data.Level >= 100);
        TryUnlock("level_200", _data.Level >= 200);
    }

    private void TryUnlock(string titleId, bool condition)
    {
        if (!condition) return;
        if (_data.UnlockedTitles.Any(t => t.Id == titleId)) return;

        var def = TitleDefinitions.FirstOrDefault(d => d.Id == titleId);
        if (def == null) return;

        _data.UnlockedTitles.Add(new UnlockedTitle(titleId, def.Name, def.Rarity, DateTime.UtcNow));

        // Auto-set main title if it's a higher rarity
        if (_data.MainTitle == null || GetRarityRank(def.Rarity) > GetRarityRank(GetMainTitleRarity()))
        {
            _data.MainTitle = def.Name;
        }
    }

    private string GetMainTitleRarity()
    {
        var mainTitle = _data.MainTitle;
        if (mainTitle == null) return "F";
        var unlocked = _data.UnlockedTitles.FirstOrDefault(t => t.Name == mainTitle);
        return unlocked?.Rarity ?? "F";
    }

    public static int GetRarityRank(string rarity)
    {
        return rarity switch
        {
            "F" => 0, "E" => 1, "D" => 2, "C" => 3, "B" => 4,
            "A" => 5, "S" => 6, "SS" => 7, "SSS" => 8, "EX" => 9,
            _ => 0
        };
    }

    public static string GetRarityColor(string rarity)
    {
        return rarity switch
        {
            "F" => "#808080",   // Gray
            "E" => "#8BC34A",   // Light green
            "D" => "#4FC3F7",   // Light blue
            "C" => "#AB47BC",   // Purple
            "B" => "#FF7043",   // Deep orange
            "A" => "#FFD54F",   // Gold
            "S" => "#FF4081",   // Pink
            "SS" => "#E040FB",  // Magenta
            "SSS" => "#FF1744", // Crimson
            "EX" => "#FFD700",  // Bright gold with glow
            _ => "#808080"
        };
    }

    private void IncrementDailyCounter(bool isKeyboard)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dict = isKeyboard ? _data.DailyKeyboardClicks : _data.DailyMouseClicks;
        if (!dict.ContainsKey(today)) dict[today] = 0;
        dict[today]++;
    }

    private void CleanupOldDailyData()
    {
        var cutoff = DateTime.Now.AddDays(-60).ToString("yyyy-MM-dd");
        var keysToRemove = _data.DailyKeyboardClicks.Keys.Where(k => string.Compare(k, cutoff) < 0).ToList();
        foreach (var key in keysToRemove) _data.DailyKeyboardClicks.Remove(key);
        keysToRemove = _data.DailyMouseClicks.Keys.Where(k => string.Compare(k, cutoff) < 0).ToList();
        foreach (var key in keysToRemove) _data.DailyMouseClicks.Remove(key);
    }

    private void ThrottleSave()
    {
        if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 10) return;
        SaveToDisk();
    }

    public void ForceSave()
    {
        lock (_sync) SaveToDisk();
    }

    private void SaveToDisk()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(StatsFilePath, json);
            _lastSaveUtc = DateTime.UtcNow;
        }
        catch { /* ignore save failures */ }
    }

    private static StatsData LoadFromDisk()
    {
        try
        {
            if (!File.Exists(StatsFilePath)) return new StatsData();
            var json = File.ReadAllText(StatsFilePath);
            return JsonSerializer.Deserialize<StatsData>(json, JsonOptions) ?? new StatsData();
        }
        catch { return new StatsData(); }
    }
}

// ── Data Models ─────────────────────────────────────────────────────────

public sealed class StatsData
{
    public long TotalKeyboardClicks { get; set; }
    public long TotalMouseClicks { get; set; }
    public long SessionKeyboardClicks { get; set; }
    public long SessionMouseClicks { get; set; }
    public int Level { get; set; } = 1;
    public long CurrentXp { get; set; }
    public string? MainTitle { get; set; }
    public string? SecondaryTitle { get; set; }
    public Dictionary<string, long> KeyPressCounts { get; set; } = new();
    public Dictionary<string, long> DailyKeyboardClicks { get; set; } = new();
    public Dictionary<string, long> DailyMouseClicks { get; set; } = new();
    public List<UnlockedTitle> UnlockedTitles { get; set; } = new();
    public long NightOwlClicks { get; set; }
    public List<long> BurstClickTimestamps { get; set; } = new();
}

public sealed record UnlockedTitle(string Id, string Name, string Rarity, DateTime UnlockedAtUtc);

public sealed record TitleDefinition(
    string Id,
    string Name,
    string Description,
    string Rarity,
    long RequiredCount,
    bool IsSecret = false);


