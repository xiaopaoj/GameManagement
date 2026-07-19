using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using GameManagement.Models;
using GameManagement.Services;
using Microsoft.Win32;

namespace GameManagement.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly StateStore _store = new();
    private readonly FileScanner _scanner = new();
    private AppState _state;
    private string _statusMessage = "就绪";
    private bool _hideAddedCandidates = true;
    private GameItem? _selectedGame;
    private ScanPathItem? _selectedScanPath;
    private ScanCandidate? _selectedCandidate;
    private GameDiskItem? _selectedGameDisk;

    public ObservableCollection<GameItem> Games { get; } = [];
    public ObservableCollection<ScanPathItem> ScanPaths { get; } = [];
    public ObservableCollection<GameDiskItem> GameDisks { get; } = [];
    public ObservableCollection<ScanCandidate> Candidates { get; } = [];
    public ObservableCollection<ScanCandidate> VisibleCandidates { get; } = [];
    public ObservableCollection<OperationTaskItem> Tasks { get; } = [];
    public string ApplicationRoot => AppPaths.Root;
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public bool HideAddedCandidates { get => _hideAddedCandidates; set { if (Set(ref _hideAddedCandidates, value)) RefreshCandidates(); } }
    public GameItem? SelectedGame { get => _selectedGame; set => Set(ref _selectedGame, value); }
    public ScanPathItem? SelectedScanPath { get => _selectedScanPath; set => Set(ref _selectedScanPath, value); }
    public ScanCandidate? SelectedCandidate { get => _selectedCandidate; set => Set(ref _selectedCandidate, value); }
    public GameDiskItem? SelectedGameDisk { get => _selectedGameDisk; set => Set(ref _selectedGameDisk, value); }

    public ICommand AddScanPathCommand { get; }
    public ICommand RemoveScanPathCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand AddCandidateToLibraryCommand { get; }
    public ICommand AddGameDiskCommand { get; }
    public ICommand RemoveGameDiskCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand ReloadCommand { get; }

    public MainViewModel()
    {
        _state = _store.Load(); LoadCollections();
        AddScanPathCommand = new RelayCommand(AddScanPath); RemoveScanPathCommand = new RelayCommand(RemoveScanPath);
        ScanCommand = new AsyncRelayCommand(ScanAsync); AddCandidateToLibraryCommand = new RelayCommand(AddCandidateToLibrary);
        AddGameDiskCommand = new RelayCommand(AddGameDisk); RemoveGameDiskCommand = new RelayCommand(RemoveGameDisk);
        LaunchGameCommand = new RelayCommand(LaunchGame); OpenGameFolderCommand = new RelayCommand(OpenGameFolder); ReloadCommand = new RelayCommand(Reload);
    }

    private void LoadCollections()
    {
        Games.Clear(); foreach (var item in _state.Games) Games.Add(item);
        ScanPaths.Clear(); foreach (var item in _state.ScanPaths) ScanPaths.Add(item);
        GameDisks.Clear(); foreach (var item in _state.GameDisks) GameDisks.Add(item);
    }

    private static string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void AddScanPath()
    {
        var path = PickFolder("选择原始游戏文件扫描目录");
        if (path is null || ScanPaths.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var item = new ScanPathItem { Path = path }; ScanPaths.Add(item); _state.ScanPaths.Add(item); Save("扫描路径已添加");
    }

    private void RemoveScanPath()
    {
        if (SelectedScanPath is null) return;
        if (MessageBox.Show($"确定删除扫描路径？\n{SelectedScanPath.Path}", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _state.ScanPaths.RemoveAll(x => x.Id == SelectedScanPath.Id); ScanPaths.Remove(SelectedScanPath); Save("扫描路径已删除");
    }

    private async Task ScanAsync()
    {
        var task = new OperationTaskItem { Name = "扫描原始游戏文件", Status = "运行中", Message = "正在扫描已启用路径" }; Tasks.Insert(0, task); StatusMessage = "正在扫描…";
        try
        {
            var result = await _scanner.ScanAsync(ScanPaths, Games, CancellationToken.None);
            Candidates.Clear(); foreach (var item in result) Candidates.Add(item); RefreshCandidates();
            task.Status = "完成"; task.Progress = 100; task.Message = $"发现 {result.Count} 个候选项目"; Save($"扫描完成，共 {result.Count} 项");
        }
        catch (Exception ex) { task.Status = "失败"; task.Message = ex.Message; AppLogger.Error("扫描失败", ex); ShowError(ex.Message); }
    }

    private void RefreshCandidates()
    {
        VisibleCandidates.Clear(); foreach (var item in Candidates.Where(c => !HideAddedCandidates || !c.Added)) VisibleCandidates.Add(item);
    }

    private void AddCandidateToLibrary()
    {
        if (SelectedCandidate is null) return;
        var owner = Application.Current.MainWindow;
        var dialog = new TextInputWindow("添加游戏", "请输入游戏名称或备注：", SelectedCandidate.Name) { Owner = owner };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        var versionDialog = new TextInputWindow("添加版本", "请输入版本名称：", "初始版本") { Owner = owner };
        if (versionDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(versionDialog.Value)) return;
        var version = new GameVersionItem { VersionName = versionDialog.Value.Trim(), SourcePath = SelectedCandidate.FullPath };
        var game = new GameItem { DisplayName = dialog.Value.Trim(), Note = dialog.Value.Trim(), SourcePath = SelectedCandidate.FullPath, CurrentVersionId = version.Id, CurrentVersionName = version.VersionName, Versions = [version] };
        Games.Add(game); _state.Games.Add(game); SelectedCandidate.Added = true; RefreshCandidates(); Save("游戏已添加到游戏库");
    }

    private void AddGameDisk()
    {
        var path = PickFolder("选择游戏盘工作根目录");
        if (path is null || GameDisks.Any(x => string.Equals(x.RootPath, path, StringComparison.OrdinalIgnoreCase))) return;
        var dialog = new TextInputWindow("添加游戏盘", "请输入游戏盘显示名称：", new DirectoryInfo(path).Name) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        foreach (var folder in new[] { "Games", "GameTemp", "GameSave", "GameSaveTemp" }) Directory.CreateDirectory(Path.Combine(path, folder));
        var disk = new GameDiskItem { DisplayName = dialog.Value.Trim(), RootPath = path }; GameDisks.Add(disk); _state.GameDisks.Add(disk); Save("游戏盘已添加");
    }

    private void RemoveGameDisk()
    {
        if (SelectedGameDisk is null) return;
        if (MessageBox.Show($"仅删除游戏盘配置，不删除实际文件。是否继续？\n{SelectedGameDisk.RootPath}", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _state.GameDisks.RemoveAll(x => x.Id == SelectedGameDisk.Id); GameDisks.Remove(SelectedGameDisk); Save("游戏盘配置已删除");
    }

    private void LaunchGame()
    {
        if (SelectedGame is null) return;
        try { ShellService.LaunchGame(SelectedGame); SelectedGame.Status = "运行中"; SelectedGame.LastPlayedAt = DateTime.Now; Save("游戏已启动"); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenGameFolder()
    {
        var path = SelectedGame?.PlayableRootPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { ShowError("可游玩目录不存在。"); return; }
        ShellService.OpenFolder(path);
    }

    private void Reload() { _state = _store.Load(); LoadCollections(); StatusMessage = "数据已刷新"; }
    private void Save(string message) { _store.Save(_state); StatusMessage = message; AppLogger.Info(message); }
    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
