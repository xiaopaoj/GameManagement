using System.Diagnostics;
using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class BackupManagementWindow : Window
{
    private readonly AppState _state;
    private readonly Action<string> _save;
    private readonly string _filter;
    private ExternalBackupItem? Selected => BackupGrid.SelectedItem as ExternalBackupItem;

    public BackupManagementWindow(AppState state, Action<string> save, string filter = "全部")
    {
        InitializeComponent(); _state = state; _save = save; _filter = filter; RefreshList();
    }

    private void RefreshList()
    {
        var items = _state.ExternalBackups.AsEnumerable();
        if (_filter == "建议清理") items = items.Where(item => item.CleanupSuggested);
        BackupGrid.ItemsSource = items.OrderByDescending(item => item.CreatedAt).ToList();
        StatusText.Text = $"共 {_state.ExternalBackups.Count} 个｜建议清理 {_state.ExternalBackups.Count(item => item.CleanupSuggested)} 个｜筛选：{_filter}";
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        try { new BackupDetailWindow(Selected) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var backup = Selected;
        if (backup is null) return;
        IsEnabled = false;
        try
        {
            backup.Verified = await ExternalBackupService.VerifyAsync(backup);
            backup.VerifiedAt = backup.Verified ? DateTime.Now : null;
            backup.Status = backup.Verified ? "已完成" : "校验失败";
            _save(backup.Verified ? "外部备份重新校验成功" : "外部备份重新校验失败");
            WindowInteractionService.Restore(this);
            MessageBox.Show(this, backup.Verified ? "ZIP 可读性和文件 Hash 校验成功。" : "ZIP 不可读、文件缺失或 Hash 不一致。", "重新校验", MessageBoxButton.OK, backup.Verified ? MessageBoxImage.Information : MessageBoxImage.Warning);
            RefreshList();
        }
        finally { WindowInteractionService.Restore(this); }
    }

    private void OpenZip_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        if (!File.Exists(Selected.FilePath)) { ShowError("备份 ZIP 已经不存在。"); return; }
        Process.Start(new ProcessStartInfo(Selected.FilePath) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Selected is null ? null : Path.GetDirectoryName(Selected.FilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) { ShowError("备份所在目录不存在。"); return; }
        ShellService.OpenFolder(directory);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var backup = Selected;
        if (backup is null) return;
        if (MessageBox.Show($"将永久删除外部 ZIP 备份，此操作不可恢复：\n{backup.FilePath}\n\n是否继续？", "删除外部备份确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { ExternalBackupService.Remove(_state, backup); _save("外部 ZIP 备份已永久删除"); RefreshList(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshList();
    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
