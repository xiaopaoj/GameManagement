using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameManagement.ViewModels;
using GameManagement.Services;

namespace GameManagement;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _securityIdleTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime _lastUserActivity = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        PreviewMouseDown += (_, _) => _lastUserActivity = DateTime.UtcNow;
        PreviewKeyDown += (_, _) => _lastUserActivity = DateTime.UtcNow;
        _securityIdleTimer.Tick += SecurityIdleTimer_Tick;
        if (MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile)) _securityIdleTimer.Start();
        Closed += (_, _) => _securityIdleTimer.Stop();
    }

    private void GameGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not System.Windows.Controls.DataGridRow) current = VisualTreeHelper.GetParent(current);
        if (current is System.Windows.Controls.DataGridRow row)
        {
            if (!row.IsSelected)
            {
                GameGrid.SelectedItems.Clear();
                GameGrid.SelectedItem = row.Item;
                row.IsSelected = true;
            }
        }
    }

    private void GameContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not System.Windows.Controls.MenuItem item) return;
        var games = GameGrid.SelectedItems.OfType<GameManagement.Models.GameItem>().OrderBy(game => GameGrid.Items.IndexOf(game)).ToList();
        if (item.Tag is string action)
        {
            if (games.Count > 1 && action is "准备游玩" or "归档游戏") _ = viewModel.ExecuteGamesActionAsync(games, action);
            else viewModel.ExecuteSelectedGameAction(action);
        }
        else viewModel.OpenSelectedGameDetails();
    }

    private void GameContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            SystemSaveContextMenuItem.IsChecked = viewModel.SelectedGame?.HasSystemSave == true;
            var multiple = GameGrid.SelectedItems.Count > 1;
            var prepared = !multiple && viewModel.SelectedGame is { PlayableRootPath: { Length: > 0 } playableRoot } && Directory.Exists(playableRoot);
            PrepareContextMenuItem.Header = prepared ? "启动游戏" : "准备游戏";
            PrepareContextMenuItem.Tag = prepared ? "启动游戏" : "准备游玩";
            if (sender is System.Windows.Controls.ContextMenu menu)
            {
                foreach (var entry in menu.Items)
                {
                    if (entry is System.Windows.Controls.Separator separator)
                    {
                        separator.Visibility = multiple ? Visibility.Collapsed : Visibility.Visible;
                        continue;
                    }
                    if (entry is not System.Windows.Controls.MenuItem menuItem) continue;
                    var batchSupported = ReferenceEquals(menuItem, PrepareContextMenuItem)
                        || ReferenceEquals(menuItem, ArchiveContextMenuItem)
                        || ReferenceEquals(menuItem, ExtractionTemplateContextMenuItem);
                    menuItem.Visibility = !multiple || batchSupported ? Visibility.Visible : Visibility.Collapsed;
                    menuItem.IsEnabled = true;
                }
            }
            PopulateExtractionTemplateMenu(viewModel);
        }
    }

    private void ExtractionTemplateContextMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) PopulateExtractionTemplateMenu(viewModel);
    }

    private void PopulateExtractionTemplateMenu(MainViewModel viewModel)
    {
        ExtractionTemplateContextMenuItem.Items.Clear();
        var selectedGames = GameGrid.SelectedItems.OfType<GameManagement.Models.GameItem>().ToList();
        var hasUniformTemplate = selectedGames.Count > 0 && selectedGames.Select(item => item.ExtractionTemplateId).Distinct().Count() == 1;
        var currentId = hasUniformTemplate ? selectedGames[0].ExtractionTemplateId : null;
        var noneItem = new System.Windows.Controls.MenuItem { Header = "不使用模板", IsCheckable = true, IsChecked = hasUniformTemplate && currentId is null, Tag = Guid.Empty };
        noneItem.Click += ExtractionTemplateMenuItem_Click;
        ExtractionTemplateContextMenuItem.Items.Add(noneItem);
        if (viewModel.ExtractionTemplates.Count > 0) ExtractionTemplateContextMenuItem.Items.Add(new System.Windows.Controls.Separator());
        foreach (var template in viewModel.ExtractionTemplates)
        {
            var item = new System.Windows.Controls.MenuItem { Header = template.Name, IsCheckable = true, IsChecked = currentId == template.Id, Tag = template.Id };
            item.Click += ExtractionTemplateMenuItem_Click;
            ExtractionTemplateContextMenuItem.Items.Add(item);
        }
        if (viewModel.ExtractionTemplates.Count == 0)
            ExtractionTemplateContextMenuItem.Items.Add(new System.Windows.Controls.MenuItem { Header = "暂无可用模板，请在设置中创建", IsEnabled = false });
    }

    private void ExtractionTemplateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && sender is System.Windows.Controls.MenuItem { Tag: Guid templateId })
            viewModel.SetGamesExtractionTemplate(GameGrid.SelectedItems.OfType<GameManagement.Models.GameItem>().OrderBy(game => GameGrid.Items.IndexOf(game)).ToList(), templateId == Guid.Empty ? null : templateId);
    }

    private void SystemSaveContextMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel) viewModel.SetSelectedGameHasSystemSave(SystemSaveContextMenuItem.IsChecked);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not MainViewModel { IsScanning: true }) return;
        e.Cancel = true;
        MessageBox.Show("扫描进行中，请等待扫描完成后再关闭软件。", "正在扫描", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void NormalSnapshots_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(1, "正常"); }
    private void AbnormalSnapshots_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(1, "异常"); }
    private void SaveRules_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSystemSaveDirectories(); }
    private void SaveCandidates_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenSaveManagement(0); }
    private void BackupList_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupManagement(); }
    private void BackupSchedule_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupSchedule(); }
    private void PendingBackups_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupSchedule(); }
    private void BackupCleanup_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupManagement("建议清理"); }
    private void TaskHistory_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenTaskHistory(); }
    private void BackupHistory_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenBackupManagement(); }
    private void DeleteHistory_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenDeletionHistory(); }
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => new EncryptedLogViewerWindow { Owner = this }.ShowDialog();
    private void ExtractionTemplates_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenExtractionTemplates(); }

    private void ExecutableIgnoreList_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenExecutableIgnoreList(); }
    private void SecurityMode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel { Tasks: var tasks } && tasks.Any(task => task.Status == "运行中")) { MessageBox.Show(this, "存在运行中的任务，禁止切换安全模式或修改密码。", "任务运行中", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var window = new SecurityModeSettingsWindow { Owner = this };
        if (window.ShowDialog() == true && window.RequiresRestart) Application.Current.Shutdown();
    }

    private void LockNow_Click(object sender, RoutedEventArgs e)
    {
        if (!MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile)) { MessageBox.Show(this, "安全密码模式尚未开启。", "无法锁定", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (HasRunningGames()) { MessageBox.Show(this, "检测到仍在运行的主游戏程序，请先退出游戏后再锁定。", "游戏运行中", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (HasRunningTasks()) { MessageBox.Show(this, "存在运行中的软件任务，请等待任务完成或取消后再锁定。", "任务运行中", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        SecurityLockService.RestartIntoLockedMode();
    }

    private void SecurityIdleTimer_Tick(object? sender, EventArgs e)
    {
        var minutes = Math.Max(1, MasterKeyService.LoadConfiguration(AppPaths.SecurityConfigFile).AutoLockMinutes);
        if (DateTime.UtcNow - _lastUserActivity < TimeSpan.FromMinutes(minutes) || HasRunningGames() || HasRunningTasks()) return;
        _securityIdleTimer.Stop(); SecurityLockService.RestartIntoLockedMode();
    }

    private bool HasRunningTasks() => DataContext is MainViewModel { Tasks: var tasks } && tasks.Any(task => task.Status == "运行中");
    private bool HasRunningGames() => DataContext is MainViewModel viewModel && GameProcessMonitorService.HasRunningGame(viewModel.Games);
}
