using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;
using Microsoft.Win32;

namespace GameManagement;

public partial class BackupScheduleWindow : Window
{
    private readonly AppState _state;
    private readonly Action<string> _save;

    public BackupScheduleWindow(AppState state, Action<string> save)
    {
        InitializeComponent(); _state = state; _save = save; RefreshFields();
    }

    private void RefreshFields()
    {
        var settings = _state.BackupSettings;
        DirectoryText.Text = settings.BackupDirectory;
        TimeText.Text = settings.DailyTime;
        EnabledCheck.IsChecked = settings.DailyEnabled;
        LastRunText.Text = $"{settings.LastRunAt:yyyy-MM-dd HH:mm:ss}｜{settings.LastStatus}{(string.IsNullOrWhiteSpace(settings.LastError) ? string.Empty : $"｜{settings.LastError}")}";
        PendingText.Text = settings.PendingScheduledBackup ? $"等待目标磁盘，自 {settings.PendingSince:yyyy-MM-dd HH:mm:ss}" : "没有等待补执行任务";
    }

    private void PickDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择唯一的外部备份目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try { BackupTargetService.Configure(_state.BackupSettings, dialog.FolderName); DirectoryText.Text = _state.BackupSettings.BackupDirectory; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_state.BackupSettings.BackupDirectory)) { ShowError("请先选择外部备份目录。"); return; }
        if (!TimeOnly.TryParseExact(TimeText.Text.Trim(), "HH:mm", out _)) { ShowError("每日执行时间必须使用 HH:mm 格式，例如 02:00。"); return; }
        try
        {
            _state.BackupSettings.DailyTime = TimeText.Text.Trim();
            _state.BackupSettings.DailyEnabled = EnabledCheck.IsChecked == true;
            if (_state.BackupSettings.DailyEnabled) BackupScheduleService.RegisterDaily(_state.BackupSettings.DailyTime);
            else BackupScheduleService.RemoveDaily();
            _save(_state.BackupSettings.DailyEnabled ? "每日计划备份设置已保存并注册" : "每日计划备份已禁用");
            RefreshFields();
            MessageBox.Show("计划备份设置已保存。", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void RunNow_Click(object sender, RoutedEventArgs e)
    {
        var progress = new PreparationProgressWindow("正在备份所有游戏盘的完整 GameSave 目录") { Owner = this };
        using var cancellation = new CancellationTokenSource();
        progress.EnableCancellation(cancellation.Cancel);
        IsEnabled = false; progress.Show();
        try
        {
            var result = await ExternalBackupService.CreateScheduledFullBackupAsync(_state, cancellation.Token);
            _save($"完整 GameSave 备份：{result.Status}"); RefreshFields();
            WindowInteractionService.RestoreBeforeDialog(this, progress);
            MessageBox.Show(this, result.Message, "完整备份", MessageBoxButton.OK, result.WaitingForTarget ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { WindowInteractionService.RestoreBeforeDialog(this, progress); MessageBox.Show(this, "完整备份已取消，临时 ZIP 已自动清理。", "已取消", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { _state.BackupSettings.LastStatus = "失败"; _state.BackupSettings.LastError = ex.Message; _save("完整 GameSave 备份失败"); WindowInteractionService.RestoreBeforeDialog(this, progress); MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { WindowInteractionService.CompleteProgress(this, progress); }
    }

    private void ViewBackups_Click(object sender, RoutedEventArgs e) => new BackupManagementWindow(_state, _save) { Owner = this }.ShowDialog();
    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
