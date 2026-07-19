using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class TaskHistoryWindow : Window
{
    private OperationTaskItem? Selected => TaskGrid.SelectedItem as OperationTaskItem;

    public TaskHistoryWindow(AppState state)
    {
        InitializeComponent();
        TaskGrid.ItemsSource = state.OperationTasks.OrderByDescending(item => item.StartedAt).ToList();
    }

    private void ShowError_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is null) { MessageBox.Show("请先选择一条任务记录。", "未选择任务", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var detail = string.IsNullOrWhiteSpace(Selected.ErrorMessage) ? "该任务没有错误详情。" : Selected.ErrorMessage;
        MessageBox.Show(detail, $"任务详情 - {Selected.Name}", MessageBoxButton.OK, Selected.Status == "失败" ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Selected?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { MessageBox.Show("该任务没有仍然存在的临时目录。", "目录不存在", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        ShellService.OpenFolder(path);
    }
}
