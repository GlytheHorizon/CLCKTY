using System.Windows;

namespace CLCKTY.App.UI;

public partial class ConfirmActionDialog : Window
{
    public ConfirmActionDialog(string title, string message, string confirmLabel = "Confirm")
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Confirm Action" : title;
        MessageText.Text = message;
        ConfirmButton.Content = string.IsNullOrWhiteSpace(confirmLabel) ? "Confirm" : confirmLabel;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
