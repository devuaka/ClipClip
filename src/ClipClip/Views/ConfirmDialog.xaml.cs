using System.Windows;

namespace ClipClip.Views;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string body, string action)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        OkButton.Content = action;
    }

    public static bool Ask(string title, string body, string action)
    {
        var dialog = new ConfirmDialog(title, body, action) { Owner = App.Current.MainWindowOrNull };
        return dialog.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
