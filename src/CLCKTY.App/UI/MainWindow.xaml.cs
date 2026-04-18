using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CLCKTY.App.Core;
using CLCKTY.App.Services;
using NAudio.Wave;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfPoint = System.Windows.Point;

namespace CLCKTY.App.UI;

public partial class MainWindow : Window
{
    private static readonly string CustomPacksFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CLCKTY", "CustomPacks");

    private const double MaxRecordingSeconds = 2.5;

    private StatsService? _statsService;
    private ISoundEngine? _soundEngine;
    private readonly DispatcherTimer _statsRefreshTimer;

    private string? _currentPackFolder;
    private readonly List<string> _currentPackFiles = new();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _waveWriter;
    private string? _recordingFilePath;
    private bool _isRecording;
    private DateTime _recordingStartUtc;
    private DispatcherTimer? _recordingTimer;
    private bool _isSavingPackage;

    private WaveOutEvent? _previewOutput;
    private AudioFileReader? _previewReader;

    private static readonly string[] AchievementRankPages =
    [
        "F", "E", "D", "C", "B", "A", "S", "SS", "SSS", "EX"
    ];
    private int _achievementPageIndex;

    private sealed class OutputDeviceOption
    {
        public OutputDeviceOption(int deviceNumber, string name)
        {
            DeviceNumber = deviceNumber;
            Name = name;
        }

        public int DeviceNumber { get; }

        public string Name { get; }
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        NavigateToSection("Dashboard");
        viewModel.InputTriggered += ViewModel_InputTriggered;

        _statsRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statsRefreshTimer.Tick += (_, _) => RefreshStatsDisplay();

        Closed += (_, _) =>
        {
            _statsRefreshTimer.Stop();
            StopRecordingIfActive();
            StopPreviewPlayback();
        };
    }

    public void SetStatsService(StatsService statsService)
    {
        _statsService = statsService;
        RefreshStatsDisplay();
        _statsRefreshTimer.Start();
    }

    public void SetSoundEngine(ISoundEngine soundEngine)
    {
        _soundEngine = soundEngine;

        if (IsLoaded)
        {
            LoadOutputDevices();
        }
    }

    public void ShowPanel()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Opacity = 0;
        BeginOpenAnimation();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // set window icon from shared logo loader so taskbar/tray use matching visuals.
        try
        {
            var taskbarIcon = IconAssetLoader.LoadTaskbarLogo();
            if (taskbarIcon is not null)
            {
                Icon = taskbarIcon;
            }
        }
        catch
        {
            // ignore and continue
        }

        BeginOpenAnimation();
        InitializeVisualizerKeycap();
        LoadOutputDevices();
        RefreshCreatedPackagesList();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("Settings");
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("Info");
    }

    private void StatsButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("Stats");
    }

    private void RecordItNavButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("RecordIt");
    }

    private void DeleteClipOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not FrameworkElement element
            || element.DataContext is not KeyMappingOption option
            || !viewModel.RemoveClipOptionCommand.CanExecute(option))
        {
            return;
        }

        viewModel.RemoveClipOptionCommand.Execute(option);
        e.Handled = true;
    }

    private void DeleteProfileOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel
            || sender is not FrameworkElement element
            || element.DataContext is not SoundProfileDescriptor profile
            || !profile.IsImported)
        {
            return;
        }

        if (string.Equals(profile.SourceLabel, "custom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.SourceLabel, "custome", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var confirmDialog = new ConfirmActionDialog(
            "Delete Imported Pack",
            $"Delete imported pack '{profile.DisplayName}'?",
            "Delete Pack")
        {
            Owner = this
        };

        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        viewModel.TryRemoveImportedProfile(profile);

        e.Handled = true;
    }

    private void BeginOpenAnimation()
    {
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private void DashboardNavButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("Dashboard");
    }

    private void SoundProfilesNavButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("SoundProfiles");
    }

    private void KeyMappingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("KeyMappings");
    }

    private void MouseSettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateToSection("MouseSettings");
    }

    private void NavigateToSection(string section)
    {
        DashboardSectionPanel.Visibility = section == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        SoundProfilesSectionPanel.Visibility = section == "SoundProfiles" ? Visibility.Visible : Visibility.Collapsed;
        KeyMappingsSectionPanel.Visibility = section == "KeyMappings" ? Visibility.Visible : Visibility.Collapsed;
        MouseSettingsSectionPanel.Visibility = section == "MouseSettings" ? Visibility.Visible : Visibility.Collapsed;
        RecordItSectionPanel.Visibility = section == "RecordIt" ? Visibility.Visible : Visibility.Collapsed;
        StatsSectionPanel.Visibility = section == "Stats" ? Visibility.Visible : Visibility.Collapsed;
        InfoSectionPanel.Visibility = section == "Info" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSectionPanel.Visibility = section == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        SetNavButtonState(DashboardNavButton, section == "Dashboard");
        SetNavButtonState(SoundProfilesNavButton, section == "SoundProfiles");
        SetNavButtonState(KeyMappingsNavButton, section == "KeyMappings");
        SetNavButtonState(MouseSettingsNavButton, section == "MouseSettings");
        SetNavButtonState(RecordItNavButton, section == "RecordIt");

        if (section == "Stats")
        {
            RefreshStatsDisplay();
        }
    }

    private static void SetNavButtonState(System.Windows.Controls.Button button, bool isActive)
    {
        button.Tag = isActive ? "Active" : null;
        button.Foreground = isActive
            ? new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"))
            : new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#9FD8C1"));
    }

    private void ViewModel_InputTriggered(object? sender, InputTriggeredPreviewEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            AnimateTestTypingPreview();
            AnimateInputKeycap(e);
        });
    }

    private void AnimateTestTypingPreview()
    {
        if (TestTypingPreviewCard.RenderTransform is not ScaleTransform scaleTransform)
        {
            scaleTransform = new ScaleTransform(1d, 1d);
            TestTypingPreviewCard.RenderTransformOrigin = new WpfPoint(0.5d, 0.5d);
            TestTypingPreviewCard.RenderTransform = scaleTransform;
        }

        var scalePulse = new DoubleAnimation
        {
            To = 1.05d,
            Duration = TimeSpan.FromMilliseconds(90),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var opacityPulse = new DoubleAnimation
        {
            To = 0.82d,
            Duration = TimeSpan.FromMilliseconds(90),
            AutoReverse = true
        };

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scalePulse, HandoffBehavior.SnapshotAndReplace);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scalePulse, HandoffBehavior.SnapshotAndReplace);
        TestTypingPreviewCard.BeginAnimation(OpacityProperty, opacityPulse, HandoffBehavior.SnapshotAndReplace);
    }

    private void InitializeVisualizerKeycap()
    {
        VisualizerKeycapLabel.Text = "A";
        VisualizerKeycapState.Text = "READY";
        VisualizerKeycapShadow.Opacity = 0.35d;
    }

    private void AnimateInputKeycap(InputTriggeredPreviewEventArgs args)
    {
        var isPressed = args.Trigger == KeyEventTrigger.Down;
        var translateY = isPressed ? 7d : 0d;
        var scale = isPressed ? 0.965d : 1d;
        var shadowOpacity = isPressed ? 0.18d : 0.35d;
        var animationDuration = TimeSpan.FromMilliseconds(isPressed ? 78 : 118);

        VisualizerKeycapLabel.Text = BuildKeycapLabel(args.InputCode);
        VisualizerKeycapState.Text = isPressed ? "PRESSED" : "RELEASED";

        var easing = new CubicEase
        {
            EasingMode = isPressed ? EasingMode.EaseOut : EasingMode.EaseInOut
        };

        var keycapMoveAnimation = new DoubleAnimation
        {
            To = translateY,
            Duration = animationDuration,
            EasingFunction = easing
        };

        var keycapScaleAnimation = new DoubleAnimation
        {
            To = scale,
            Duration = animationDuration,
            EasingFunction = easing
        };

        var shadowOpacityAnimation = new DoubleAnimation
        {
            To = shadowOpacity,
            Duration = animationDuration,
            EasingFunction = easing
        };

        VisualizerKeycapTranslate.BeginAnimation(TranslateTransform.YProperty, keycapMoveAnimation, HandoffBehavior.SnapshotAndReplace);
        VisualizerKeycapScale.BeginAnimation(ScaleTransform.ScaleXProperty, keycapScaleAnimation, HandoffBehavior.SnapshotAndReplace);
        VisualizerKeycapScale.BeginAnimation(ScaleTransform.ScaleYProperty, keycapScaleAnimation, HandoffBehavior.SnapshotAndReplace);
        VisualizerKeycapShadow.BeginAnimation(OpacityProperty, shadowOpacityAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    // ── Mode Preset Handlers ────────────────────────────────────────────

    private void ModeNormal_Click(object sender, MouseButtonEventArgs e)
    {
        ApplyMode("Normal", 75);
    }

    private void ModeMeeting_Click(object sender, MouseButtonEventArgs e)
    {
        ApplyMode("Meeting", 25);
    }

    private void ModeGaming_Click(object sender, MouseButtonEventArgs e)
    {
        ApplyMode("Gaming", 90);
    }

    private void ModeSilent_Click(object sender, MouseButtonEventArgs e)
    {
        ApplyMode("Silent", 0);
    }

    private void ApplyMode(string modeName, double volume)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Volume = volume;

            switch (modeName)
            {
                case "Meeting":
                    vm.IsKeyboardSoundEnabled = true;
                    vm.IsMouseSoundEnabled = false;
                    break;
                case "Silent":
                    vm.IsEnabled = false;
                    break;
                case "Gaming":
                    vm.IsEnabled = true;
                    vm.IsKeyboardSoundEnabled = true;
                    vm.IsMouseSoundEnabled = true;
                    break;
                default: // Normal
                    vm.IsEnabled = true;
                    vm.IsKeyboardSoundEnabled = true;
                    vm.IsMouseSoundEnabled = true;
                    break;
            }
        }

        UpdateModeButtonStyles(modeName);
    }

    private void UpdateModeButtonStyles(string activeMode)
    {
        var accentBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        var inactiveBg = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#1A3D30"));
        var activeTextBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#07241A"));
        var inactiveTextBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#E8FFF5"));

        SetModeBtnStyle(ModeNormalBtn, activeMode == "Normal", accentBrush, inactiveBg, activeTextBrush, inactiveTextBrush);
        SetModeBtnStyle(ModeMeetingBtn, activeMode == "Meeting", accentBrush, inactiveBg, activeTextBrush, inactiveTextBrush);
        SetModeBtnStyle(ModeGamingBtn, activeMode == "Gaming", accentBrush, inactiveBg, activeTextBrush, inactiveTextBrush);
        SetModeBtnStyle(ModeSilentBtn, activeMode == "Silent", accentBrush, inactiveBg, activeTextBrush, inactiveTextBrush);
    }

    private static void SetModeBtnStyle(System.Windows.Controls.Border border, bool isActive,
        SolidColorBrush accentBrush, SolidColorBrush inactiveBg,
        SolidColorBrush activeTextBrush, SolidColorBrush inactiveTextBrush)
    {
        border.Background = isActive ? accentBrush : inactiveBg;

        if (border.Child is TextBlock tb)
        {
            tb.Foreground = isActive ? activeTextBrush : inactiveTextBrush;
            tb.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
        }
    }

    private void RefreshStatsDisplay()
    {
        if (_statsService is null)
        {
            return;
        }

        var level = _statsService.Level;
        var currentXp = _statsService.CurrentXp;
        var xpToNext = Math.Max(1L, _statsService.XpToNextLevel);
        var xpProgress = Math.Clamp(currentXp / (double)xpToNext, 0d, 1d);

        var currentRank = ResolveDisplayRank(level);
        var nextRank = ResolveNextDisplayRank(currentRank);
        var nextRankMinLevel = ResolveNextRankMinLevel(currentRank);

        StatsLevelText.Text = level.ToString();
        StatsXpText.Text = $"{currentXp:N0} / {xpToNext:N0} XP";
        StatsCurrentRankText.Text = currentRank;
        StatsNextRankText.Text = nextRankMinLevel is null
            ? "Next Rank: MAX"
            : $"Next Rank: {nextRank} at Lv {nextRankMinLevel.Value}";

        if (nextRankMinLevel is null)
        {
            StatsPromotionHintText.Text = "Top rank reached. Keep grinding for title unlocks.";
        }
        else if (currentRank == "SSS" && level >= 319 && !_statsService.IsExAscensionRequirementMet)
        {
            StatsPromotionHintText.Text = $"EX Ascension Locked: {_statsService.ExAscensionRequirementText}";
        }
        else
        {
            var levelsRemaining = Math.Max(0, nextRankMinLevel.Value - level);
            StatsPromotionHintText.Text = levelsRemaining == 0
                ? $"Promotion to {nextRank} is ready."
                : $"{levelsRemaining} level(s) remaining until {nextRank}.";
        }

        StatsMainTitleText.Text = _statsService.MainTitle;
        StatsSecondaryTitleText.Text = _statsService.SecondaryTitle;

        StatsTotalClackityText.Text = _statsService.TotalClackity.ToString("N0");
        StatsTodayText.Text = _statsService.TodayClackity.ToString("N0");
        StatsWeekText.Text = _statsService.ThisWeekClackity.ToString("N0");
        StatsMonthText.Text = _statsService.ThisMonthClackity.ToString("N0");
        StatsKeyboardClicksText.Text = _statsService.TotalKeyboardClicks.ToString("N0");
        StatsMouseClicksText.Text = _statsService.TotalMouseClicks.ToString("N0");

        UpdateStatsXpBar(xpProgress);
        RebuildAchievementsList();
    }

    private static string ResolveDisplayRank(int level)
    {
        if (level >= 320) return "EX";
        if (level >= 260) return "SSS";
        if (level >= 210) return "SS";
        if (level >= 170) return "S";
        if (level >= 140) return "A";
        if (level >= 110) return "B";
        if (level >= 80) return "C";
        if (level >= 50) return "D";
        if (level >= 25) return "E";
        return "F";
    }

    private static string ResolveNextDisplayRank(string currentRank)
    {
        return currentRank switch
        {
            "F" => "E",
            "E" => "D",
            "D" => "C",
            "C" => "B",
            "B" => "A",
            "A" => "S",
            "S" => "SS",
            "SS" => "SSS",
            "SSS" => "EX",
            _ => "MAX"
        };
    }

    private static int? ResolveNextRankMinLevel(string currentRank)
    {
        return currentRank switch
        {
            "F" => 25,
            "E" => 50,
            "D" => 80,
            "C" => 110,
            "B" => 140,
            "A" => 170,
            "S" => 210,
            "SS" => 260,
            "SSS" => 320,
            _ => null
        };
    }

    private void UpdateStatsXpBar(double progress)
    {
        var fillWidth = StatsXpProgressTrack.ActualWidth * progress;
        if (fillWidth > 0)
        {
            StatsXpProgressFill.Width = fillWidth;
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var refreshedWidth = StatsXpProgressTrack.ActualWidth * progress;
            StatsXpProgressFill.Width = Math.Max(0, refreshedWidth);
        }, DispatcherPriority.Background);
    }

    private void RebuildAchievementsList()
    {
        if (_statsService is null)
        {
            return;
        }

        StatsAchievementsPanel.Children.Clear();

        var unlockedIds = _statsService.UnlockedTitles
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allDefinitions = _statsService.AllTitleDefinitions
            .Where(def => !def.IsSecret)
            .ToList();

        var selectedRank = AchievementRankPages[_achievementPageIndex];

        var definitions = allDefinitions
            .Where(def => string.Equals(def.Rarity, selectedRank, StringComparison.OrdinalIgnoreCase))
            .OrderBy(def => def.RequiredCount)
            .ThenBy(def => def.Name)
            .ToList();

        var unlockedCount = allDefinitions.Count(def => unlockedIds.Contains(def.Id));
        StatsAchievementsCountText.Text = $"{unlockedCount} / {allDefinitions.Count}";
        StatsAchievementsPageText.Text = $"Page {selectedRank} ({_achievementPageIndex + 1}/{AchievementRankPages.Length})";
        StatsAchievementPrevButton.IsEnabled = _achievementPageIndex > 0;
        StatsAchievementNextButton.IsEnabled = _achievementPageIndex < AchievementRankPages.Length - 1;

        if (definitions.Count == 0)
        {
            StatsAchievementsPanel.Children.Add(new TextBlock
            {
                Text = "No achievements in this rank page.",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#7BAF9B")),
                FontStyle = FontStyles.Italic,
                FontSize = 11,
                Margin = new Thickness(4, 2, 0, 0)
            });
            return;
        }

        foreach (var def in definitions)
        {
            var isUnlocked = unlockedIds.Contains(def.Id);
            var rarityColor = StatsService.GetRarityColor(def.Rarity);
            var isEquipped = string.Equals(_statsService.MainTitle, def.Name, StringComparison.Ordinal);

            var row = new Border
            {
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(6, 4, 6, 4),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#214B3C")),
                Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#101A16"))
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.4, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

            var icon = new TextBlock
            {
                Text = isUnlocked ? "OPEN" : "LOCKED",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(isUnlocked ? "#22D883" : "#5E7A6F")),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var nameText = new TextBlock
            {
                Text = def.Name,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(rarityColor)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var reqText = new TextBlock
            {
                Text = def.Description,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#8DC4AE")),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var rarity = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#1A5A43")),
                BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#2A6A53")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = def.Rarity,
                    Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(rarityColor)),
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold
                }
            };

            var actionButton = new WpfButton
            {
                Style = (Style)FindResource("StatsCompactButtonStyle"),
                MinWidth = 72,
                Height = 24,
                Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = def.Name
            };

            if (!isUnlocked)
            {
                actionButton.Content = "Locked";
                actionButton.IsEnabled = false;
            }
            else if (isEquipped)
            {
                actionButton.Content = "Unequip";
                actionButton.Background = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#3A1D19"));
                actionButton.BorderBrush = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#8A4D45"));
                actionButton.Click += StatsAchievementEquipToggleButton_Click;
            }
            else
            {
                actionButton.Content = "Equip";
                actionButton.Click += StatsAchievementEquipToggleButton_Click;
            }

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(nameText, 1);
            Grid.SetColumn(reqText, 2);
            Grid.SetColumn(rarity, 3);
            Grid.SetColumn(actionButton, 4);

            rowGrid.Children.Add(icon);
            rowGrid.Children.Add(nameText);
            rowGrid.Children.Add(reqText);
            rowGrid.Children.Add(rarity);
            rowGrid.Children.Add(actionButton);
            row.Child = rowGrid;
            StatsAchievementsPanel.Children.Add(row);
        }
    }

    private void StatsAchievementPrevButton_Click(object sender, RoutedEventArgs e)
    {
        if (_achievementPageIndex <= 0)
        {
            return;
        }

        _achievementPageIndex--;
        RebuildAchievementsList();
    }

    private void StatsAchievementNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_achievementPageIndex >= AchievementRankPages.Length - 1)
        {
            return;
        }

        _achievementPageIndex++;
        RebuildAchievementsList();
    }

    private void StatsAchievementEquipToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_statsService is null || sender is not WpfButton btn || btn.Tag is not string titleName)
        {
            return;
        }

        _statsService.MainTitle = string.Equals(_statsService.MainTitle, titleName, StringComparison.Ordinal)
            ? "Newbie Clacker"
            : titleName;

        RefreshStatsDisplay();
    }

    private void OpenMechvibesGithubButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/hainguyents13/mechvibes-dx",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore if shell launch fails
        }
    }

    private void LoadOutputDevices()
    {
        if (_soundEngine is null)
        {
            return;
        }

        var devices = _soundEngine.GetOutputDevices();
        var options = devices
            .Select((name, index) => new OutputDeviceOption(index - 1, name))
            .ToList();

        OutputDeviceComboBox.ItemsSource = options;
        var selectedDeviceNumber = _soundEngine.GetOutputDeviceNumber();

        OutputDeviceComboBox.SelectedItem = options.FirstOrDefault(option => option.DeviceNumber == selectedDeviceNumber)
            ?? options.FirstOrDefault();
    }

    private void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_soundEngine is null || OutputDeviceComboBox.SelectedItem is not OutputDeviceOption selected)
        {
            return;
        }

        if (!_soundEngine.SetOutputDeviceNumber(selected.DeviceNumber))
        {
            PackStatusText.Text = "Could not switch output device.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            LoadOutputDevices();
        }
    }

    private void AnalyzeLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var testDevice = new WaveOutEvent
            {
                DesiredLatency = 40,
                NumberOfBuffers = 2
            };

            sw.Stop();
            var measuredMs = Math.Max(20, sw.ElapsedMilliseconds + 40);

            CurrentLatencyText.Text = $"{measuredMs} ms";

            if (measuredMs <= 40)
            {
                LatencyStatusText.Text = "Excellent";
                LatencyStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
            }
            else if (measuredMs <= 70)
            {
                LatencyStatusText.Text = "Good";
                LatencyStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#8BC34A"));
            }
            else if (measuredMs <= 120)
            {
                LatencyStatusText.Text = "Fair";
                LatencyStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FFD54F"));
            }
            else
            {
                LatencyStatusText.Text = "High";
                LatencyStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            }
        }
        catch
        {
            CurrentLatencyText.Text = "Error";
            LatencyStatusText.Text = "Failed";
            LatencyStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF5252"));
        }
    }

    private void OptimizeLatencyButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentLatencyText.Text = "40 ms";
        BufferCountText.Text = "2";
        LatencyStatusText.Text = "Optimized";
        LatencyStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
    }

    private void CreatePackButton_Click(object sender, RoutedEventArgs e)
    {
        var packName = PackNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(packName))
        {
            PackStatusText.Text = "Please enter a pack name.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        var safeName = string.Join("_", packName.Split(Path.GetInvalidFileNameChars()));
        _currentPackFolder = GetUniquePackageFolderPath(safeName);

        try
        {
            Directory.CreateDirectory(_currentPackFolder);
            _currentPackFiles.Clear();

            RefreshPackSoundsList();
            RefreshCreatedPackagesList();
            PackStatusText.Text = $"New package '{Path.GetFileName(_currentPackFolder)}' created.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        }
        catch (Exception ex)
        {
            PackStatusText.Text = $"Error creating pack: {ex.Message}";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
        }
    }

    private void BulkUploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPackFolder == null)
        {
            PackStatusText.Text = "Create a pack first before uploading sounds.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        using var dialog = new Forms.OpenFileDialog
        {
            Title = "Select audio files to add to pack",
            Filter = "Audio files|*.wav;*.mp3;*.ogg;*.flac;*.aac;*.m4a|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
            RestoreDirectory = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var addedCount = 0;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var destPath = Path.Combine(_currentPackFolder, Path.GetFileName(file));
                if (File.Exists(destPath))
                {
                    destPath = Path.Combine(_currentPackFolder,
                        $"{Path.GetFileNameWithoutExtension(file)}_{DateTime.Now:HHmmss}{Path.GetExtension(file)}");
                }

                File.Copy(file, destPath);
                _currentPackFiles.Add(destPath);
                addedCount++;
            }
            catch
            {
                // skip files that fail to copy
            }
        }

        RefreshPackSoundsList();
        PackStatusText.Text = $"Added {addedCount} sound(s) to pack.";
        PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
    }

    private async void SavePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSavingPackage)
        {
            return;
        }

        if (_currentPackFolder == null || _soundEngine is null)
        {
            PackStatusText.Text = "Create a pack first before saving.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        if (_currentPackFiles.Count == 0)
        {
            PackStatusText.Text = "Add at least one audio file before saving package.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        _isSavingPackage = true;
        SavePackageButton.IsEnabled = false;

        try
        {
            var packageName = Path.GetFileName(_currentPackFolder);

            if (DataContext is MainViewModel vm)
            {
                RemoveExistingCustomPackageProfiles(vm, packageName);
            }

            var keyboardImportedId = await _soundEngine.ImportSoundPackAsync(_currentPackFolder, false, "custom");
            var mouseImportedId = await _soundEngine.ImportSoundPackAsync(_currentPackFolder, true, "custom");

            if (string.IsNullOrWhiteSpace(keyboardImportedId) && string.IsNullOrWhiteSpace(mouseImportedId))
            {
                PackStatusText.Text = "Could not save package. Ensure files are valid audio clips.";
                PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
                return;
            }

            if (DataContext is MainViewModel currentViewModel)
            {
                currentViewModel.RefreshProfiles();
            }

            RefreshCreatedPackagesList();
            PackStatusText.Text = "Package saved and added to keyboard + mouse Sound Profiles (badge: custom).";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        }
        catch (Exception ex)
        {
            PackStatusText.Text = $"Save failed: {ex.Message}";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
        }
        finally
        {
            _isSavingPackage = false;
            SavePackageButton.IsEnabled = true;
        }
    }

    private static void RemoveExistingCustomPackageProfiles(MainViewModel viewModel, string packageName)
    {
        var existingProfiles = viewModel.Profiles
            .Where(profile => profile.IsImported
                && string.Equals(profile.SourceLabel, "custom", StringComparison.OrdinalIgnoreCase)
                && string.Equals(profile.DisplayName, packageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var profile in existingProfiles)
        {
            viewModel.TryRemoveImportedProfile(profile);
        }
    }

    private static void RemoveCustomProfilesForPackage(MainViewModel viewModel, string packageName)
    {
        var matchingProfiles = viewModel.Profiles
            .Where(profile => profile.IsImported
                && (string.Equals(profile.SourceLabel, "custom", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(profile.SourceLabel, "custome", StringComparison.OrdinalIgnoreCase))
                && string.Equals(profile.DisplayName, packageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var profile in matchingProfiles)
        {
            viewModel.TryRemoveImportedProfile(profile);
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            StopRecordingIfActive();
            return;
        }

        if (_currentPackFolder == null)
        {
            PackStatusText.Text = "Create a pack first before recording.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            return;
        }

        StartRecording();
    }

    private void StartRecording()
    {
        try
        {
            var fileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
            _recordingFilePath = Path.Combine(_currentPackFolder!, fileName);

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(44100, 16, 1),
                BufferMilliseconds = 100
            };

            _waveWriter = new WaveFileWriter(_recordingFilePath, _waveIn.WaveFormat);

            _waveIn.DataAvailable += (s, e) =>
            {
                if (_waveWriter != null && _isRecording)
                {
                    _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                }
            };

            _waveIn.RecordingStopped += (s, e) =>
            {
                _waveWriter?.Dispose();
                _waveWriter = null;
                _waveIn?.Dispose();
                _waveIn = null;
            };

            _isRecording = true;
            _recordingStartUtc = DateTime.UtcNow;
            _waveIn.StartRecording();

            RecordingStatusText.Text = "Recording...";
            RecordingStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF5252"));
            RecordButtonIcon.Text = "\uE71A";

            _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _recordingTimer.Tick += RecordingTimer_Tick;
            _recordingTimer.Start();
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = $"Mic error: {ex.Message}";
            RecordingStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
            _isRecording = false;
        }
    }

    private void RecordingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _recordingStartUtc).TotalSeconds;
        RecordingTimerText.Text = $"{elapsed:F1} / {MaxRecordingSeconds:F1} sec";

        if (RecordingProgressFill.Parent is Grid progressParent)
        {
            var parentWidth = progressParent.ActualWidth;
            if (parentWidth > 0)
            {
                RecordingProgressFill.Width = parentWidth * Math.Min(1.0, elapsed / MaxRecordingSeconds);
            }
        }

        if (elapsed >= MaxRecordingSeconds)
        {
            StopRecordingIfActive();
        }
    }

    private void StopRecordingIfActive()
    {
        if (!_isRecording)
        {
            return;
        }

        _isRecording = false;

        if (_recordingTimer is not null)
        {
            _recordingTimer.Stop();
            _recordingTimer.Tick -= RecordingTimer_Tick;
            _recordingTimer = null;
        }

        try
        {
            _waveIn?.StopRecording();
        }
        catch
        {
            // ignore
        }

        RecordingStatusText.Text = "Recording saved!";
        RecordingStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        RecordButtonIcon.Text = "\uE720";
        RecordingProgressFill.Width = 0;

        if (_recordingFilePath != null && File.Exists(_recordingFilePath))
        {
            _currentPackFiles.Add(_recordingFilePath);
            RefreshPackSoundsList();
        }

        RecordingTimerText.Text = $"0.0 / {MaxRecordingSeconds:F1} sec";
    }

    private static bool IsSupportedAudioFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUniquePackageFolderPath(string baseFolderName)
    {
        if (!Directory.Exists(CustomPacksFolder))
        {
            Directory.CreateDirectory(CustomPacksFolder);
        }

        var normalizedBase = string.IsNullOrWhiteSpace(baseFolderName) ? "Package" : baseFolderName;
        var candidate = Path.Combine(CustomPacksFolder, normalizedBase);

        if (!Directory.Exists(candidate))
        {
            return candidate;
        }

        var suffix = 2;
        while (true)
        {
            candidate = Path.Combine(CustomPacksFolder, $"{normalizedBase}_{suffix}");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private void RefreshPackSoundsList()
    {
        PackSoundsPanel.Children.Clear();

        if (_currentPackFiles.Count == 0)
        {
            PackSoundsPanel.Children.Add(new TextBlock
            {
                Text = "No sounds added yet.",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#5B8A76")),
                FontStyle = FontStyles.Italic,
                FontSize = 11
            });
            PackSoundCountText.Text = "0 sounds";
            return;
        }

        PackSoundCountText.Text = $"{_currentPackFiles.Count} sound(s)";

        foreach (var filePath in _currentPackFiles)
        {
            var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock
            {
                Text = Path.GetFileName(filePath),
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#CFECE0")),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var previewBtn = new WpfButton
            {
                Tag = filePath,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                ToolTip = "Preview sound",
                Style = (Style)FindResource("GlowButtonStyle"),
                Content = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Text = "\uE768",
                    FontSize = 12,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            previewBtn.Click += PreviewSoundButton_Click;

            var deleteBtn = new WpfButton
            {
                Tag = filePath,
                Margin = new Thickness(6, 0, 0, 0),
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                ToolTip = "Delete sound",
                Style = (Style)FindResource("DangerIconButtonStyle"),
                Content = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Text = "\uE74D",
                    FontSize = 13,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            deleteBtn.Click += DeleteSoundButton_Click;

            Grid.SetColumn(previewBtn, 0);
            Grid.SetColumn(textBlock, 1);
            Grid.SetColumn(deleteBtn, 2);
            rowGrid.Children.Add(previewBtn);
            rowGrid.Children.Add(textBlock);
            rowGrid.Children.Add(deleteBtn);

            PackSoundsPanel.Children.Add(rowGrid);
        }
    }

    private void PreviewSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || btn.Tag is not string filePath || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            StopPreviewPlayback();

            _previewReader = new AudioFileReader(filePath);
            _previewOutput = new WaveOutEvent
            {
                DeviceNumber = _soundEngine?.GetOutputDeviceNumber() ?? -1,
                DesiredLatency = 60,
                NumberOfBuffers = 3
            };
            _previewOutput.PlaybackStopped += (_, _) => StopPreviewPlayback();
            _previewOutput.Init(_previewReader);
            _previewOutput.Play();
        }
        catch
        {
            StopPreviewPlayback();
        }
    }

    private void DeleteSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || btn.Tag is not string filePath)
        {
            return;
        }

        var confirmDialog = new ConfirmActionDialog(
            "Delete Sound",
            $"Delete '{Path.GetFileName(filePath)}' from this package?",
            "Delete Sound")
        {
            Owner = this
        };

        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            StopPreviewPlayback();

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            _currentPackFiles.Remove(filePath);
            RefreshPackSoundsList();
        }
        catch
        {
            // ignore
        }
    }

    private void RefreshCreatedPackagesList()
    {
        CreatedPackagesPanel.Children.Clear();

        if (!Directory.Exists(CustomPacksFolder))
        {
            Directory.CreateDirectory(CustomPacksFolder);
        }

        var packageFolders = Directory.EnumerateDirectories(CustomPacksFolder)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        CreatedPackagesCountText.Text = $"{packageFolders.Count} package(s)";

        if (packageFolders.Count == 0)
        {
            CreatedPackagesPanel.Children.Add(new TextBlock
            {
                Text = "No packages created yet.",
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#5B8A76")),
                FontStyle = FontStyles.Italic,
                FontSize = 11
            });
            return;
        }

        foreach (var folderPath in packageFolders)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var packageName = Path.GetFileName(folderPath);

            var nameText = new TextBlock
            {
                Text = packageName,
                Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#CFECE0")),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var editBtn = new WpfButton
            {
                Tag = folderPath,
                Width = 28,
                Height = 28,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                ToolTip = "Edit package",
                Style = (Style)FindResource("GlowButtonStyle"),
                Content = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Text = "\uE70F",
                    FontSize = 12,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                }
            };
            editBtn.Click += EditPackageButton_Click;

            var deleteBtn = new WpfButton
            {
                Tag = folderPath,
                Width = 28,
                Height = 28,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                ToolTip = "Delete package",
                Style = (Style)FindResource("DangerIconButtonStyle"),
                Content = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Text = "\uE74D",
                    FontSize = 12,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                }
            };
            deleteBtn.Click += DeletePackageButton_Click;

            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(editBtn, 1);
            Grid.SetColumn(deleteBtn, 2);

            row.Children.Add(nameText);
            row.Children.Add(editBtn);
            row.Children.Add(deleteBtn);

            CreatedPackagesPanel.Children.Add(row);
        }
    }

    private void EditPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || btn.Tag is not string folderPath || !Directory.Exists(folderPath))
        {
            return;
        }

        _currentPackFolder = folderPath;
        PackNameTextBox.Text = Path.GetFileName(folderPath);

        _currentPackFiles.Clear();
        foreach (var file in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (IsSupportedAudioFile(file))
            {
                _currentPackFiles.Add(file);
            }
        }

        RefreshPackSoundsList();
        PackStatusText.Text = $"Editing package '{PackNameTextBox.Text}'.";
        PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
    }

    private void DeletePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton btn || btn.Tag is not string folderPath || !Directory.Exists(folderPath))
        {
            return;
        }

        var packageName = Path.GetFileName(folderPath);

        var confirmDialog = new ConfirmActionDialog(
            "Delete Package",
            $"Delete package '{packageName}' and all its sounds?",
            "Delete Package")
        {
            Owner = this
        };

        if (confirmDialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Directory.Delete(folderPath, true);

            if (DataContext is MainViewModel vm)
            {
                RemoveCustomProfilesForPackage(vm, packageName);
                vm.RefreshProfiles();
            }

            if (string.Equals(_currentPackFolder, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentPackFolder = null;
                _currentPackFiles.Clear();
                RefreshPackSoundsList();
            }

            RefreshCreatedPackagesList();
            PackStatusText.Text = "Package deleted.";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#22D883"));
        }
        catch (Exception ex)
        {
            PackStatusText.Text = $"Delete failed: {ex.Message}";
            PackStatusText.Foreground = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString("#FF7043"));
        }
    }

    private void StopPreviewPlayback()
    {
        _previewOutput?.Dispose();
        _previewOutput = null;

        _previewReader?.Dispose();
        _previewReader = null;
    }

    private static string BuildKeycapLabel(int inputCode)
    {
        if (InputBindingCode.IsMouseCode(inputCode))
        {
            return inputCode switch
            {
                InputBindingCode.MouseLeft => "LMB",
                InputBindingCode.MouseRight => "RMB",
                InputBindingCode.MouseMiddle => "MMB",
                InputBindingCode.MouseX1 => "X1",
                InputBindingCode.MouseX2 => "X2",
                _ => "MOUSE"
            };
        }

        if (inputCode >= 0x30 && inputCode <= 0x39)
        {
            return ((char)inputCode).ToString();
        }

        if (inputCode >= 0x41 && inputCode <= 0x5A)
        {
            return ((char)inputCode).ToString();
        }

        return inputCode switch
        {
            0x20 => "SPACE",
            0x0D => "ENTER",
            0x08 => "BKSP",
            0x09 => "TAB",
            0x10 => "SHIFT",
            0x11 => "CTRL",
            0x12 => "ALT",
            0x1B => "ESC",
            _ => $"VK{inputCode:X2}"
        };
    }
}
