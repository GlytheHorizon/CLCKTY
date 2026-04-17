using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.IO;
using System.Windows.Media.Imaging;

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
        // set window icon from optional Assets/Icons/clckty-app.png
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var pngPath = Path.Combine(baseDir, "Assets", "Icons", "clckty-app.png");
            if (File.Exists(pngPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(pngPath, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                Icon = bmp;
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
