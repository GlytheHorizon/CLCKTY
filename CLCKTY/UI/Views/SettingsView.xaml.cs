using System.Windows;
using System.Windows.Controls;
using CLCKTY.UI.ViewModels;
using Forms = System.Windows.Forms;

namespace CLCKTY.UI.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void ImportSoundPack_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a folder containing your sound pack audio files.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        await vm.ImportSoundPackAsync(dialog.SelectedPath);
    }

    private async void ResetConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            "Reset CLCKTY settings back to defaults?",
            "Reset Configuration",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await vm.ResetConfigurationAsync();
    }

    private async void RecordClip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.RecordClipAsync();
    }
}
