using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GameManagement.ViewModels;
using GameManagement.Services;

namespace GameManagement;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void GameGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.OpenSelectedGameDetails();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel { IsScanning: true }) return;
        e.Cancel = true;
        MessageBox.Show("扫描进行中，请等待扫描完成后再关闭软件。", "正在扫描", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowFeature(string name, string description) => new FeatureStatusWindow(name, description) { Owner = this }.ShowDialog();
    private void NormalSnapshots_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(1, "正常"); }
    private void AbnormalSnapshots_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(1, "异常"); }
    private void SaveRules_Click(object sender, RoutedEventArgs e) => ShowFeature("存档目录规则", "管理每个游戏已确认的一个或多个 Windows 系统存档目录。");
    private void SaveCandidates_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(0); }
    private void BackupList_Click(object sender, RoutedEventArgs e) => ShowFeature("外部备份列表", "查看无密码 ZIP 备份、文件树、校验状态和人工恢复说明。");
    private void BackupSchedule_Click(object sender, RoutedEventArgs e) => ShowFeature("每日计划备份", "配置每天执行时间，并通过 Windows 任务计划程序备份所有游戏盘的完整 GameSave 目录。");
    private void PendingBackups_Click(object sender, RoutedEventArgs e) => ShowFeature("等待补执行", "查看备份磁盘离线时等待重新连接后补执行的任务。");
    private void BackupCleanup_Click(object sender, RoutedEventArgs e) => ShowFeature("超额备份清理", "查看超过三个保留版本的备份，并在一次人工确认后进行清理。");
    private void TaskHistory_Click(object sender, RoutedEventArgs e) => ShowFeature("任务历史", "查询复制、解压、Hash、存档和清理任务的执行记录。");
    private void BackupHistory_Click(object sender, RoutedEventArgs e) => ShowFeature("备份历史", "查询手动备份和每日计划备份的结果及校验记录。");
    private void DeleteHistory_Click(object sender, RoutedEventArgs e) => ShowFeature("删除历史", "查询进入回收站、永久清理临时目录及数据库记录删除的历史。");
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ShellService.OpenFolder(AppPaths.Logs);
}
