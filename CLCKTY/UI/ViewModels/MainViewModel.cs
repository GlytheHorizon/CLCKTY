using System.Collections.ObjectModel;
using CLCKTY.Core;
using CLCKTY.Services;

namespace CLCKTY.UI.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISoundEngine _soundEngine;
    private readonly IAudioProfileManager _profileManager;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private CancellationTokenSource? _saveDebounceTokenSource;

    private bool _isInitializing;
    private bool _isLoaded;

    private bool _soundsEnabled = true;
    private bool _spatialAudioEnabled = true;
    private bool _randomPitchEnabled = true;
    private bool _keyDownEnabled = true;
    private bool _keyUpEnabled = true;
    private bool _mouseLeftEnabled = true;
    private bool _mouseRightEnabled = true;
    private bool _mouseMiddleEnabled = true;
    private double _masterVolume = 0.85;
    private double _toneX = -0.25;
    private double _toneY;
    private bool _startWithWindows;
    private LatencyMode _latencyMode = LatencyMode.Performance;
    private bool _globalHotkeyEnabled = true;
    private string _toggleHotkey = "Ctrl+Alt+M";
    private bool _startMinimizedToTray = true;
    private double _cpuUsagePercent;
    private double _engineLatencyMs;
    private string _statusText = "Ready.";
    private AudioProfile? _selectedProfile;
    private AudioAsset? _selectedAsset;
    private SoundSlot _selectedRecordingSlot = SoundSlot.KeyDown;

    public MainViewModel(
        ISoundEngine soundEngine,
        IAudioProfileManager profileManager,
        ISettingsService settingsService)
    {
        _soundEngine = soundEngine;
        _profileManager = profileManager;
        _settingsService = settingsService;
    }

    public event EventHandler<bool>? SoundsEnabledChanged;

    public event EventHandler<string>? ErrorRaised;

    public ObservableCollection<AudioProfile> Profiles { get; } = [];

    public ObservableCollection<AudioAsset> ProfileAssets { get; } = [];

    public IEnumerable<LatencyMode> LatencyModes { get; } = Enum.GetValues<LatencyMode>();

    public IEnumerable<SoundSlot> RecordableSlots { get; } = Enum.GetValues<SoundSlot>();

    public string AppVersion => $"v{typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    public string SafetyNote => "No keystroke logging. Fully offline.";

    public bool SoundsEnabled
    {
        get => _soundsEnabled;
        set
        {
            if (!SetProperty(ref _soundsEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            SoundsEnabledChanged?.Invoke(this, value);
            ScheduleSettingsSave();
        }
    }

    public bool SpatialAudioEnabled
    {
        get => _spatialAudioEnabled;
        set
        {
            if (!SetProperty(ref _spatialAudioEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool RandomPitchEnabled
    {
        get => _randomPitchEnabled;
        set
        {
            if (!SetProperty(ref _randomPitchEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool KeyDownEnabled
    {
        get => _keyDownEnabled;
        set
        {
            if (!SetProperty(ref _keyDownEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool KeyUpEnabled
    {
        get => _keyUpEnabled;
        set
        {
            if (!SetProperty(ref _keyUpEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool MouseLeftEnabled
    {
        get => _mouseLeftEnabled;
        set
        {
            if (!SetProperty(ref _mouseLeftEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool MouseRightEnabled
    {
        get => _mouseRightEnabled;
        set
        {
            if (!SetProperty(ref _mouseRightEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool MouseMiddleEnabled
    {
        get => _mouseMiddleEnabled;
        set
        {
            if (!SetProperty(ref _mouseMiddleEnabled, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            value = Math.Clamp(value, 0.0, 1.0);
            if (!SetProperty(ref _masterVolume, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public double ToneX
    {
        get => _toneX;
        set
        {
            value = Math.Clamp(value, -1.0, 1.0);
            if (!SetProperty(ref _toneX, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public double ToneY
    {
        get => _toneY;
        set
        {
            value = Math.Clamp(value, -1.0, 1.0);
            if (!SetProperty(ref _toneY, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetProperty(ref _startWithWindows, value))
            {
                return;
            }

            _ = ApplyStartupRegistrationSafelyAsync(value);
            ScheduleSettingsSave();
        }
    }

    public LatencyMode LatencyMode
    {
        get => _latencyMode;
        set
        {
            if (!SetProperty(ref _latencyMode, value))
            {
                return;
            }

            ApplyRuntimeConfiguration();
            ScheduleSettingsSave();
        }
    }

    public bool GlobalHotkeyEnabled
    {
        get => _globalHotkeyEnabled;
        set
        {
            if (!SetProperty(ref _globalHotkeyEnabled, value))
            {
                return;
            }

            ScheduleSettingsSave();
        }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "Ctrl+Alt+M" : value;
            if (!SetProperty(ref _toggleHotkey, value))
            {
                return;
            }

            ScheduleSettingsSave();
        }
    }

    public bool StartMinimizedToTray
    {
        get => _startMinimizedToTray;
        set
        {
            if (!SetProperty(ref _startMinimizedToTray, value))
            {
                return;
            }

            ScheduleSettingsSave();
        }
    }

    public double CpuUsagePercent
    {
        get => _cpuUsagePercent;
        private set => SetProperty(ref _cpuUsagePercent, value);
    }

    public double EngineLatencyMs
    {
        get => _engineLatencyMs;
        private set => SetProperty(ref _engineLatencyMs, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public AudioProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
            {
                return;
            }

            RefreshAssetList();

            if (_isLoaded && value is not null)
            {
                _ = ActivateProfileAsync(value);
            }
        }
    }

    public AudioAsset? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                OnPropertyChanged(nameof(CanDeleteSelectedAsset));
            }
        }
    }

    public SoundSlot SelectedRecordingSlot
    {
        get => _selectedRecordingSlot;
        set => SetProperty(ref _selectedRecordingSlot, value);
    }

    public bool CanDeleteSelectedAsset => SelectedAsset?.IsDeletable == true && SelectedProfile is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _isInitializing = true;
        try
        {
            var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            ApplySettings(settings);

            await RefreshProfilesAndSelectAsync(settings.ActiveProfileId, persistSelection: false, cancellationToken).ConfigureAwait(false);

            ApplyRuntimeConfiguration();
            StatusText = "Sound engine armed.";

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            StatusText = "Initialization failed.";
            ErrorRaised?.Invoke(this, ex.Message);
        }
        finally
        {
            _isInitializing = false;
        }
    }

    public async Task ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var importedProfile = await _profileManager.ImportSoundPackAsync(folderPath, cancellationToken).ConfigureAwait(false);
            await RefreshProfilesAndSelectAsync(importedProfile.Id, persistSelection: true, cancellationToken).ConfigureAwait(false);
            StatusText = $"Imported {importedProfile.Name}.";
        }
        catch (Exception ex)
        {
            StatusText = "Import failed.";
            ErrorRaised?.Invoke(this, ex.Message);
        }
    }

    public async Task RecordClipAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            StatusText = "Recording clip...";
            var profile = await _profileManager.RecordClipAsync(SelectedRecordingSlot, cancellationToken).ConfigureAwait(false);
            await RefreshProfilesAndSelectAsync(profile.Id, persistSelection: true, cancellationToken).ConfigureAwait(false);
            StatusText = $"Recorded {SelectedRecordingSlot} clip.";
        }
        catch (Exception ex)
        {
            StatusText = "Recording failed.";
            ErrorRaised?.Invoke(this, ex.Message);
        }
    }

    public async Task<bool> DeleteSelectedAssetAsync(CancellationToken cancellationToken = default)
    {
        if (!CanDeleteSelectedAsset || SelectedProfile is null || SelectedAsset is null)
        {
            return false;
        }

        var selectedProfileId = SelectedProfile.Id;
        var deleted = await _profileManager.DeleteAssetAsync(selectedProfileId, SelectedAsset.Id, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            await RefreshProfilesAndSelectAsync(selectedProfileId, persistSelection: false, cancellationToken).ConfigureAwait(false);
            StatusText = "Asset deleted.";
        }

        return deleted;
    }

    public async Task ResetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var defaults = new AppSettings();
        ApplySettings(defaults);
        await _settingsService.ApplyStartupRegistrationAsync(defaults.StartWithWindows, cancellationToken).ConfigureAwait(false);
        await SaveSettingsCoreAsync(cancellationToken).ConfigureAwait(false);
        await RefreshProfilesAndSelectAsync(defaults.ActiveProfileId, persistSelection: false, cancellationToken).ConfigureAwait(false);

        ApplyRuntimeConfiguration();
        StatusText = "Configuration reset.";
    }

    public void ToggleSounds()
    {
        SoundsEnabled = !SoundsEnabled;
    }

    public void SetSoundsEnabledFromTray(bool enabled)
    {
        SoundsEnabled = enabled;
    }

    public void UpdateCpuUsage(double cpuUsagePercent)
    {
        CpuUsagePercent = Math.Round(cpuUsagePercent, 1);
    }

    public void UpdateLatencyIndicator()
    {
        EngineLatencyMs = Math.Round(_soundEngine.LastDispatchLatencyMs, 2);
    }

    public void Dispose()
    {
        _saveDebounceTokenSource?.Cancel();
        _saveDebounceTokenSource?.Dispose();
        _saveGate.Dispose();
    }

    private async Task ActivateProfileAsync(AudioProfile profile)
    {
        try
        {
            await _soundEngine.InitializeAsync(profile).ConfigureAwait(false);
            StatusText = $"Active profile: {profile.Name}";
            ScheduleSettingsSave();
        }
        catch (Exception ex)
        {
            StatusText = "Profile activation failed.";
            ErrorRaised?.Invoke(this, ex.Message);
        }
    }

    private async Task RefreshProfilesAndSelectAsync(
        string? profileId,
        bool persistSelection,
        CancellationToken cancellationToken)
    {
        var profiles = await _profileManager.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);

        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        var selected = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault();

        _selectedProfile = selected;
        OnPropertyChanged(nameof(SelectedProfile));
        RefreshAssetList();

        if (selected is not null)
        {
            await _soundEngine.InitializeAsync(selected, cancellationToken).ConfigureAwait(false);
            if (persistSelection)
            {
                ScheduleSettingsSave();
            }
        }
    }

    private void RefreshAssetList()
    {
        ProfileAssets.Clear();
        if (SelectedProfile is null)
        {
            OnPropertyChanged(nameof(CanDeleteSelectedAsset));
            return;
        }

        foreach (var asset in SelectedProfile.Assets
                     .OrderBy(asset => asset.Badge)
                     .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase))
        {
            ProfileAssets.Add(asset);
        }

        SelectedAsset = ProfileAssets.FirstOrDefault();
        OnPropertyChanged(nameof(CanDeleteSelectedAsset));
    }

    private void ApplySettings(AppSettings settings)
    {
        _isInitializing = true;

        SoundsEnabled = settings.SoundsEnabled;
        SpatialAudioEnabled = settings.SpatialAudioEnabled;
        RandomPitchEnabled = settings.RandomPitchEnabled;
        KeyDownEnabled = settings.KeyDownEnabled;
        KeyUpEnabled = settings.KeyUpEnabled;
        MouseLeftEnabled = settings.MouseLeftEnabled;
        MouseRightEnabled = settings.MouseRightEnabled;
        MouseMiddleEnabled = settings.MouseMiddleEnabled;
        MasterVolume = settings.MasterVolume;
        ToneX = settings.ToneX;
        ToneY = settings.ToneY;
        StartWithWindows = settings.StartWithWindows;
        LatencyMode = settings.LatencyMode;
        GlobalHotkeyEnabled = settings.GlobalHotkeyEnabled;
        ToggleHotkey = settings.ToggleHotkey;
        StartMinimizedToTray = settings.StartMinimizedToTray;

        _isInitializing = false;
    }

    private void ApplyRuntimeConfiguration()
    {
        var config = new SoundEngineConfiguration
        {
            SoundsEnabled = SoundsEnabled,
            SpatialAudioEnabled = SpatialAudioEnabled,
            RandomPitchEnabled = RandomPitchEnabled,
            KeyDownEnabled = KeyDownEnabled,
            KeyUpEnabled = KeyUpEnabled,
            MouseLeftEnabled = MouseLeftEnabled,
            MouseRightEnabled = MouseRightEnabled,
            MouseMiddleEnabled = MouseMiddleEnabled,
            MasterVolume = (float)MasterVolume,
            ToneX = (float)ToneX,
            ToneY = (float)ToneY,
            LatencyMode = LatencyMode
        };

        _soundEngine.UpdateConfiguration(config);
        _soundEngine.IsMuted = !SoundsEnabled;
    }

    private void ScheduleSettingsSave()
    {
        if (_isInitializing)
        {
            return;
        }

        var oldTokenSource = Interlocked.Exchange(ref _saveDebounceTokenSource, new CancellationTokenSource());
        oldTokenSource?.Cancel();
        oldTokenSource?.Dispose();

        var token = _saveDebounceTokenSource!.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                await SaveSettingsCoreAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore debounce cancellations.
            }
            catch (Exception ex)
            {
                ErrorRaised?.Invoke(this, ex.Message);
            }
        }, token);
    }

    private async Task SaveSettingsCoreAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = new AppSettings
            {
                SoundsEnabled = SoundsEnabled,
                SpatialAudioEnabled = SpatialAudioEnabled,
                RandomPitchEnabled = RandomPitchEnabled,
                KeyDownEnabled = KeyDownEnabled,
                KeyUpEnabled = KeyUpEnabled,
                MouseLeftEnabled = MouseLeftEnabled,
                MouseRightEnabled = MouseRightEnabled,
                MouseMiddleEnabled = MouseMiddleEnabled,
                MasterVolume = MasterVolume,
                ToneX = ToneX,
                ToneY = ToneY,
                ActiveProfileId = SelectedProfile?.Id ?? "stock-default",
                StartWithWindows = StartWithWindows,
                LatencyMode = LatencyMode,
                GlobalHotkeyEnabled = GlobalHotkeyEnabled,
                ToggleHotkey = ToggleHotkey,
                StartMinimizedToTray = StartMinimizedToTray
            };

            await _settingsService.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task ApplyStartupRegistrationSafelyAsync(bool enabled)
    {
        try
        {
            await _settingsService.ApplyStartupRegistrationAsync(enabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke(this, ex.Message);
        }
    }
}
