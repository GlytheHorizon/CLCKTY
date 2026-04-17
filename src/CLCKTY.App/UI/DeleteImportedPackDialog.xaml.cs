using System.Windows;

namespace CLCKTY.App.UI;

public partial class DeleteImportedPackDialog : Window
{
    public DeleteImportedPackDialog(string packName)
    {
        InitializeComponent();
        MessageText.Text = $"Delete imported pack '{packName}'?";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
