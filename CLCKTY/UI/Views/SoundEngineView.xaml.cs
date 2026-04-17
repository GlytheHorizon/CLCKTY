using System.Windows;
using System.Windows.Controls;
using CLCKTY.UI.ViewModels;

namespace CLCKTY.UI.Views;

public partial class SoundEngineView : System.Windows.Controls.UserControl
{
    public SoundEngineView()
    {
        InitializeComponent();
    }

    private async void DeleteSelectedAsset_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.DeleteSelectedAssetAsync();
    }
}
