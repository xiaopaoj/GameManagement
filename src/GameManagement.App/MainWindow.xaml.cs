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
    private void SaveRules_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSystemSaveDirectories(); }
    private void SaveCandidates_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(0); }
    private void BackupList_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupManagement(); }
    private void BackupSchedule_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupSchedule(); }
    private void PendingBackups_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupSchedule(); }
    private void BackupCleanup_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupManagement("建议清理"); }
    private void TaskHistory_Click(object sender, RoutedEventArgs e) => ShowFeature("任务历史", "查询复制、解压、Hash、存档和清理任务的执行记录。");
    private void BackupHistory_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupManagement(); }
    private void DeleteHistory_Click(object sender, RoutedEventArgs e) => ShowFeature("删除历史", "查询进入回收站、永久清理临时目录及数据库记录删除的历史。");
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ShellService.OpenFolder(AppPaths.Logs);
}
