using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
        if (DataContext is MainViewModel viewModel && viewModel.LaunchGameCommand.CanExecute(null)) viewModel.LaunchGameCommand.Execute(null);
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
            if (sender is System.Windows.Controls.ContextMenu menu)
            {
                foreach (var menuItem in menu.Items.OfType<System.Windows.Controls.MenuItem>())
                    menuItem.IsEnabled = !multiple || ReferenceEquals(menuItem, PrepareContextMenuItem) || ReferenceEquals(menuItem, ArchiveContextMenuItem) || ReferenceEquals(menuItem, ExtractionTemplateContextMenuItem);
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
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => ShellService.OpenFolder(AppPaths.Logs);
    private void ExtractionTemplates_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenExtractionTemplates(); }

    private void ExecutableIgnoreList_Click(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel viewModel) viewModel.OpenExecutableIgnoreList(); }
}
