using System.Diagnostics;
using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;
using Microsoft.Win32;

namespace GameManagement;

public partial class VersionManagementWindow : Window
{
    private readonly GameItem _game;
    private readonly AppState _state;
    private readonly Action<string> _save;
    private readonly Guid? _initialVersionId;

    public VersionManagementWindow(GameItem game, AppState state, Action<string> save, Guid? initialVersionId = null)
    {
        InitializeComponent();
        _game = game; _state = state; _save = save; _initialVersionId = initialVersionId;
        GameNameText.Text = game.DisplayName;
        RefreshVersions();
    }

    private GameVersionItem? SelectedVersion => VersionGrid.SelectedItem as GameVersionItem;

    private void RefreshVersions(Guid? selectedId = null)
    {
        VersionGrid.ItemsSource = null; VersionGrid.ItemsSource = _game.Versions;
        var targetId = selectedId ?? _initialVersionId ?? _game.CurrentVersionId;
        VersionGrid.SelectedItem = _game.Versions.FirstOrDefault(version => version.Id == targetId) ?? _game.Versions.FirstOrDefault();
        CurrentVersionText.Text = $"当前版本：{_game.CurrentVersionName}｜共 {_game.Versions.Count} 个版本";
    }

    private async void AddVersion_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectSource("选择新版本原始来源");
        if (source is null) return;
        if (_game.Versions.Any(version => PathsEqual(version.SourcePath, source.Value.Path)))
        {
            ShowError("该原始路径已经存在于当前游戏的版本列表中。"); return;
        }
        var nameWindow = new TextInputWindow("添加游戏版本", "请输入版本名称：", $"版本 {_game.Versions.Count + 1}") { Owner = this };
        if (nameWindow.ShowDialog() != true) return;
        var versionName = nameWindow.Value.Trim();
        if (_game.Versions.Any(version => version.VersionName.Equals(versionName, StringComparison.CurrentCultureIgnoreCase)))
        {
            ShowError("版本名称不能重复。"); return;
        }
        var noteWindow = new TextInputWindow("添加游戏版本", "请输入版本备注；允许留空：", string.Empty, true) { Owner = this };
        if (noteWindow.ShowDialog() != true) return;

        var version = new GameVersionItem { VersionName = versionName, Note = noteWindow.Value.Trim(), SourcePath = source.Value.Path, SourceKind = source.Value.Kind };
        if (!await CaptureMetadataAsync(version, "正在添加版本")) return;
        _game.Versions.Add(version);
        if (_game.CurrentVersionId is null) ApplyCurrentVersion(version);
        _save($"已添加游戏版本：{version.VersionName}");
        RefreshVersions(version.Id);
    }

    private void EditVersion_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null) { ShowError("请先选择一个版本。"); return; }
        var nameWindow = new TextInputWindow("编辑版本信息", "请输入版本名称：", version.VersionName) { Owner = this };
        if (nameWindow.ShowDialog() != true) return;
        var versionName = nameWindow.Value.Trim();
        if (_game.Versions.Any(item => item.Id != version.Id && item.VersionName.Equals(versionName, StringComparison.CurrentCultureIgnoreCase)))
        {
            ShowError("版本名称不能重复。"); return;
        }
        var noteWindow = new TextInputWindow("编辑版本信息", "请输入版本备注；允许留空：", version.Note, true) { Owner = this };
        if (noteWindow.ShowDialog() != true) return;
        version.VersionName = versionName; version.Note = noteWindow.Value.Trim();
        if (_game.CurrentVersionId == version.Id) _game.CurrentVersionName = version.VersionName;
        _save($"已更新版本信息：{version.VersionName}");
        RefreshVersions(version.Id);
    }

    private void SetCurrent_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null) { ShowError("请先选择一个版本。"); return; }
        if (_game.CurrentVersionId == version.Id) return;
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath))
        {
            ShowError("当前游戏仍存在可游玩目录，请先完成归档后再切换版本。"); return;
        }
        if (MessageBox.Show($"确定将“{version.VersionName}”设为当前版本？", "切换版本确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyCurrentVersion(version);
        _save($"当前版本已切换为：{version.VersionName}");
        RefreshVersions(version.Id);
    }

    private async void Relocate_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion;
        if (version is null) { ShowError("请先选择一个版本。"); return; }
        var source = SelectSource("选择新的原始文件或目录");
        if (source is null || PathsEqual(version.SourcePath, source.Value.Path)) return;
        var temporary = new GameVersionItem { SourcePath = source.Value.Path, SourceKind = source.Value.Kind };
        if (!await CaptureMetadataAsync(temporary, "正在验证新来源")) return;

        var comparisons = new List<string>();
        comparisons.Add($"文件名：{(Path.GetFileName(version.SourcePath).Equals(Path.GetFileName(temporary.SourcePath), StringComparison.CurrentCultureIgnoreCase) ? "匹配" : "不同")}");
        comparisons.Add(version.SourceSize > 0 ? $"总体积：{(version.SourceSize == temporary.SourceSize ? "匹配" : "不同")}" : "总体积：无历史记录");
        comparisons.Add(version.SourceModifiedAt.HasValue ? $"修改时间：{(version.SourceModifiedAt.Value == temporary.SourceModifiedAt ? "匹配" : "不同")}" : "修改时间：无历史记录");
        comparisons.Add(!string.IsNullOrWhiteSpace(version.SourceFingerprint) ? $"SHA-256 指纹：{(version.SourceFingerprint == temporary.SourceFingerprint ? "匹配" : "不同")}" : "SHA-256 指纹：无历史记录");
        var message = $"准备将版本“{version.VersionName}”的来源更新为：\n{temporary.SourcePath}\n\n匹配结果：\n{string.Join("\n", comparisons)}\n\n即使存在不匹配，也允许由用户确认替换。是否继续？";
        if (MessageBox.Show(message, "重新定位确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        version.SourcePath = temporary.SourcePath; version.SourceKind = temporary.SourceKind;
        SourceMetadataService.Apply(version, new SourceMetadataSnapshot(temporary.SourceFileCount, temporary.SourceSize, temporary.SourceModifiedAt, temporary.SourceFingerprint));
        if (_game.CurrentVersionId == version.Id) ApplyCurrentVersion(version);
        _save($"已重新定位版本来源：{version.VersionName}");
        RefreshVersions(version.Id);
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedVersion?.SourcePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(path)) ShellService.OpenFolder(path);
        else ShowError("原始文件或目录不存在，请执行重新定位。");
    }

    private async Task<bool> CaptureMetadataAsync(GameVersionItem version, string title)
    {
        var progressWindow = new PreparationProgressWindow(title) { Owner = this };
        using var cancellation = new CancellationTokenSource();
        progressWindow.EnableCancellation(cancellation.Cancel);
        var task = new OperationTaskItem { Name = $"{title}：{_game.DisplayName}", TaskType = "来源指纹", GameId = _game.Id, GameVersionId = version.Id, Status = "运行中", Message = title };
        _state.OperationTasks.Add(task); _save($"{title}任务已创建");
        IsEnabled = false; progressWindow.Show();
        try
        {
            var progress = new Progress<SourceMetadataProgress>(value => { task.Progress = value.Percentage; task.CurrentPath = value.CurrentPath; progressWindow.UpdateStatus($"{value.Percentage}%｜{value.CurrentPath}", value.Percentage); });
            var metadata = await SourceMetadataService.CaptureAsync(version.SourcePath, progress, cancellation.Token);
            SourceMetadataService.Apply(version, metadata);
            task.Status = "完成"; task.Progress = 100; task.Message = $"{title}完成"; task.CompletedAt = DateTime.Now; _save(task.Message);
            return true;
        }
        catch (OperationCanceledException) { task.Status = "已取消"; task.Message = $"用户取消了{title}"; task.CompletedAt = DateTime.Now; _save(task.Message); return false; }
        catch (Exception ex) { task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now; _save($"{title}失败"); AppLogger.Error(title, ex); ShowError($"{title}失败：{ex.Message}"); return false; }
        finally { IsEnabled = true; progressWindow.CloseSafely(); }
    }

    private (string Path, string Kind)? SelectSource(string title)
    {
        var choiceWindow = new ChoiceWindow(title, "请选择原始来源形式：", [
            new ChoiceItem { Name = "ZIP/RAR 文件", Description = "选择单个 ZIP 或 RAR 原始压缩文件", Value = SourceKinds.ArchiveFile },
            new ChoiceItem { Name = "压缩文件目录", Description = "选择内部包含 ZIP/RAR 的游戏原始目录", Value = SourceKinds.ArchiveDirectory }
        ]) { Owner = this };
        if (choiceWindow.ShowDialog() != true || choiceWindow.SelectedChoice?.Value is not string kind) return null;

        string? path;
        if (kind == SourceKinds.ArchiveFile)
        {
            var dialog = new OpenFileDialog { Title = title, Filter = "ZIP/RAR 压缩文件|*.zip;*.rar|所有文件|*.*", CheckFileExists = true, Multiselect = false };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
            if (path is not null && ArchiveDiscoveryService.DetectFormat(path) is null) { ShowError("所选文件不是可识别的 ZIP 或 RAR 文件。"); return null; }
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
            path = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
            if (path is not null && ArchiveDiscoveryService.Discover(path).Count == 0) { ShowError("所选目录中没有发现 ZIP、RAR 或可识别的混淆压缩文件。"); return null; }
        }
        return path is null ? null : (Path.GetFullPath(path), kind);
    }

    private void ApplyCurrentVersion(GameVersionItem version)
    {
        _game.CurrentVersionId = version.Id;
        _game.CurrentVersionName = version.VersionName;
        _game.SourcePath = version.SourcePath;
        _game.SourceKind = version.SourceKind;
        if (string.IsNullOrWhiteSpace(_game.PlayableRootPath) || !Directory.Exists(_game.PlayableRootPath)) _game.Status = "未准备";
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
