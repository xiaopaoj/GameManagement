using System.Windows;
using GameManagement.Services;

namespace GameManagement;

public partial class SecurityModeSettingsWindow : Window
{
    public bool RequiresRestart { get; private set; }
    public SecurityModeSettingsWindow() { InitializeComponent(); Refresh(); }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile)) { ShowError("安全密码模式已经开启。"); return; }
        if (RiskCheck.IsChecked != true) { ShowError("请先确认密码遗忘风险。"); return; }
        if (!PasswordsMatch()) return;
        try { MasterKeyService.EnablePassword(AppPaths.SecurityConfigFile, NewPasswordText.Password); RequiresRestart = true; MessageBox.Show(this, "安全模式已经开启。软件将退出，重新启动后需要输入安全密码。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information); DialogResult = true; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        if (!MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile)) { ShowError("安全密码模式尚未开启。"); return; }
        if (!PasswordsMatch()) return;
        try { MasterKeyService.ChangePassword(AppPaths.SecurityConfigFile, CurrentPasswordText.Password, NewPasswordText.Password); Clear(); MessageBox.Show(this, "安全密码已经修改。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (!MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile)) { ShowError("安全密码模式尚未开启。"); return; }
        try { MasterKeyService.DisablePassword(AppPaths.SecurityConfigFile, CurrentPasswordText.Password); RequiresRestart = true; MessageBox.Show(this, "安全模式已经关闭，数据库仍保持密文。软件将重新启动到普通界面。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information); DialogResult = true; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private bool PasswordsMatch()
    {
        if (NewPasswordText.Password != ConfirmPasswordText.Password) { ShowError("两次输入的新密码不一致。"); return false; }
        return true;
    }
    private void Refresh() => ModeStatusText.Text = MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile) ? "安全密码模式：已开启" : "安全密码模式：未开启";
    private void Clear() { CurrentPasswordText.Clear(); NewPasswordText.Clear(); ConfirmPasswordText.Clear(); }
    private void ShowError(string message) { Clear(); MessageBox.Show(this, message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
}
