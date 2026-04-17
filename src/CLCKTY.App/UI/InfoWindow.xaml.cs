using System.Windows;
using System.Windows.Input;
using System.Diagnostics;

namespace CLCKTY.App.UI;

public partial class InfoWindow : Window
{
    public InfoWindow()
    {
        InitializeComponent();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
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
}
