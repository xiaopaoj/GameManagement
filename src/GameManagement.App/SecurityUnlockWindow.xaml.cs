using System.Windows;
using System.Windows.Input;
using GameManagement.Services;

namespace GameManagement;

public partial class SecurityUnlockWindow : Window
{
    public SecurityUnlockWindow() { InitializeComponent(); Loaded += (_, _) => PasswordText.Focus(); }

    private void PasswordText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) TryUnlock(); }
    private void Unlock_Click(object sender, RoutedEventArgs e) => TryUnlock();

    private void TryUnlock()
    {
        if (MasterKeyService.TryUnlock(AppPaths.SecurityConfigFile, PasswordText.Password)) { PasswordText.Clear(); DialogResult = true; return; }
        PasswordText.Clear(); StatusText.Text = "安全密码错误。"; PasswordText.Focus();
    }
}
