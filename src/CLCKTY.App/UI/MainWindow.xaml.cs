using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CLCKTY.App.Core;
using CLCKTY.App.Services;

namespace CLCKTY.App.UI;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
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
            || !viewModel.RemoveImportedProfileCommand.CanExecute(profile))
        {
            return;
        }

        viewModel.RemoveImportedProfileCommand.Execute(profile);
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
}
