using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;
using Microsoft.Win32;

namespace GameManagement;

public partial class SystemSaveDirectoryWindow : Window
{
    private readonly GameItem _game;
    private readonly AppState _state;
    private readonly Action<string> _save;
    private SystemSaveDirectoryRuleItem? SelectedRule => DirectoryGrid.SelectedItem as SystemSaveDirectoryRuleItem;

    public SystemSaveDirectoryWindow(GameItem game, AppState state, Action<string> save)
    {
        InitializeComponent(); _game = game; _state = state; _save = save;
        GameNameText.Text = $"系统存档目录 - {game.DisplayName}";
        RefreshList();
    }

    private void RefreshList()
    {
        DirectoryGrid.ItemsSource = _state.SystemSaveDirectories.Where(item => item.GameId == _game.Id).OrderBy(item => item.DisplayName).ToList();
        InitialScanText.Text = _game.SystemSaveInitialScanCompleted
            ? $"首次常见目录扫描已确认完成：{_game.SystemSaveInitialScanCompletedAt:yyyy-MM-dd HH:mm:ss}。后续运行只监控下方已启用目录。"
            : "首次常见目录扫描尚未确认完成。下次启动游戏前将扫描 AppData、LocalAppData、Documents 和 Saved Games。";
    }

    private static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder("选择系统存档目录");
        if (path is null) return;
        if (_state.SystemSaveDirectories.Any(item => item.GameId == _game.Id && string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) { ShowError("该游戏已经配置此目录。 "); return; }
        var nameWindow = new TextInputWindow("目录名称", "请输入目录显示名称：", new DirectoryInfo(path).Name) { Owner = this };
        if (nameWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(nameWindow.Value)) return;
        _state.SystemSaveDirectories.Add(new SystemSaveDirectoryRuleItem { GameId = _game.Id, Path = path, DisplayName = nameWindow.Value.Trim() });
        _save("系统存档目录已添加"); RefreshList();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        var rule = SelectedRule;
        if (rule is null) return;
        var path = PickFolder("重新选择系统存档目录");
        if (path is null) return;
        var nameWindow = new TextInputWindow("目录名称", "请输入目录显示名称：", rule.DisplayName) { Owner = this };
        if (nameWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(nameWindow.Value)) return;
        rule.Path = path; rule.DisplayName = nameWindow.Value.Trim(); rule.AutoDiscovered = false;
        _save("系统存档目录已修改"); RefreshList();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is null) return;
        SelectedRule.Enabled = !SelectedRule.Enabled; _save(SelectedRule.Enabled ? "系统存档目录已启用" : "系统存档目录已禁用"); RefreshList();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rule = SelectedRule;
        if (rule is null) return;
        if (MessageBox.Show($"仅删除目录关联配置，不删除实际存档文件。是否继续？\n{rule.Path}", "删除目录配置确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _state.SystemSaveDirectories.RemoveAll(item => item.Id == rule.Id); _save("系统存档目录配置已删除"); RefreshList();
    }

    private void ResetInitialScan_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("下次启动游戏前将重新扫描全部常见系统目录，可能耗时较长。是否继续？", "重新扫描确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _game.SystemSaveInitialScanCompleted = false; _game.SystemSaveInitialScanCompletedAt = null; _save("已重新启用首次常见目录扫描"); RefreshList();
    }

    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
