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
        InitializeVisualizerKeycap();
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
            AnimateInputKeycap(e);
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

        VisualizerKeycapTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, keycapMoveAnimation, HandoffBehavior.SnapshotAndReplace);
        VisualizerKeycapScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, keycapScaleAnimation, HandoffBehavior.SnapshotAndReplace);
        VisualizerKeycapScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, keycapScaleAnimation, HandoffBehavior.SnapshotAndReplace);
        VisualizerKeycapShadow.BeginAnimation(OpacityProperty, shadowOpacityAnimation, HandoffBehavior.SnapshotAndReplace);
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
