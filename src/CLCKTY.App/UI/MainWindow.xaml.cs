using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CLCKTY.App.Core;
using CLCKTY.App.Services;

namespace CLCKTY.App.UI;

public partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;
    private InfoWindow? _infoWindow;
    private readonly Random _visualizerRandom = new();
    private readonly double[] _visualizerBaseHeights = { 24d, 40d, 64d, 80d, 48d, 56d, 30d, 22d, 44d, 68d };

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        NavigateToSection("Dashboard");
        viewModel.InputTriggered += ViewModel_InputTriggered;
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
        ResetVisualizerBars();
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
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow
            {
                Owner = this,
                DataContext = DataContext
            };
        }

        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
    }

    private void InfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_infoWindow is null || !_infoWindow.IsLoaded)
        {
            _infoWindow = new InfoWindow
            {
                Owner = this
            };
        }

        if (!_infoWindow.IsVisible)
        {
            _infoWindow.Show();
        }

        _infoWindow.Activate();
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

        var confirmDialog = new DeleteImportedPackDialog(profile.DisplayName)
        {
            Owner = this
        };

        if (confirmDialog.ShowDialog() == true)
        {
            viewModel.TryRemoveImportedProfile(profile);
        }

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

        SetNavButtonState(DashboardNavButton, section == "Dashboard");
        SetNavButtonState(SoundProfilesNavButton, section == "SoundProfiles");
        SetNavButtonState(KeyMappingsNavButton, section == "KeyMappings");
        SetNavButtonState(MouseSettingsNavButton, section == "MouseSettings");
    }

    private static void SetNavButtonState(System.Windows.Controls.Button button, bool isActive)
    {
        button.Tag = isActive ? "Active" : null;
        button.Foreground = isActive
            ? new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#22D883"))
            : new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9FD8C1"));
    }

    private void ViewModel_InputTriggered(object? sender, InputTriggeredPreviewEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            AnimateTestTypingPreview();
            PulseVisualizer(e.Intensity);
        });
    }

    private void AnimateTestTypingPreview()
    {
        if (TestTypingPreviewCard.RenderTransform is not System.Windows.Media.ScaleTransform scaleTransform)
        {
            scaleTransform = new System.Windows.Media.ScaleTransform(1d, 1d);
            TestTypingPreviewCard.RenderTransformOrigin = new System.Windows.Point(0.5d, 0.5d);
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

        scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scalePulse, HandoffBehavior.SnapshotAndReplace);
        scaleTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scalePulse, HandoffBehavior.SnapshotAndReplace);
        TestTypingPreviewCard.BeginAnimation(OpacityProperty, opacityPulse, HandoffBehavior.SnapshotAndReplace);
    }

    private System.Windows.Controls.Border[] GetVisualizerBars()
    {
        return new[]
        {
            VisualizerBar1,
            VisualizerBar2,
            VisualizerBar3,
            VisualizerBar4,
            VisualizerBar5,
            VisualizerBar6,
            VisualizerBar7,
            VisualizerBar8,
            VisualizerBar9,
            VisualizerBar10
        };
    }

    private void ResetVisualizerBars()
    {
        var bars = GetVisualizerBars();

        for (var i = 0; i < bars.Length; i++)
        {
            bars[i].Height = _visualizerBaseHeights[i];
            bars[i].Opacity = 0.55d;
        }
    }

    private void PulseVisualizer(double intensity)
    {
        var bars = GetVisualizerBars();
        var normalizedIntensity = Math.Clamp(intensity, 0.15d, 1.0d);

        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            var baseHeight = _visualizerBaseHeights[i];
            var randomBoost = _visualizerRandom.NextDouble() * 34d * normalizedIntensity;
            var waveBoost = (Math.Sin((Environment.TickCount / 85d) + i) + 1d) * 7d * normalizedIntensity;
            var peakHeight = Math.Max(12d, baseHeight + randomBoost + waveBoost);

            var pulse = new DoubleAnimation
            {
                To = peakHeight,
                Duration = TimeSpan.FromMilliseconds(95),
                AutoReverse = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var opacityPulse = new DoubleAnimation
            {
                To = 0.85d + (_visualizerRandom.NextDouble() * 0.15d),
                Duration = TimeSpan.FromMilliseconds(95),
                AutoReverse = true
            };

            bar.BeginAnimation(HeightProperty, pulse, HandoffBehavior.SnapshotAndReplace);
            bar.BeginAnimation(OpacityProperty, opacityPulse, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
