using System.Windows;

namespace GameManagement;

public partial class PasswordInputWindow : Window
{
    public string Password => NoPasswordCheck.IsChecked == true ? string.Empty : PasswordValue.Password;

    public PasswordInputWindow(string title, string archiveName, string? existingPassword = null)
    {
        InitializeComponent(); Title = title; PromptText.Text = $"请输入压缩文件密码：\n{archiveName}";
        if (existingPassword is not null) { PasswordValue.Password = existingPassword; NoPasswordCheck.IsChecked = existingPassword.Length == 0; }
        Loaded += (_, _) => PasswordValue.Focus();
    }

    private void NoPasswordCheck_Changed(object sender, RoutedEventArgs e) => PasswordValue.IsEnabled = NoPasswordCheck.IsChecked != true;
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (NoPasswordCheck.IsChecked != true && string.IsNullOrEmpty(PasswordValue.Password)) { MessageBox.Show("请输入密码，或者勾选“没有密码”。", "密码提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
