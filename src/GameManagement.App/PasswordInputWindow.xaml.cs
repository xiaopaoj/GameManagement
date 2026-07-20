using System.Windows;

namespace GameManagement;

public partial class PasswordInputWindow : Window
{
    public string Password => NoPasswordCheck.IsChecked == true ? string.Empty : PasswordValue.Text;

    public PasswordInputWindow(string title, string archiveName, string? existingPassword = null, IReadOnlyList<string>? passwordHistory = null)
    {
        InitializeComponent(); Title = title; PromptText.Text = $"请输入压缩文件密码：\n{archiveName}";
        HistoryList.ItemsSource = passwordHistory ?? [];
        HistoryButton.IsEnabled = HistoryList.Items.Count > 0;
        if (existingPassword is not null) { PasswordValue.Text = existingPassword; NoPasswordCheck.IsChecked = existingPassword.Length == 0; }
        Loaded += (_, _) =>
        {
            HistoryPopup.IsOpen = HistoryList.Items.Count > 0;
            PasswordValue.Focus();
        };
    }

    private void NoPasswordCheck_Changed(object sender, RoutedEventArgs e) => PasswordValue.IsEnabled = NoPasswordCheck.IsChecked != true;
    private void HistoryButton_Click(object sender, RoutedEventArgs e) => HistoryPopup.IsOpen = !HistoryPopup.IsOpen;
    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is not string password) return;
        NoPasswordCheck.IsChecked = false;
        PasswordValue.Text = password;
        HistoryPopup.IsOpen = false;
        DialogResult = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (NoPasswordCheck.IsChecked != true && string.IsNullOrEmpty(PasswordValue.Text)) { MessageBox.Show("请输入密码，或者勾选“没有密码”。", "密码提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
