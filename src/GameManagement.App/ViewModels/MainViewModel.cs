using System.Collections.ObjectModel;
using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Data;
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
    private GameDiskItem? _selectedGameDisk;
    private bool _isScanning;
    private int _scanProgress;
    private string _scanProgressMessage = string.Empty;
    private string _candidateSearchText = string.Empty;
    private string _candidateKindFilter = "全部";
    private string _candidateAddedFilter = "全部";
    private string _candidateDriveFilter = "全部";
    private OperationTaskItem? _selectedTask;
    private CancellationTokenSource? _scanCancellation;
    private readonly HashSet<Guid> _saveScansInProgress = [];

    public ObservableCollection<GameItem> Games { get; } = [];
    public ObservableCollection<ScanPathItem> ScanPaths { get; } = [];
    public ObservableCollection<GameDiskItem> GameDisks { get; } = [];
    public ObservableCollection<ScanCandidate> Candidates { get; } = [];
    public ObservableCollection<ScanCandidate> VisibleCandidates { get; } = [];
    public ObservableCollection<OperationTaskItem> Tasks { get; } = [];
    public ObservableCollection<string> CandidateKindOptions { get; } = ["全部", SourceKinds.ArchiveFile, SourceKinds.ArchiveDirectory];
    public ObservableCollection<string> CandidateAddedOptions { get; } = ["全部", "未添加", "已添加"];
    public ObservableCollection<string> CandidateDriveOptions { get; } = ["全部"];
    public string ApplicationRoot => AppPaths.Root;
    public bool IsScanning { get => _isScanning; set { if (Set(ref _isScanning, value)) Raise(nameof(IsInterfaceEnabled)); } }
    public bool IsInterfaceEnabled => !IsScanning;
    public int ScanProgress { get => _scanProgress; set => Set(ref _scanProgress, value); }
    public string ScanProgressMessage { get => _scanProgressMessage; set => Set(ref _scanProgressMessage, value); }
    public string CandidateSearchText { get => _candidateSearchText; set { if (Set(ref _candidateSearchText, value)) RefreshCandidates(); } }
    public string CandidateKindFilter { get => _candidateKindFilter; set { if (Set(ref _candidateKindFilter, value)) RefreshCandidates(); } }
    public string CandidateAddedFilter { get => _candidateAddedFilter; set { if (Set(ref _candidateAddedFilter, value)) RefreshCandidates(); } }
    public string CandidateDriveFilter { get => _candidateDriveFilter; set { if (Set(ref _candidateDriveFilter, value)) RefreshCandidates(); } }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public bool HideAddedCandidates { get => _hideAddedCandidates; set { if (Set(ref _hideAddedCandidates, value)) RefreshCandidates(); } }
    public GameItem? SelectedGame { get => _selectedGame; set => Set(ref _selectedGame, value); }
    public ScanPathItem? SelectedScanPath { get => _selectedScanPath; set => Set(ref _selectedScanPath, value); }
    public GameDiskItem? SelectedGameDisk { get => _selectedGameDisk; set => Set(ref _selectedGameDisk, value); }
    public OperationTaskItem? SelectedTask { get => _selectedTask; set => Set(ref _selectedTask, value); }

    public ICommand AddScanPathCommand { get; }
    public ICommand RemoveScanPathCommand { get; }
    public ICommand RelocateScanPathCommand { get; }
    public ICommand ToggleScanPathCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand AddCandidateToLibraryCommand { get; }
    public ICommand AddGameDiskCommand { get; }
    public ICommand RemoveGameDiskCommand { get; }
    public ICommand EditGameDiskCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand OpenGameDetailsCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand OpenTaskFolderCommand { get; }
    public ICommand CleanupTaskTempCommand { get; }
    public ICommand OpenTaskGameCommand { get; }
    public ICommand ShowTaskErrorCommand { get; }

    public MainViewModel()
    {
        _state = _store.Load(); LoadCollections(); _store.Save(_state);
        AddScanPathCommand = new RelayCommand(AddScanPath); RemoveScanPathCommand = new RelayCommand(RemoveScanPath); RelocateScanPathCommand = new RelayCommand(RelocateScanPath); ToggleScanPathCommand = new RelayCommand(ToggleScanPath);
        ScanCommand = new AsyncRelayCommand(ScanAsync); AddCandidateToLibraryCommand = new AsyncRelayCommand<IList>(AddCandidatesToLibraryAsync);
        CancelScanCommand = new RelayCommand(CancelScan);
        AddGameDiskCommand = new RelayCommand(AddGameDisk); RemoveGameDiskCommand = new RelayCommand(RemoveGameDisk); EditGameDiskCommand = new RelayCommand(EditGameDisk);
        OpenGameDetailsCommand = new RelayCommand(OpenSelectedGameDetails);
        LaunchGameCommand = new RelayCommand(LaunchGame); OpenGameFolderCommand = new RelayCommand(OpenGameFolder); ReloadCommand = new RelayCommand(Reload);
        OpenTaskFolderCommand = new RelayCommand(OpenTaskFolder); CleanupTaskTempCommand = new RelayCommand(CleanupTaskTemp); OpenTaskGameCommand = new RelayCommand(OpenTaskGame); ShowTaskErrorCommand = new RelayCommand(ShowTaskError);
        GameProcessMonitorService.Restore(_state.Games, OnGameStateChanged);
        _ = TryRunPendingBackupAsync();
    }

    private void LoadCollections()
    {
        RefreshGameOrder();
        ScanPaths.Clear(); foreach (var item in _state.ScanPaths) ScanPaths.Add(item);
        GameDisks.Clear(); foreach (var item in _state.GameDisks) GameDisks.Add(item);
        Tasks.Clear(); foreach (var item in _state.OperationTasks.OrderByDescending(task => task.StartedAt)) Tasks.Add(item);
    }

    public static IEnumerable<GameItem> SortGames(IEnumerable<GameItem> games) => games
        .OrderBy(game => game.ArchiveStatus == "已归档" || game.Status == "已归档")
        .ThenByDescending(game => game.AddedAt)
        .ThenByDescending(game => game.LastPlayedAt ?? DateTime.MinValue)
        .ThenBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase);

    public static IEnumerable<ScanCandidate> SortCandidates(IEnumerable<ScanCandidate> candidates) => candidates
        .OrderByDescending(candidate => candidate.ModifiedAt)
        .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase);

    private void RefreshGameOrder()
    {
        Games.Clear();
        foreach (var item in SortGames(_state.Games)) Games.Add(item);
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

    private void RelocateScanPath()
    {
        if (SelectedScanPath is null) return;
        var path = PickFolder("重新定位扫描路径");
        if (path is null || string.Equals(path, SelectedScanPath.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (ScanPaths.Any(item => item.Id != SelectedScanPath.Id && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) { ShowError("该扫描路径已经存在。"); return; }
        if (MessageBox.Show($"确定将扫描路径更新为：\n{path}", "重新定位确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SelectedScanPath.Path = path; Save("扫描路径已重新定位"); CollectionViewSource.GetDefaultView(ScanPaths).Refresh();
    }

    private void ToggleScanPath()
    {
        if (SelectedScanPath is null) return;
        SelectedScanPath.Enabled = !SelectedScanPath.Enabled; Save(SelectedScanPath.Enabled ? "扫描路径已启用" : "扫描路径已禁用"); CollectionViewSource.GetDefaultView(ScanPaths).Refresh();
    }

    private async Task ScanAsync()
    {
        var task = new OperationTaskItem { Name = "扫描原始游戏文件", TaskType = "文件扫描", Status = "运行中", Message = "正在扫描已启用路径" };
        _state.OperationTasks.Add(task); Tasks.Insert(0, task); StatusMessage = "正在扫描…"; _store.Save(_state);
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true; ScanProgress = 0; ScanProgressMessage = "正在统计扫描项目…";
        try
        {
            var progress = new Progress<ScanProgressInfo>(value =>
            {
                ScanProgress = value.Percentage;
                ScanProgressMessage = value.Total <= 0 ? "没有发现可扫描项目" : $"{value.Completed}/{value.Total}｜{value.CurrentPath}";
                task.Progress = value.Percentage; task.CurrentPath = value.CurrentPath;
            });
            var result = await _scanner.ScanAsync(ScanPaths, Games, progress, _scanCancellation.Token);
            Candidates.Clear(); foreach (var item in result) Candidates.Add(item); RefreshCandidateFilterOptions(); RefreshCandidates();
            task.Status = "完成"; task.Progress = 100; task.Message = $"发现 {result.Count} 个候选项目"; task.CompletedAt = DateTime.Now; Save($"扫描完成，共 {result.Count} 项");
        }
        catch (OperationCanceledException) { task.Status = "已取消"; task.Message = "用户取消了文件扫描。"; task.CompletedAt = DateTime.Now; StatusMessage = "扫描已取消"; }
        catch (Exception ex) { task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now; AppLogger.Error("扫描失败", ex); ShowError(ex.Message); }
        finally { IsScanning = false; _scanCancellation.Dispose(); _scanCancellation = null; _store.Save(_state); }
    }

    private void CancelScan()
    {
        if (_scanCancellation is null || _scanCancellation.IsCancellationRequested) return;
        ScanProgressMessage = "正在取消扫描，请稍候…"; _scanCancellation.Cancel();
    }

    private void RefreshCandidates()
    {
        var query = Candidates.AsEnumerable();
        if (HideAddedCandidates) query = query.Where(candidate => !candidate.Added);
        if (!string.IsNullOrWhiteSpace(CandidateSearchText))
            query = query.Where(candidate => candidate.Name.Contains(CandidateSearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) || candidate.FullPath.Contains(CandidateSearchText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (CandidateKindFilter != "全部") query = query.Where(candidate => candidate.Kind == CandidateKindFilter);
        if (CandidateAddedFilter == "未添加") query = query.Where(candidate => !candidate.Added);
        if (CandidateAddedFilter == "已添加") query = query.Where(candidate => candidate.Added);
        if (CandidateDriveFilter != "全部") query = query.Where(candidate => string.Equals(candidate.DriveName, CandidateDriveFilter, StringComparison.OrdinalIgnoreCase));
        VisibleCandidates.Clear(); foreach (var item in SortCandidates(query)) VisibleCandidates.Add(item);
    }

    private void RefreshCandidateFilterOptions()
    {
        var selected = CandidateDriveFilter;
        CandidateDriveOptions.Clear(); CandidateDriveOptions.Add("全部");
        foreach (var drive in Candidates.Select(candidate => candidate.DriveName).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value)) CandidateDriveOptions.Add(drive);
        CandidateDriveFilter = CandidateDriveOptions.Contains(selected) ? selected : "全部";
    }

    private async Task AddCandidatesToLibraryAsync(IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0) return;
        var selected = selectedItems.OfType<ScanCandidate>().Where(candidate => !candidate.Added).ToList();
        if (selected.Count == 0) { StatusMessage = "所选项目均已存在于游戏库"; return; }

        var owner = Application.Current.MainWindow;
        var progressWindow = new PreparationProgressWindow("正在添加游戏") { Owner = owner };
        using var cancellation = new CancellationTokenSource();
        progressWindow.EnableCancellation(cancellation.Cancel);
        var task = new OperationTaskItem { Name = $"添加 {selected.Count} 个游戏", TaskType = "来源指纹", Status = "运行中", Message = "正在计算原始来源指纹" };
        _state.OperationTasks.Add(task); Tasks.Insert(0, task); _store.Save(_state);
        progressWindow.Show();
        try
        {
            var prepared = new List<(ScanCandidate Candidate, GameVersionItem Version)>();
            for (var index = 0; index < selected.Count; index++)
            {
                var candidate = selected[index];
                if (_state.Games.Any(game => string.Equals(NormalizePath(game.SourcePath), NormalizePath(candidate.FullPath), StringComparison.OrdinalIgnoreCase)))
                {
                    candidate.Added = true;
                    continue;
                }
                progressWindow.UpdateStatus($"正在记录来源指纹（{index + 1}/{selected.Count}）：{candidate.Name}", index * 100 / selected.Count);
                var metadataProgress = new Progress<SourceMetadataProgress>(value =>
                {
                    var overall = (index * 100 + value.Percentage) / selected.Count;
                    task.Progress = overall; task.CurrentPath = value.CurrentPath; task.Message = $"正在记录来源指纹（{index + 1}/{selected.Count}）";
                    progressWindow.UpdateStatus($"正在记录来源指纹（{index + 1}/{selected.Count}，{value.Percentage}%）：{value.CurrentPath}", overall);
                });
                var metadata = await SourceMetadataService.CaptureAsync(candidate.FullPath, metadataProgress, cancellation.Token);
                var version = new GameVersionItem { VersionName = "初始版本", SourcePath = candidate.FullPath, SourceKind = candidate.Kind };
                SourceMetadataService.Apply(version, metadata);
                prepared.Add((candidate, version));
            }

            foreach (var item in prepared)
            {
                var displayName = GetCandidateDisplayName(item.Candidate);
                var game = new GameItem
                {
                    AddedAt = DateTime.Now,
                    DisplayName = displayName,
                    Note = displayName,
                    SourcePath = item.Candidate.FullPath,
                    SourceKind = item.Candidate.Kind,
                    CurrentVersionId = item.Version.Id,
                    CurrentVersionName = item.Version.VersionName,
                    Versions = [item.Version]
                };
                Games.Add(game); _state.Games.Add(game); item.Candidate.Added = true;
            }

            RefreshGameOrder();
            RefreshCandidates();
            task.Status = "完成"; task.Progress = 100; task.Message = $"已添加 {prepared.Count} 个游戏并保存来源指纹"; task.CompletedAt = DateTime.Now;
            Save(task.Message);
        }
        catch (OperationCanceledException) { task.Status = "已取消"; task.Message = "用户取消了来源指纹计算，未添加尚未完成的游戏。"; task.CompletedAt = DateTime.Now; StatusMessage = task.Message; }
        catch (Exception ex) { task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now; AppLogger.Error("添加游戏并记录来源指纹失败", ex); WindowInteractionService.RestoreBeforeDialog(owner, progressWindow); MessageBox.Show(owner, $"添加失败：{ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _store.Save(_state); WindowInteractionService.CompleteProgress(owner, progressWindow); }
    }

    public static string GetCandidateDisplayName(ScanCandidate candidate)
    {
        if (candidate.Kind == SourceKinds.ArchiveFile)
        {
            var sevenZipPart = System.Text.RegularExpressions.Regex.Match(candidate.Name, @"^(?<name>.+)\.7z\.\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return sevenZipPart.Success ? sevenZipPart.Groups["name"].Value : Path.GetFileNameWithoutExtension(candidate.Name);
        }
        return candidate.Name;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void AddGameDisk()
    {
        var path = PickFolder("选择游戏盘工作根目录");
        if (path is null || GameDisks.Any(x => string.Equals(x.RootPath, path, StringComparison.OrdinalIgnoreCase))) return;
        var dialog = new TextInputWindow("添加游戏盘", "请输入游戏盘显示名称：", new DirectoryInfo(path).Name) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        foreach (var folder in new[] { "Games", "GameTemp", "GameSave", "GameSaveTemp" }) Directory.CreateDirectory(Path.Combine(path, folder));
        var disk = new GameDiskItem { DisplayName = dialog.Value.Trim(), RootPath = path, IsDefault = GameDisks.Count == 0 }; GameDisks.Add(disk); _state.GameDisks.Add(disk); Save("游戏盘已添加");
    }

    private void RemoveGameDisk()
    {
        if (SelectedGameDisk is null) return;
        if (MessageBox.Show($"仅删除游戏盘配置，不删除实际文件。是否继续？\n{SelectedGameDisk.RootPath}", "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var wasDefault = SelectedGameDisk.IsDefault;
        _state.GameDisks.RemoveAll(x => x.Id == SelectedGameDisk.Id); GameDisks.Remove(SelectedGameDisk);
        if (wasDefault && GameDisks.Count > 0) GameDisks[0].IsDefault = true;
        Save("游戏盘配置已删除");
    }

    private void EditGameDisk()
    {
        if (SelectedGameDisk is null) return;
        var nameWindow = new TextInputWindow("编辑游戏盘", "请输入游戏盘显示名称：", SelectedGameDisk.DisplayName) { Owner = Application.Current.MainWindow };
        if (nameWindow.ShowDialog() != true) return;
        var thresholdWindow = new TextInputWindow("编辑游戏盘", "请输入最低保留空间（GB）：", Math.Max(0, SelectedGameDisk.MinimumFreeSpaceBytes / 1024d / 1024 / 1024).ToString("0.##")) { Owner = Application.Current.MainWindow };
        if (thresholdWindow.ShowDialog() != true) return;
        if (!double.TryParse(thresholdWindow.Value, out var thresholdGb) || thresholdGb < 0 || thresholdGb > 100000) { ShowError("最低保留空间必须是有效的非负数字。"); return; }
        var setDefault = MessageBox.Show("是否将该游戏盘设为默认选择？", "默认游戏盘", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        var enabled = MessageBox.Show("是否启用该游戏盘？", "游戏盘状态", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        SelectedGameDisk.DisplayName = nameWindow.Value.Trim(); SelectedGameDisk.MinimumFreeSpaceBytes = (long)(thresholdGb * 1024 * 1024 * 1024); SelectedGameDisk.IsDefault = setDefault; SelectedGameDisk.Enabled = enabled;
        if (setDefault) foreach (var disk in GameDisks.Where(disk => disk.Id != SelectedGameDisk.Id)) disk.IsDefault = false;
        Save("游戏盘配置已更新"); CollectionViewSource.GetDefaultView(GameDisks).Refresh();
    }

    private async void LaunchGame()
    {
        if (SelectedGame is null) return;
        if (SelectedGame.Status == "运行中") { ShowError("该游戏的主程序已经在运行。 "); return; }
        var game = SelectedGame;
        var owner = Application.Current.MainWindow;
        if (!game.HasSystemSave)
        {
            try
            {
                SystemSaveMonitoringService.CancelLatestSession(_state, game, "已跳过");
                _store.Save(_state);
                StatusMessage = "该游戏已标记为不存在系统存档，本次跳过系统目录扫描";
                GameProcessMonitorService.Launch(game, OnGameStateChanged);
            }
            catch (Exception ex) { _store.Save(_state); ShowError(ex.Message); }
            return;
        }
        var progressWindow = new PreparationProgressWindow("正在建立系统存档监控快照") { Owner = owner };
        using var cancellation = new CancellationTokenSource();
        progressWindow.EnableCancellation(cancellation.Cancel);
        try
        {
            owner.IsEnabled = false; progressWindow.Show();
            var progress = new Progress<SystemMonitorProgress>(value => progressWindow.UpdateStatus($"已扫描 {value.FileCount} 个系统目录文件：{value.CurrentPath}", 50));
            await SystemSaveMonitoringService.BeginSessionAsync(_state, game, progress, cancellation.Token);
            _store.Save(_state);
            WindowInteractionService.RestoreBeforeDialog(owner, progressWindow);
            GameProcessMonitorService.Launch(game, OnGameStateChanged);
        }
        catch (OperationCanceledException) { SystemSaveMonitoringService.CancelLatestSession(_state, game, "已取消"); _store.Save(_state); StatusMessage = "游戏启动前的系统存档扫描已取消"; }
        catch (Exception ex) { SystemSaveMonitoringService.CancelLatestSession(_state, game, "启动失败"); _store.Save(_state); WindowInteractionService.RestoreBeforeDialog(owner, progressWindow); MessageBox.Show(owner, ex.Message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { WindowInteractionService.CompleteProgress(owner, progressWindow); }
    }

    private void OnGameStateChanged(GameItem game, string message)
    {
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            _store.Save(_state);
            StatusMessage = message;
            RefreshGameOrder();
            AppLogger.Info($"{game.DisplayName}：{message}");
            if (message.StartsWith("主游戏程序已退出", StringComparison.Ordinal)) await DetectSaveChangesAfterExitAsync(game);
        });
    }

    private async Task DetectSaveChangesAfterExitAsync(GameItem game)
    {
        if (!_saveScansInProgress.Add(game.Id)) return;
        var task = new OperationTaskItem { Name = $"扫描存档变化：{game.DisplayName}", TaskType = "存档扫描", GameId = game.Id, GameVersionId = game.CurrentVersionId, Status = "运行中", Message = "正在比较游戏目录与首次文件基线" };
        _state.OperationTasks.Add(task); Tasks.Insert(0, task); _store.Save(_state);
        try
        {
            var kind = game.LastExitCode == 0 ? SaveSnapshotKinds.Normal : SaveSnapshotKinds.Abnormal;
            var activeSystemSession = _state.SystemMonitorSessions.Where(item => item.GameId == game.Id && item.Status == "监控中").OrderByDescending(item => item.StartedAt).FirstOrDefault();
            var wasInitialSystemScan = activeSystemSession?.IsInitialCommonScan == true;
            var systemCandidates = await SystemSaveMonitoringService.CompleteSessionAsync(_state, game, kind);
            SystemSaveMonitoringService.ReplaceDetectedCandidates(_state, game, systemCandidates);
            var gameCandidates = await SaveChangeDetectionService.DetectAsync(_state, game, kind);
            SaveChangeDetectionService.ReplaceDetectedCandidates(_state, game, gameCandidates);
            var candidates = gameCandidates.Concat(systemCandidates).ToList();
            var automaticCandidates = candidates.Where(item => item.PreviouslyConfirmed && !item.SharedDirectory && item.ChangeType != "删除" && item.Decision == SaveCandidateDecisions.Pending).Select(item => item.Id).ToList();
            SaveSnapshotCreationResult? automaticResult = null;
            if (automaticCandidates.Count > 0) automaticResult = await SaveSnapshotService.ApplyAndCreateAsync(_state, game, automaticCandidates);
            task.Status = "完成"; task.Progress = 100; task.Message = automaticResult?.ContentChanged == true ? $"发现 {candidates.Count} 个变化，已自动创建{kind}快照" : $"发现 {candidates.Count} 个游戏及系统目录变化"; task.CompletedAt = DateTime.Now;
            _store.Save(_state);
            var pendingCount = candidates.Count(item => item.Decision == SaveCandidateDecisions.Pending);
            StatusMessage = wasInitialSystemScan
                ? (_state.SystemSaveDirectories.Any(item => item.GameId == game.Id) ? "首次系统存档目录定位完成，已保存匹配目录" : "首次系统存档目录未匹配，归档时将询问")
                : pendingCount == 0 ? "游戏退出后未发现需要确认的存档变化" : $"发现 {pendingCount} 个待确认存档候选";
            if (pendingCount > 0)
            {
                var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) ?? Application.Current.MainWindow;
                var window = new SaveManagementWindow(_state, message => Save(message), game) { Owner = owner };
                window.Show();
            }
        }
        catch (Exception ex)
        {
            task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now;
            _store.Save(_state); AppLogger.Error($"游戏退出后扫描存档变化失败：{game.DisplayName}", ex); StatusMessage = $"存档变化扫描失败：{ex.Message}";
        }
        finally { _saveScansInProgress.Remove(game.Id); }
    }

    public void OpenSaveManagement(int initialTab, string snapshotFilter = "全部")
    {
        new SaveManagementWindow(_state, message => Save(message), null, initialTab, snapshotFilter) { Owner = Application.Current.MainWindow }.ShowDialog();
        CollectionViewSource.GetDefaultView(Games).Refresh();
    }

    public void OpenSystemSaveDirectories()
    {
        if (_state.Games.Count == 0) { ShowError("游戏库中没有可配置的游戏。 "); return; }
        var choices = _state.Games.OrderBy(game => game.DisplayName).Select(game => new ChoiceItem { Name = game.DisplayName, Description = game.SourcePath, Value = game });
        var choiceWindow = new ChoiceWindow("选择游戏", "请选择需要管理系统存档目录的游戏：", choices) { Owner = Application.Current.MainWindow };
        if (choiceWindow.ShowDialog() != true || choiceWindow.SelectedChoice?.Value is not GameItem game) return;
        new SystemSaveDirectoryWindow(game, _state, message => Save(message)) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    public void OpenBackupManagement(string filter = "全部") => new BackupManagementWindow(_state, message => Save(message), filter) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void OpenBackupSchedule() => new BackupScheduleWindow(_state, message => Save(message)) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void OpenDeletionHistory() => new DeletionHistoryWindow(_state) { Owner = Application.Current.MainWindow }.ShowDialog();

    public void OpenTaskHistory() => new TaskHistoryWindow(_state) { Owner = Application.Current.MainWindow }.ShowDialog();

    private async Task TryRunPendingBackupAsync()
    {
        if (!_state.BackupSettings.PendingScheduledBackup || BackupTargetService.Resolve(_state.BackupSettings, false) is null) return;
        try
        {
            var result = await ExternalBackupService.CreateScheduledFullBackupAsync(_state);
            _store.Save(_state);
            StatusMessage = $"离线备份补执行：{result.Status}";
            await BackupNotificationService.ShowAsync("游戏管理软件备份", result.Message, result.WaitingForTarget ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { AppLogger.Error("主程序启动后补执行等待备份失败", ex); }
    }

    public void OpenSelectedGameDetails() => OpenSelectedGameDetails(null);

    public void OpenSelectedGameDetails(string? initialAction)
    {
        if (SelectedGame is null) { StatusMessage = "请先选择一个游戏"; return; }
        var window = new GameDetailWindow(SelectedGame, _state, message => Save(message), OnGameStateChanged, initialAction) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        LoadCollections();
    }

    public void ExecuteSelectedGameAction(string action)
    {
        if (SelectedGame is null) { StatusMessage = "请先选择一个游戏"; return; }
        var actionHost = new GameDetailWindow(SelectedGame, _state, SaveAndRefreshGameLibrary, OnGameStateChanged, directActionHost: true)
        {
            Owner = Application.Current.MainWindow
        };
        actionHost.ExecuteAction(action);
    }

    public async Task ExecuteGamesActionAsync(IReadOnlyList<GameItem> games, string action)
    {
        if (games.Count == 0 || action is not ("准备游玩" or "归档游戏")) return;
        StatusMessage = $"已将 {games.Count} 个游戏加入“{action}”后台队列";
        var batchContext = new BatchOperationContext { IsBatch = true };
        foreach (var game in games.DistinctBy(item => item.Id))
        {
            var actionHost = new GameDetailWindow(game, _state, SaveAndRefreshGameLibrary, OnGameStateChanged, directActionHost: true, batchContext: batchContext) { Owner = Application.Current.MainWindow };
            await actionHost.ExecuteBackgroundActionAsync(action);
        }
        SaveAndRefreshGameLibrary($"批量{action}队列处理完成");
        MessageBox.Show(Application.Current.MainWindow, $"所选 {games.Count} 个游戏的“{action}”后台队列已经处理完成。", "批量操作完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void OpenExtractionTemplates() => new ExtractionTemplateWindow(_state, message => Save(message)) { Owner = Application.Current.MainWindow }.ShowDialog();

    public IReadOnlyList<ExtractionTemplateItem> ExtractionTemplates => _state.ExtractionTemplates
        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public IReadOnlyList<string> ThemeOptions { get; } = [ThemeNames.Classic, ThemeNames.Windows11];

    public string SelectedTheme
    {
        get => _state.UiSettings.ThemeName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _state.UiSettings.ThemeName) return;
            _state.UiSettings.ThemeName = value;
            ThemeService.Apply(value);
            Raise();
            Save($"界面主题已切换为：{value}");
        }
    }

    public void SetSelectedGameExtractionTemplate(Guid? templateId)
    {
        if (SelectedGame is null) { StatusMessage = "请先选择一个游戏"; return; }
        var template = templateId is Guid id ? _state.ExtractionTemplates.FirstOrDefault(item => item.Id == id) : null;
        if (templateId is not null && template is null) { StatusMessage = "所选解压模板不存在，请刷新后重试"; return; }
        SelectedGame.ExtractionTemplateId = template?.Id;
        SaveAndRefreshGameLibrary(template is null ? "已取消游戏的解压流程模板" : $"已为游戏选择解压流程模板：{template.Name}");
    }

    public void SetGamesExtractionTemplate(IReadOnlyList<GameItem> games, Guid? templateId)
    {
        if (games.Count == 0) { StatusMessage = "请先选择至少一个游戏"; return; }
        var template = templateId is Guid id ? _state.ExtractionTemplates.FirstOrDefault(item => item.Id == id) : null;
        if (templateId is not null && template is null) { StatusMessage = "所选解压模板不存在，请刷新后重试"; return; }
        foreach (var game in games.DistinctBy(item => item.Id)) game.ExtractionTemplateId = template?.Id;
        SaveAndRefreshGameLibrary(template is null ? $"已取消 {games.Count} 个游戏的解压流程模板" : $"已为 {games.Count} 个游戏选择解压流程模板：{template.Name}");
    }

    public void SetSelectedGameHasSystemSave(bool hasSystemSave)
    {
        if (SelectedGame is null) return;
        SelectedGame.HasSystemSave = hasSystemSave;
        if (!hasSystemSave)
        {
            SystemSaveMonitoringService.CancelLatestSession(_state, SelectedGame, "已关闭系统存档扫描");
            _state.SaveCandidates.RemoveAll(item => item.GameId == SelectedGame.Id && item.SourceKind == "系统目录" && item.Decision == SaveCandidateDecisions.Pending);
        }
        SaveAndRefreshGameLibrary(hasSystemSave ? "已启用该游戏的系统存档扫描" : "已标记该游戏不存在系统存档，后续不再扫描系统目录");
    }

    private void SaveAndRefreshGameLibrary(string message)
    {
        _store.Save(_state);
        StatusMessage = message;
        AppLogger.Info(message);

        var existingIds = _state.Games.Select(game => game.Id).ToHashSet();
        foreach (var removed in Games.Where(game => !existingIds.Contains(game.Id)).ToList()) Games.Remove(removed);
        foreach (var added in SortGames(_state.Games).Where(game => Games.All(existing => existing.Id != game.Id))) Games.Add(added);
        var sortedGames = SortGames(Games).ToList();
        for (var index = 0; index < sortedGames.Count; index++)
        {
            var currentIndex = Games.IndexOf(sortedGames[index]);
            if (currentIndex != index) Games.Move(currentIndex, index);
        }
        if (SelectedGame is not null && !existingIds.Contains(SelectedGame.Id)) SelectedGame = null;
        CollectionViewSource.GetDefaultView(Games).Refresh();
    }

    private void OpenGameFolder()
    {
        var path = SelectedGame?.PlayableRootPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { ShowError("可游玩目录不存在。"); return; }
        ShellService.OpenFolder(path);
    }

    private void OpenTaskFolder()
    {
        var path = SelectedTask?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { ShowError("该任务没有可查看的临时目录。"); return; }
        ShellService.OpenFolder(path);
    }

    private void CleanupTaskTemp()
    {
        var task = SelectedTask;
        var path = task?.WorkingDirectory;
        if (task is null || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { ShowError("该任务没有可清理的临时目录。"); return; }
        if (!TemporaryDirectoryService.IsManagedTaskDirectory(path, GameDisks)) { ShowError("安全检查失败：该目录不属于已配置游戏盘的 GameTemp 或 GameSaveTemp 子目录。禁止删除。"); return; }
        if (task.Status == "运行中") { ShowError("运行中的任务禁止清理临时目录。"); return; }
        if (MessageBox.Show($"将永久删除任务临时目录，此操作不可恢复：\n{path}\n\n是否继续？", "清理临时目录确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { Directory.Delete(path, true); task.Message = "任务临时目录已由用户清理。"; task.WorkingDirectory = string.Empty; Save("任务临时目录已清理"); }
        catch (Exception ex) { AppLogger.Error("清理任务临时目录失败", ex); ShowError($"清理失败：{ex.Message}"); }
    }

    private void OpenTaskGame()
    {
        if (SelectedTask?.GameId is not Guid gameId) { ShowError("该任务没有关联游戏。"); return; }
        SelectedGame = Games.FirstOrDefault(game => game.Id == gameId);
        if (SelectedGame is null) { ShowError("关联游戏记录不存在。"); return; }
        OpenSelectedGameDetails();
    }

    private void ShowTaskError()
    {
        if (SelectedTask is null) return;
        var detail = string.IsNullOrWhiteSpace(SelectedTask.ErrorMessage) ? SelectedTask.Message : SelectedTask.ErrorMessage;
        MessageBox.Show(detail, $"任务详情 - {SelectedTask.Name}", MessageBoxButton.OK, SelectedTask.Status == "失败" ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    private void Reload() { _state = _store.Load(); LoadCollections(); _store.Save(_state); StatusMessage = "数据已刷新"; }
    private void Save(string message) { _store.Save(_state); StatusMessage = message; AppLogger.Info(message); }
    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
