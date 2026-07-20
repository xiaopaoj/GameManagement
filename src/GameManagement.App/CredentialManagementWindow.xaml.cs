using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class CredentialManagementWindow : Window
{
    private readonly GameItem _game;
    private readonly AppState _state;
    private readonly Action<string> _save;

    public CredentialManagementWindow(GameItem game, AppState state, Action<string> save)
    {
        InitializeComponent(); _game = game; _state = state; _save = save;
        GameNameText.Text = game.DisplayName; RefreshItems();
    }

    private CredentialDisplayItem? Selected => CredentialGrid.SelectedItem as CredentialDisplayItem;

    private void RefreshItems(Guid? selectedId = null)
    {
        var versionIds = _game.Versions.Select(version => version.Id).ToHashSet();
        var items = _state.Credentials.Where(item => versionIds.Contains(item.GameVersionId))
            .Select(item => new CredentialDisplayItem(item, _game.Versions.FirstOrDefault(version => version.Id == item.GameVersionId)?.VersionName ?? "未知版本"))
            .OrderBy(item => item.VersionName).ThenBy(item => item.Credential.StepOrder).ToList();
        CredentialGrid.ItemsSource = items;
        CredentialGrid.SelectedItem = items.FirstOrDefault(item => item.Credential.Id == selectedId) ?? items.FirstOrDefault();
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        try
        {
            var password = CredentialService.Decrypt(Selected.Credential.EncryptedPassword, Selected.Credential.GameVersionId);
            MessageBox.Show(password.Length == 0 ? "该压缩文件记录为无密码。" : $"保存的密码：\n\n{password}", "查看解压密码", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError($"密码解密失败：{ex.Message}"); }
    }

    private void Modify_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        string current;
        try { current = CredentialService.Decrypt(Selected.Credential.EncryptedPassword, Selected.Credential.GameVersionId); }
        catch (Exception ex) { ShowError($"密码解密失败：{ex.Message}"); return; }
        var dialog = new PasswordInputWindow("修改解压密码", Selected.ArchiveName, current, CredentialService.GetPasswordHistory(_state)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        CredentialService.AddPasswordHistory(_state, dialog.Password);
        CredentialService.SavePassword(_state, Selected.Credential.GameVersionId, Selected.Credential.ArchiveFingerprint, dialog.Password, Selected.Credential.StepOrder, Selected.Credential.ArchiveDisplayName, Selected.Credential.ArchiveRelativePath, null);
        _save("解压密码已修改，等待下次使用或手动重新验证"); RefreshItems(Selected.Credential.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (MessageBox.Show($"确定删除“{Selected.ArchiveName}”保存的解压密码？\n删除后下次解压需要重新输入。", "删除密码确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        CredentialService.DeletePassword(_state, Selected.Credential.Id); _save("保存的解压密码已删除"); RefreshItems();
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        var archivePath = ResolveArchivePath(Selected.Credential);
        if (archivePath is null)
        {
            MessageBox.Show("当前无法定位该压缩文件。第二次解压文件通常只存在于准备任务临时目录中；如临时目录已清理，将在下次准备游玩实际解压成功后自动更新验证时间。", "无法立即验证", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var progress = new PreparationProgressWindow("正在验证解压密码") { Owner = this };
        progress.Show(); IsEnabled = false;
        try
        {
            var password = CredentialService.Decrypt(Selected.Credential.EncryptedPassword, Selected.Credential.GameVersionId);
            await ArchiveExtractionService.ValidatePasswordAsync(archivePath, password);
            Selected.Credential.VerifiedAt = DateTime.Now; Selected.Credential.UpdatedAt = DateTime.Now; _save("解压密码重新验证成功");
            MessageBox.Show("密码验证成功。", "重新验证", MessageBoxButton.OK, MessageBoxImage.Information); RefreshItems(Selected.Credential.Id);
        }
        catch (Exception ex) { AppLogger.Error("重新验证解压密码失败", ex); ShowError($"密码验证失败：{ex.Message}"); }
        finally { IsEnabled = true; progress.CloseSafely(); }
    }

    private string? ResolveArchivePath(ArchiveCredentialItem credential)
    {
        var version = _game.Versions.FirstOrDefault(item => item.Id == credential.GameVersionId);
        if (version is null) return null;
        if (credential.StepOrder == 1)
        {
            if (File.Exists(version.SourcePath)) return version.SourcePath;
            if (Directory.Exists(version.SourcePath))
            {
                var path = Path.GetFullPath(Path.Combine(version.SourcePath, credential.ArchiveRelativePath));
                if (File.Exists(path)) return path;
            }
        }
        foreach (var task in _state.OperationTasks.Where(task => task.GameVersionId == version.Id && !string.IsNullOrWhiteSpace(task.WorkingDirectory)).OrderByDescending(task => task.StartedAt))
        {
            var baseDirectory = credential.StepOrder == 1
                ? File.Exists(version.SourcePath) ? Path.Combine(task.WorkingDirectory, "source") : Path.Combine(task.WorkingDirectory, "source", new DirectoryInfo(version.SourcePath).Name)
                : Path.Combine(task.WorkingDirectory, "step1");
            var path = Path.GetFullPath(Path.Combine(baseDirectory, credential.ArchiveRelativePath));
            if (File.Exists(path)) return path;
            if (credential.StepOrder == 2 && version.SecondArchiveUsedFallback && !string.IsNullOrWhiteSpace(version.SecondArchiveFormat))
            {
                var extension = version.SecondArchiveFormat.ToUpperInvariant() switch { "ZIP" => ".zip", "7Z" => ".7z", _ => ".rar" };
                var renamed = Path.ChangeExtension(path, extension);
                if (File.Exists(renamed)) return renamed;
            }
        }
        return null;
    }

    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}

public sealed class CredentialDisplayItem(ArchiveCredentialItem credential, string versionName)
{
    public ArchiveCredentialItem Credential { get; } = credential;
    public string VersionName { get; } = versionName;
    public string StepName => Credential.StepOrder switch { 1 => "第一次解压", 2 => "第二次解压", _ => "历史记录" };
    public string ArchiveName => string.IsNullOrWhiteSpace(Credential.ArchiveDisplayName) ? Credential.ArchiveFingerprint[..Math.Min(12, Credential.ArchiveFingerprint.Length)] : Credential.ArchiveDisplayName;
    public string RelativePath => Credential.ArchiveRelativePath;
    public DateTime? VerifiedAt => Credential.VerifiedAt;
    public DateTime UpdatedAt => Credential.UpdatedAt;
}
