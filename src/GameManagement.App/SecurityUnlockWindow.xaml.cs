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
        var remaining = MasterKeyService.GetRemainingRetryDelay(AppPaths.SecurityConfigFile);
        if (remaining > TimeSpan.Zero) { StatusText.Text = $"验证失败次数过多，请在 {Math.Ceiling(remaining.TotalSeconds)} 秒后重试。"; return; }
        if (MasterKeyService.TryUnlock(AppPaths.SecurityConfigFile, PasswordText.Password)) { PasswordText.Clear(); DialogResult = true; return; }
        PasswordText.Clear(); remaining = MasterKeyService.GetRemainingRetryDelay(AppPaths.SecurityConfigFile);
        StatusText.Text = remaining > TimeSpan.Zero ? $"验证未通过，请在 {Math.Ceiling(remaining.TotalSeconds)} 秒后重试。" : "验证未通过。"; PasswordText.Focus();
    }
}
