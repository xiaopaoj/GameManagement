using System.IO;
using System.Windows;
using GameManagement.Models;

namespace GameManagement;

public partial class ExecutableIgnoreListWindow : Window
{
    private readonly AppState _state;
    private readonly Action<string> _save;

    public ExecutableIgnoreListWindow(AppState state, Action<string> save)
    {
        InitializeComponent();
        _state = state;
        _save = save;
        RefreshList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextInputWindow("新增 EXE 忽略项", "请输入完整 EXE 文件名，例如 UnityCrashHandler64.exe：", string.Empty) { Owner = this };
        if (input.ShowDialog() != true || !TryNormalize(input.Value, out var name)) return;
        if (_state.UiSettings.ExecutableIgnoreNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { ShowError("该文件名已经存在于忽略清单中。"); return; }
        _state.UiSettings.ExecutableIgnoreNames.Add(name);
        SaveAndRefresh("EXE 忽略项已新增");
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (IgnoreList.SelectedItem is not string oldName) { ShowError("请先选择需要编辑的忽略项。"); return; }
        var input = new TextInputWindow("编辑 EXE 忽略项", "请输入完整 EXE 文件名：", oldName) { Owner = this };
        if (input.ShowDialog() != true || !TryNormalize(input.Value, out var name)) return;
        if (!name.Equals(oldName, StringComparison.OrdinalIgnoreCase) && _state.UiSettings.ExecutableIgnoreNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { ShowError("该文件名已经存在于忽略清单中。"); return; }
        var index = _state.UiSettings.ExecutableIgnoreNames.FindIndex(item => item.Equals(oldName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _state.UiSettings.ExecutableIgnoreNames[index] = name;
        SaveAndRefresh("EXE 忽略项已更新");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IgnoreList.SelectedItem is not string name) { ShowError("请先选择需要删除的忽略项。"); return; }
        if (MessageBox.Show(this, $"确定从忽略清单删除“{name}”吗？", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _state.UiSettings.ExecutableIgnoreNames.RemoveAll(item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        SaveAndRefresh("EXE 忽略项已删除");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SaveAndRefresh(string message) { _save(message); RefreshList(); }

    private void RefreshList() => IgnoreList.ItemsSource = _state.UiSettings.ExecutableIgnoreNames.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private bool TryNormalize(string value, out string name)
    {
        var input = value.Trim();
        name = Path.GetFileName(input);
        if (string.IsNullOrWhiteSpace(name) || !input.Equals(name, StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("请输入不包含路径的完整 EXE 文件名，并以 .exe 结尾。");
            return false;
        }
        return true;
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "EXE 忽略清单", MessageBoxButton.OK, MessageBoxImage.Warning);
}
