using System.Diagnostics;
using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;
using Microsoft.Win32;

namespace GameManagement;

public partial class GameDetailWindow : Window
{
    private readonly GameItem _game;
    private readonly AppState _state;
    private readonly Action<string> _save;
    private readonly Action<GameItem, string> _gameStateChanged;
    private bool _versionSwitchInProgress;

    public GameDetailWindow(GameItem game, AppState state, Action<string> save, Action<GameItem, string> gameStateChanged)
    {
        InitializeComponent();
        _game = game;
        _state = state;
        _save = save;
        _gameStateChanged = gameStateChanged;
        DataContext = game;
        Title = $"游戏详情 - {game.DisplayName}";
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_game.SourcePath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_game.SourcePath}\"") { UseShellExecute = true });
            return;
        }
        if (Directory.Exists(_game.SourcePath)) { ShellService.OpenFolder(_game.SourcePath); return; }
        MessageBox.Show("原始文件或目录不存在。", "路径失效", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenPlayable_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath))
        {
            ShellService.OpenFolder(_game.PlayableRootPath);
            return;
        }
        MessageBox.Show("该游戏尚未准备可游玩目录。", "目录不存在", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowFeature(string name, string description) => new FeatureStatusWindow(name, description, _game) { Owner = this }.ShowDialog();

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        var version = _game.Versions.FirstOrDefault(item => item.Id == _game.CurrentVersionId) ?? _game.Versions.FirstOrDefault();
        if (version is null) { ShowError("当前游戏没有可准备的版本。"); FailPendingVersionSwitch(); return; }
        if (!File.Exists(version.SourcePath) && !Directory.Exists(version.SourcePath)) { ShowError("原始文件或目录不存在，请先执行重新定位。"); FailPendingVersionSwitch(); return; }
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath)) { ShowError("当前游戏已经存在可游玩目录，不允许重复准备。"); FailPendingVersionSwitch(); return; }

        var disks = _state.GameDisks.Where(disk => disk.Enabled && Directory.Exists(disk.RootPath)).OrderByDescending(disk => disk.IsDefault).ThenBy(disk => disk.DisplayName).ToList();
        if (disks.Count == 0) { ShowError("请先在设置中添加并启用至少一个游戏盘。"); FailPendingVersionSwitch(); return; }
        var sourceSize = version.SourceSize > 0 ? version.SourceSize : await Task.Run(() => SpaceEstimationService.GetSourceSize(version.SourcePath));
        var estimatedSpace = SpaceEstimationService.EstimateRequiredSpace(sourceSize);
        var diskChoice = SelectChoice("选择游戏盘", $"请选择本次准备游戏使用的游戏盘。预计至少需要 {Models.SizeFormatter.Format(estimatedSpace)} 临时与解压空间：", disks.Select(disk => new ChoiceItem { Name = disk.IsDefault ? $"{disk.DisplayName}（默认）" : disk.DisplayName, Description = $"{disk.RootPath}（剩余 {disk.FreeSpaceText}，最低保留 {disk.MinimumFreeSpaceText}）", Value = disk }));
        if (diskChoice?.Value is not GameDiskItem disk) { FailPendingVersionSwitch(); return; }
        var availableSpace = SpaceEstimationService.GetAvailableSpace(disk.RootPath);
        if (availableSpace < estimatedSpace + disk.MinimumFreeSpaceBytes)
        {
            ShowError($"目标游戏盘空间不足。\n预计需要：{Models.SizeFormatter.Format(estimatedSpace)}\n最低保留：{Models.SizeFormatter.Format(disk.MinimumFreeSpaceBytes)}\n当前可用：{Models.SizeFormatter.Format(availableSpace)}");
            FailPendingVersionSwitch();
            return;
        }

        var requiresSaveCopy = _game.HasLocalSave && CrossDiskSaveCopyService.RequiresCopy(_state, _game, disk);
        if (requiresSaveCopy && CrossDiskSaveCopyService.TargetCurrentExists(_game, disk)
            && MessageBox.Show("目标游戏盘已经存在该游戏的 current 存档。按照需求，复制时将保留来源存档并覆盖目标存档。是否继续？", "跨游戏盘存档覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { FailPendingVersionSwitch(); return; }
        if (_game.HasLocalSave)
        {
            try
            {
                var manifest = await CurrentSaveManifestService.LoadAsync(_state, _game);
                if (manifest is not null && manifest.GameVersionId != Guid.Empty && manifest.GameVersionId != version.Id
                    && MessageBox.Show($"当前共享存档来自其他版本。\n\n来源版本 ID：{manifest.GameVersionId}\n目标版本：{version.VersionName}\n\n跨版本恢复可能不兼容，是否继续？", "共享存档兼容风险", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { FailPendingVersionSwitch(); return; }
            }
            catch (Exception ex) { ShowError($"读取共享存档清单失败：{ex.Message}"); FailPendingVersionSwitch(); return; }
        }

        var workRoot = Path.Combine(disk.RootPath, "GameTemp", _game.Id.ToString(), version.Id.ToString());
        var sourceRoot = Path.Combine(workRoot, "source");
        var step1 = Path.Combine(workRoot, "step1");
        var step2 = Path.Combine(workRoot, "step2");
        var stagedFinal = Path.Combine(workRoot, "final");
        var finalTarget = Path.Combine(disk.RootPath, "Games", _game.Id.ToString());
        if (Directory.Exists(finalTarget)) { ShowError($"目标游戏目录已经存在：\n{finalTarget}"); FailPendingVersionSwitch(); return; }
        if (Directory.Exists(workRoot))
        {
            if (MessageBox.Show($"检测到上次失败、中断或取消后保留的临时目录：\n{workRoot}\n\n重新准备需要永久删除该临时目录，是否继续？", "临时目录恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { FailPendingVersionSwitch(); return; }
            try { Directory.Delete(workRoot, true); }
            catch (Exception ex) { ShowError($"无法清理旧临时目录：{ex.Message}"); FailPendingVersionSwitch(); return; }
        }

        using var cancellation = new CancellationTokenSource();
        var task = new OperationTaskItem { Name = $"准备游戏：{_game.DisplayName}", TaskType = "准备游玩", GameId = _game.Id, GameVersionId = version.Id, Status = "运行中", Message = "正在初始化准备任务", WorkingDirectory = workRoot };
        _state.OperationTasks.Add(task); _save("准备游玩任务已创建");
        var progress = new PreparationProgressWindow { Owner = this };
        progress.EnableCancellation(cancellation.Cancel);
        progress.Show(); IsEnabled = false;
        var finalCommitted = false;
        try
        {
            if (requiresSaveCopy)
            {
                UpdatePreparationTask(task, progress, 3, "正在复制并校验跨游戏盘主存档…", GameSavePathService.GetCurrentDirectory(_state, _game), "复制中");
                await CrossDiskSaveCopyService.CopyAsync(_state, _game, disk, cancellation.Token);
                _save("跨游戏盘主存档复制和校验完成，来源存档已保留");
            }
            if (string.IsNullOrWhiteSpace(version.SourceFingerprint))
            {
                UpdatePreparationTask(task, progress, 5, "正在为旧版本补充原始来源指纹…", version.SourcePath, "复制中");
                var sourceProgress = new Progress<SourceMetadataProgress>(value => progress.UpdateStatus($"正在记录原始来源指纹（{value.Percentage}%）：{value.CurrentPath}"));
                var sourceMetadata = await SourceMetadataService.CaptureAsync(version.SourcePath, sourceProgress, cancellation.Token);
                SourceMetadataService.Apply(version, sourceMetadata);
                _save("已补充当前版本的原始来源指纹");
            }

            UpdatePreparationTask(task, progress, 10, "正在复制原始文件到游戏盘临时目录…", version.SourcePath, "复制中");
            var copiedSource = await SourceCopyService.CopyToWorkDirectoryAsync(version.SourcePath, sourceRoot, cancellation.Token);

            UpdatePreparationTask(task, progress, 25, "正在扫描第一次解压候选…", copiedSource, "第一次解压中");
            var firstGroups = await Task.Run(() => ArchiveVolumeService.DiscoverGroups(copiedSource, cancellation.Token), cancellation.Token);
            if (firstGroups.Count == 0) throw new InvalidOperationException("原始来源中没有发现 ZIP、RAR 或可识别的分卷组。");
            progress.Hide(); IsEnabled = true;
            var firstGroup = SelectArchiveGroupWithHistory("选择第一次解压文件", "请选择第一次解压使用的 ZIP、RAR 或分卷组：", firstGroups, copiedSource, version.FirstArchiveRelativePath, "第一次解压");
            if (firstGroup is null) throw new OperationCanceledException("用户取消了第一次解压文件选择。", cancellation.Token);
            ConfirmMissingVolumes(firstGroup, "第一次解压");
            var firstArchive = firstGroup.EntryPath;
            var firstRelativePath = GetArchiveRelativePath(copiedSource, firstArchive);
            IsEnabled = false; progress.Show();
            UpdatePreparationTask(task, progress, 35, $"正在执行第一次解压：{Path.GetFileName(firstArchive)}", firstArchive, "第一次解压中");
            await ExtractWithCredentialAsync(version, firstArchive, step1, "第一次解压密码", progress, cancellation.Token, 1, firstRelativePath, firstGroup.MissingFiles.Count == 0);
            version.FirstArchiveRelativePath = firstRelativePath;
            version.FirstArchiveFormat = firstGroup.Format;

            UpdatePreparationTask(task, progress, 50, "正在扫描第二次解压候选…", step1, "第二次解压中");
            var finalExtractionRoot = step2;
            var replayedFallback = false;
            var recordedFallbackPath = version.SecondArchiveUsedFallback ? ResolveRecordedArchive(step1, version.SecondArchiveRelativePath) : null;
            var replayRecordedFallback = false;
            if (recordedFallbackPath is not null && File.Exists(recordedFallbackPath) && !string.IsNullOrWhiteSpace(version.SecondArchiveFormat))
            {
                progress.Hide(); IsEnabled = true;
                replayRecordedFallback = ConfirmHistoryReplay("第二次解压", recordedFallbackPath);
                IsEnabled = false; progress.Show();
            }
            if (replayRecordedFallback && recordedFallbackPath is not null)
            {
                var replayArchive = RenameForAttempt(recordedFallbackPath, version.SecondArchiveFormat.Equals("ZIP", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".rar");
                UpdatePreparationTask(task, progress, 65, $"正在重放历史第二次解压：{Path.GetFileName(replayArchive)}", replayArchive, "第二次解压中");
                await ExtractWithCredentialAsync(version, replayArchive, step2, "第二次解压密码", progress, cancellation.Token, 2, version.SecondArchiveRelativePath ?? Path.GetFileName(recordedFallbackPath), true);
                replayedFallback = true;
            }

            if (!replayedFallback)
            {
                var secondGroups = await Task.Run(() => ArchiveVolumeService.DiscoverGroups(step1, cancellation.Token), cancellation.Token);
                if (secondGroups.Count > 0)
                {
                    progress.Hide(); IsEnabled = true;
                    var secondGroup = SelectArchiveGroupWithHistory("选择第二次解压文件", "请选择第二次解压使用的 ZIP、RAR 或分卷组：", secondGroups, step1, version.SecondArchiveUsedFallback ? null : version.SecondArchiveRelativePath, "第二次解压");
                    if (secondGroup is null) throw new OperationCanceledException("用户取消了第二次解压文件选择。", cancellation.Token);
                    ConfirmMissingVolumes(secondGroup, "第二次解压");
                    var originalSecondArchive = secondGroup.EntryPath;
                    var secondRelativePath = Path.GetRelativePath(step1, originalSecondArchive);
                    var detectedFormat = secondGroup.Format;
                    IsEnabled = false; progress.Show();
                    var workingSecondArchive = originalSecondArchive;
                    if (!secondGroup.IsMultiVolume)
                    {
                        UpdatePreparationTask(task, progress, 60, $"正在定位真实 {detectedFormat} 数据并生成规范化临时文件…", originalSecondArchive, "第二次解压中");
                        workingSecondArchive = await ArchiveDiscoveryService.CreateNormalizedWorkingArchiveAsync(originalSecondArchive, detectedFormat, cancellation.Token);
                    }
                    UpdatePreparationTask(task, progress, 65, $"正在执行第二次解压：{Path.GetFileName(workingSecondArchive)}", workingSecondArchive, "第二次解压中");
                    await ExtractWithCredentialAsync(version, workingSecondArchive, step2, "第二次解压密码", progress, cancellation.Token, 2, secondRelativePath, secondGroup.MissingFiles.Count == 0);
                    version.SecondArchiveRelativePath = secondRelativePath;
                    version.SecondArchiveFormat = detectedFormat;
                    version.SecondArchiveUsedFallback = false;
                }
                else
                {
                    var largestFile = await Task.Run(() => ArchiveDiscoveryService.FindLargestUnrecognizedFile(step1, cancellation.Token), cancellation.Token);
                    if (largestFile is null) throw new InvalidOperationException("第一次解压结果中没有找到第二次压缩文件，也没有可以尝试改名的文件。");
                    var originalRelativePath = Path.GetRelativePath(step1, largestFile);
                    UpdatePreparationTask(task, progress, 60, $"未识别到压缩格式，准备尝试最大文件：{Path.GetFileName(largestFile)}", largestFile, "第二次解压中");
                    var attemptedArchive = await TryLargestFileAsArchiveAsync(version, largestFile, step2, progress, cancellation.Token, originalRelativePath);
                    version.SecondArchiveRelativePath = originalRelativePath;
                    version.SecondArchiveFormat = attemptedArchive.Format;
                    version.SecondArchiveUsedFallback = true;
                }
            }

            UpdatePreparationTask(task, progress, 75, "正在递归查找第一个有效 EXE 并确定游戏目录…", finalExtractionRoot, "等待确认游戏目录");
            var launchDiscovery = await Task.Run(() => ExecutableDiscoveryService.Discover(finalExtractionRoot, cancellation.Token), cancellation.Token);
            progress.Hide(); IsEnabled = true;
            var launchSelection = SelectLaunchFile(finalExtractionRoot, launchDiscovery);
            if (launchSelection is null) throw new OperationCanceledException("用户取消了启动文件选择。", cancellation.Token);
            var launchFile = launchSelection.LaunchFile;
            var selectedGameRoot = launchSelection.GameRoot;

            IsEnabled = false; progress.Show(); UpdatePreparationTask(task, progress, 85, "正在整理最终游戏目录…", selectedGameRoot, "等待确认游戏目录");
            await SourceCopyService.CopyDirectoryAsync(selectedGameRoot, stagedFinal, cancellation.Token);
            var executableRelativePath = Path.GetRelativePath(selectedGameRoot, launchFile);

            UpdatePreparationTask(task, progress, 92, "正在计算游戏文件 SHA-256 基线，这可能需要较长时间…", stagedFinal, "等待确认游戏目录");
            var baselines = await BaselineService.BuildAsync(_game.Id, version.Id, stagedFinal, cancellation.Token);
            _state.FileBaselines.RemoveAll(item => item.GameVersionId == version.Id);
            _state.FileBaselines.AddRange(baselines);

            cancellation.Token.ThrowIfCancellationRequested();
            Directory.Move(stagedFinal, finalTarget);
            finalCommitted = true;
            var finalExecutable = Path.Combine(finalTarget, executableRelativePath);

            if (_game.HasLocalSave)
            {
                UpdatePreparationTask(task, progress, 96, "正在恢复共享存档并校验目标文件…", finalTarget, "等待确认游戏目录");
                var restore = await SaveRestoreService.RestoreCurrentAsync(_state, _game, version, finalTarget, cancellation.Token);
                if (restore is not null) task.Message = $"已恢复 {restore.GameFileCount} 个游戏目录存档和 {restore.SystemFileCount} 个系统目录存档";
            }

            version.GameDiskId = disk.Id;
            _game.CurrentGameDiskId = disk.Id;
            _game.PlayableRootPath = finalTarget;
            _game.ExecutableRelativePath = Path.GetRelativePath(finalTarget, finalExecutable);
            _game.IconRelativePath = IconExtractionService.ExtractToCache(finalExecutable, _game.Id);
            ConfirmDirectoryNameAsNote(selectedGameRoot);
            _game.Status = "可游玩";
            _game.ArchiveStatus = "未归档";
            _game.DirectoryCleanupStatus = "目录存在";
            _game.ArchiveMessage = string.Empty;
            task.Status = "完成"; task.Progress = 100; task.Message = $"游戏准备完成，共建立 {baselines.Count} 个文件基线"; task.CurrentPath = finalTarget; task.CompletedAt = DateTime.Now;
            _save(task.Message);
            _versionSwitchInProgress = false;
            RefreshBindings();

            progress.UpdateStatus("正在自动清理成功任务的临时目录…", 100);
            try
            {
                if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true);
                task.WorkingDirectory = string.Empty; _save("成功任务临时目录已自动清理");
                MessageBox.Show("游戏准备完成。", "准备游玩", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception cleanupError)
            {
                task.Message = "游戏准备完成，但临时目录自动清理失败，可在任务中心手动清理。";
                task.ErrorMessage = cleanupError.ToString(); _save(task.Message); AppLogger.Error("成功任务临时目录清理失败", cleanupError);
                MessageBox.Show($"游戏准备已经完成，但临时目录清理失败：{cleanupError.Message}\n\n可在任务中心稍后手动清理。", "准备完成，清理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException ex)
        {
            RollbackFinalCommit(finalTarget, stagedFinal, finalCommitted);
            task.Status = "已取消"; task.Message = "用户取消了准备任务，临时目录已保留。"; task.ErrorMessage = ex.Message; task.CompletedAt = DateTime.Now;
            _game.Status = _versionSwitchInProgress ? "版本切换失败" : "未准备"; _versionSwitchInProgress = false; _save(task.Message);
            MessageBox.Show($"准备任务已取消。\n\n临时目录已保留：\n{workRoot}", "任务已取消", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            RollbackFinalCommit(finalTarget, stagedFinal, finalCommitted);
            task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now;
            _game.Status = _versionSwitchInProgress ? "版本切换失败" : "操作失败"; _versionSwitchInProgress = false; _save("准备任务失败，临时目录已保留");
            AppLogger.Error($"准备游戏失败：{_game.DisplayName}", ex);
            MessageBox.Show($"游戏准备失败：{ex.Message}\n\n临时目录已保留：\n{workRoot}", "准备失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            progress.CloseSafely();
        }
    }
    private async void Archive_Click(object sender, RoutedEventArgs e) => await ArchiveCurrentAsync(false);
    private void SpecialArchive_Click(object sender, RoutedEventArgs e) => ShowFeature("特殊归档", "选择已有的混乱游戏目录，与从原始压缩文件构建的干净基准目录比较后提取存档。");
    private void ManualBackup_Click(object sender, RoutedEventArgs e) => ShowFeature("手动备份", "将当前游戏的本地存档创建为无密码 ZIP，完成文件清单和 Hash 校验。");
    private void SaveDirectories_Click(object sender, RoutedEventArgs e) => new SystemSaveDirectoryWindow(_game, _state, _save) { Owner = this }.ShowDialog();
    private void Snapshots_Click(object sender, RoutedEventArgs e) => new SaveManagementWindow(_state, _save, _game) { Owner = this }.ShowDialog();
    private async void Versions_Click(object sender, RoutedEventArgs e)
    {
        var requested = OpenVersionManagement(null);
        if (requested is not null) await SwitchAndPrepareAsync(requested);
    }
    private void Credentials_Click(object sender, RoutedEventArgs e) => new CredentialManagementWindow(_game, _state, _save) { Owner = this }.ShowDialog();
    private async void RecognizeGame_Click(object sender, RoutedEventArgs e)
    {
        if (_game.Status == "运行中") { ShowError("游戏运行期间不允许修改启动文件。"); return; }
        if (string.IsNullOrWhiteSpace(_game.PlayableRootPath) || !Directory.Exists(_game.PlayableRootPath)) { ShowError("可游玩目录不存在，请先准备游戏。"); return; }
        try
        {
            IsEnabled = false;
            var discovery = await Task.Run(() => ExecutableDiscoveryService.Discover(_game.PlayableRootPath));
            IsEnabled = true;
            var selection = SelectLaunchFile(_game.PlayableRootPath, discovery);
            if (selection is null) return;
            _game.ExecutableRelativePath = Path.GetRelativePath(_game.PlayableRootPath, selection.LaunchFile);
            _game.IconRelativePath = IconExtractionService.ExtractToCache(selection.LaunchFile, _game.Id);
            ConfirmDirectoryNameAsNote(selection.GameRoot);
            _save("游戏目录、启动文件和图标已重新识别");
            RefreshBindings();
        }
        catch (Exception ex) { AppLogger.Error("重新识别游戏启动文件失败", ex); ShowError(ex.Message); }
        finally { IsEnabled = true; }
    }
    private void RelocateSource_Click(object sender, RoutedEventArgs e) => OpenVersionManagement(_game.CurrentVersionId);
    private void DeleteSource_Click(object sender, RoutedEventArgs e) => ShowFeature("删除原始文件", "该操作最终需要两次人工确认，并且只能将原始压缩文件和相关分卷移入 Windows 回收站。");

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_game.Status == "运行中") { ShowError("该游戏的主程序已经在运行。 "); return; }
        var progressWindow = new PreparationProgressWindow("正在建立系统存档监控快照") { Owner = this };
        using var cancellation = new CancellationTokenSource();
        progressWindow.EnableCancellation(cancellation.Cancel);
        try
        {
            IsEnabled = false; progressWindow.Show();
            var progress = new Progress<SystemMonitorProgress>(value => progressWindow.UpdateStatus($"已扫描 {value.FileCount} 个系统目录文件：{value.CurrentPath}", 50));
            var session = await SystemSaveMonitoringService.BeginSessionAsync(_state, _game, progress, cancellation.Token);
            _save(session is null ? "未配置可用的系统存档目录，本次只监控游戏目录" : "游戏启动前的系统存档监控快照已建立");
            progressWindow.CloseSafely(); IsEnabled = true;
            GameProcessMonitorService.Launch(_game, (game, message) =>
            {
                _gameStateChanged(game, message);
                Dispatcher.BeginInvoke(() => { if (IsLoaded) RefreshBindings(); });
            });
            RefreshBindings();
        }
        catch (OperationCanceledException) { SystemSaveMonitoringService.CancelLatestSession(_state, _game, "已取消"); _save("游戏启动前的系统存档扫描已取消"); }
        catch (Exception ex) { SystemSaveMonitoringService.CancelLatestSession(_state, _game, "启动失败"); _save("游戏启动失败，系统存档监控快照已取消"); MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; progressWindow.CloseSafely(); }
    }

    private void EditGame_Click(object sender, RoutedEventArgs e)
    {
        var nameWindow = new TextInputWindow("编辑游戏名称", "请输入游戏显示名称：", _game.DisplayName) { Owner = this };
        if (nameWindow.ShowDialog() != true) return;
        var noteWindow = new TextInputWindow("编辑游戏备注", "请输入备注；允许留空：", _game.Note, true) { Owner = this };
        if (noteWindow.ShowDialog() != true) return;
        _game.DisplayName = nameWindow.Value.Trim(); _game.Note = noteWindow.Value.Trim();
        _save("游戏名称与备注已更新"); RefreshBindings();
    }

    private GameVersionItem? OpenVersionManagement(Guid? selectedVersionId)
    {
        var window = new VersionManagementWindow(_game, _state, _save, selectedVersionId) { Owner = this };
        window.ShowDialog();
        RefreshBindings();
        return window.RequestedPreparationVersion;
    }

    private async Task SwitchAndPrepareAsync(GameVersionItem targetVersion)
    {
        if (_versionSwitchInProgress) { ShowError("当前游戏已经有版本切换任务正在执行。"); return; }
        if (_game.Status == "运行中") { ShowError("主游戏程序仍在运行，禁止切换版本。"); return; }
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath))
        {
            if (!await ArchiveCurrentAsync(true)) return;
        }
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath)) { ShowError("当前版本目录尚未成功移入回收站，版本切换已终止。"); return; }
        _versionSwitchInProgress = true;
        _game.CurrentVersionId = targetVersion.Id;
        _game.CurrentVersionName = targetVersion.VersionName;
        _game.SourcePath = targetVersion.SourcePath;
        _game.SourceKind = targetVersion.SourceKind;
        _game.Status = "未准备";
        _save($"开始切换并准备版本：{targetVersion.VersionName}");
        RefreshBindings();
        Prepare_Click(this, new RoutedEventArgs());
    }

    private async Task<bool> ArchiveCurrentAsync(bool requireDirectoryRemoval)
    {
        if (_game.Status == "运行中" || _game.RunningProcessId.HasValue) { ShowError("主游戏程序仍在运行，禁止归档。"); return false; }
        if (string.IsNullOrWhiteSpace(_game.PlayableRootPath) || !Directory.Exists(_game.PlayableRootPath)) { ShowError("可游玩目录不存在，无法执行普通归档。"); return false; }
        var task = new OperationTaskItem { Name = $"普通归档：{_game.DisplayName}", TaskType = "普通归档", GameId = _game.Id, GameVersionId = _game.CurrentVersionId, Status = "运行中", Message = "正在检查游戏目录存档变化", CurrentPath = _game.PlayableRootPath };
        _state.OperationTasks.Add(task); _game.Status = "归档中"; _save("普通归档任务已创建"); RefreshBindings();
        try
        {
            var candidates = await SaveChangeDetectionService.DetectAsync(_state, _game, SaveSnapshotKinds.Normal);
            SaveChangeDetectionService.ReplaceDetectedCandidates(_state, _game, candidates);
            var automatic = candidates.Where(item => item.PreviouslyConfirmed && item.ChangeType != "删除" && item.Decision == SaveCandidateDecisions.Pending).Select(item => item.Id).ToList();
            if (automatic.Count > 0) await SaveSnapshotService.ApplyAndCreateAsync(_state, _game, automatic);
            _save("普通归档前游戏目录存档扫描完成");
            if (_state.SaveCandidates.Any(item => item.GameId == _game.Id && item.Decision == SaveCandidateDecisions.Pending))
            {
                new SaveManagementWindow(_state, _save, _game) { Owner = this }.ShowDialog();
            }

            var readiness = await OrdinaryArchiveService.CheckReadinessAsync(_state, _game);
            if (!readiness.Ready)
            {
                task.Status = "等待"; task.Message = string.Join("；", readiness.Problems); task.CompletedAt = DateTime.Now;
                _game.Status = "可游玩"; _save("普通归档条件尚未满足"); RefreshBindings();
                var needsBackup = readiness.Problems.Any(item => item.Contains("外部 ZIP 备份", StringComparison.Ordinal));
                MessageBox.Show($"当前不能完成归档：\n\n- {string.Join("\n- ", readiness.Problems)}{(needsBackup ? "\n\n请先使用“手动备份”创建并校验最新存档的外部 ZIP。" : string.Empty)}", "归档条件未满足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            OrdinaryArchiveService.MarkArchived(_game, readiness.Manifest!);
            task.Progress = 80; task.Message = "归档条件已满足，游戏已标记为已归档"; _save(task.Message); RefreshBindings();
            var delete = MessageBox.Show($"游戏已经成功归档。是否将当前可游玩目录移入 Windows 回收站以释放空间？\n\n{_game.PlayableRootPath}", "归档目录清理确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!delete)
            {
                task.Status = "完成"; task.Progress = 100; task.Message = "归档完成，游戏目录按用户选择保留"; task.CompletedAt = DateTime.Now; _save(task.Message); RefreshBindings();
                if (requireDirectoryRemoval) MessageBox.Show("版本切换要求先清理当前可游玩目录，本次切换已终止；归档状态仍然有效。", "版本切换已终止", MessageBoxButton.OK, MessageBoxImage.Information);
                return !requireDirectoryRemoval;
            }
            var playable = _game.PlayableRootPath!;
            try
            {
                RecycleBinService.MoveDirectory(playable);
                OrdinaryArchiveService.MarkCleanupSucceeded(_game);
                task.Status = "完成"; task.Progress = 100; task.Message = "归档完成，游戏目录已移入回收站"; task.CompletedAt = DateTime.Now; _save(task.Message); RefreshBindings();
                return true;
            }
            catch (Exception ex)
            {
                OrdinaryArchiveService.MarkCleanupFailed(_game, ex.Message);
                task.Status = "完成"; task.Progress = 100; task.Message = "归档完成，但游戏目录清理失败"; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now; _save(task.Message); RefreshBindings();
                MessageBox.Show($"存档归档已经完成，但目录未能移入回收站：{ex.Message}\n\n归档状态不受影响。", "目录清理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        catch (Exception ex)
        {
            task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now;
            _game.Status = "操作失败"; _save("普通归档失败"); RefreshBindings(); AppLogger.Error($"普通归档失败：{_game.DisplayName}", ex); ShowError(ex.Message); return false;
        }
    }

    private static void RollbackFinalCommit(string finalTarget, string stagedFinal, bool finalCommitted)
    {
        if (!finalCommitted || !Directory.Exists(finalTarget)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedFinal)!);
            if (Directory.Exists(stagedFinal)) Directory.Delete(stagedFinal, true);
            Directory.Move(finalTarget, stagedFinal);
        }
        catch (Exception ex) { AppLogger.Error($"准备失败后回收最终目录失败：{finalTarget}", ex); }
    }

    private void FailPendingVersionSwitch()
    {
        if (!_versionSwitchInProgress) return;
        _versionSwitchInProgress = false;
        _game.Status = "版本切换失败";
        _save("版本切换未能进入准备流程");
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        DataContext = null; DataContext = _game; Title = $"游戏详情 - {_game.DisplayName}";
    }

    private ChoiceItem? SelectChoice(string title, string prompt, IEnumerable<ChoiceItem> choices)
    {
        var window = new ChoiceWindow(title, prompt, choices) { Owner = this };
        return window.ShowDialog() == true ? window.SelectedChoice : null;
    }

    private ArchiveVolumeGroup? SelectArchiveGroupWithHistory(string title, string prompt, IReadOnlyList<ArchiveVolumeGroup> groups, string sourceRoot, string? recordedRelativePath, string stepName)
    {
        var recordedPath = ResolveRecordedArchive(sourceRoot, recordedRelativePath);
        var recordedGroup = recordedPath is null ? null : groups.FirstOrDefault(group => group.Files.Any(path => PathsEqual(path, recordedPath)) || PathsEqual(group.EntryPath, recordedPath));
        if (recordedGroup is not null && ConfirmHistoryReplay(stepName, recordedGroup.EntryPath)) return recordedGroup;

        var choices = groups.Select(group => new ChoiceItem
        {
            Name = Path.GetFileName(group.EntryPath),
            Description = $"{group.Format}｜{group.VolumeSummary}｜{Models.SizeFormatter.Format(group.TotalSize)}｜{(group.MissingFiles.Count == 0 ? "分卷完整" : $"缺失 {group.MissingFiles.Count} 卷")}｜{group.EntryPath}",
            Value = group
        });
        return SelectChoice(title, prompt, choices)?.Value as ArchiveVolumeGroup;
    }

    private bool ConfirmHistoryReplay(string stepName, string archivePath) => MessageBox.Show($"检测到上次成功使用的{stepName}文件：\n{archivePath}\n\n是否重放历史选择和已保存密码？", "历史解压流程", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void ConfirmMissingVolumes(ArchiveVolumeGroup group, string stepName)
    {
        if (group.MissingFiles.Count == 0) return;
        var missing = string.Join("\n", group.MissingFiles.Select(Path.GetFileName));
        var result = MessageBox.Show($"{stepName}分卷组存在可推断的缺失文件：\n{missing}\n\n按照需求，只允许在人工确认后尝试解压一次；失败后立即停止，不重试密码，也不继续后续步骤。是否仍要尝试？", "分卷缺失确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) throw new OperationCanceledException("用户取消了缺失分卷解压尝试。");
    }

    private static string GetArchiveRelativePath(string sourceRoot, string archivePath) => File.Exists(sourceRoot) ? Path.GetFileName(archivePath) : Path.GetRelativePath(sourceRoot, archivePath);

    private static string? ResolveRecordedArchive(string sourceRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        if (File.Exists(sourceRoot)) return Path.GetFileName(sourceRoot).Equals(Path.GetFileName(relativePath), StringComparison.OrdinalIgnoreCase) ? sourceRoot : null;
        var path = Path.GetFullPath(Path.Combine(sourceRoot, relativePath));
        return File.Exists(path) ? path : null;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private GameLaunchSelection? SelectLaunchFile(string searchRoot, GameLaunchDiscoveryResult? discovery)
    {
        if (discovery is null)
        {
            if (MessageBox.Show("自动识别没有找到有效 EXE。是否手动选择 EXE 或 index.html？", "自动识别未找到结果", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return null;
            return SelectManualLaunchFile(searchRoot);
        }

        var choices = discovery.Candidates.Select((candidate, index) => new ChoiceItem
        {
            Name = $"{(index == 0 ? "推荐｜" : string.Empty)}{Path.GetFileName(candidate.Path)}",
            Description = $"评分 {candidate.Score}｜{string.Join("、", candidate.Reasons)}｜{Models.SizeFormatter.Format(new FileInfo(candidate.Path).Length)}｜{Path.GetRelativePath(discovery.GameRoot, candidate.Path)}",
            Value = candidate.Path
        });
        var window = new ChoiceWindow("选择游戏启动文件", $"已确定游戏目录：\n{discovery.GameRoot}\n\n候选按评分从高到低排列。请选择目录直属的 EXE/index.html，或手动选择文件：", choices, true) { Owner = this };
        if (window.ShowDialog() != true) return null;
        if (window.ManualSelectionRequested) return SelectManualLaunchFile(searchRoot);
        return window.SelectedChoice?.Value is string launchFile ? new GameLaunchSelection(discovery.GameRoot, launchFile) : null;
    }

    private GameLaunchSelection? SelectManualLaunchFile(string searchRoot)
    {
        var dialog = new OpenFileDialog
        {
            Title = "手动选择游戏启动文件",
            InitialDirectory = searchRoot,
            Filter = "游戏启动文件 (*.exe;index.html)|*.exe;index.html|EXE 文件 (*.exe)|*.exe|网页入口 (index.html)|index.html",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return null;
        var selectedPath = Path.GetFullPath(dialog.FileName);
        if (!IsPathWithinRoot(searchRoot, selectedPath)) { ShowError("启动文件必须位于本次识别的游戏目录范围内。"); return null; }
        var isValid = Path.GetExtension(selectedPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(selectedPath).Equals("index.html", StringComparison.OrdinalIgnoreCase);
        if (!isValid) { ShowError("只允许选择 EXE 文件或文件名为 index.html 的网页入口。"); return null; }
        return new GameLaunchSelection(Path.GetDirectoryName(selectedPath)!, selectedPath);
    }

    private void ConfirmDirectoryNameAsNote(string gameRoot)
    {
        if (!string.IsNullOrWhiteSpace(_game.Note)) return;
        var directoryName = new DirectoryInfo(gameRoot).Name;
        if (MessageBox.Show($"当前游戏备注为空，是否使用识别到的目录名称作为备注？\n\n{directoryName}", "确认游戏备注", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) _game.Note = directoryName;
    }

    private static bool IsPathWithinRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private async Task ExtractWithCredentialAsync(GameVersionItem version, string archivePath, string outputDirectory, string title, PreparationProgressWindow progress, CancellationToken token, int stepOrder, string archiveRelativePath, bool allowPasswordRetry)
    {
        var fingerprint = await FileFingerprintService.ComputeSha256Async(archivePath, token);
        var password = CredentialService.FindPassword(_state, version.Id, fingerprint);
        if (password is null)
        {
            password = PromptPassword(title, archivePath, null, progress);
            if (password is null) throw new OperationCanceledException("用户取消了密码输入。");
        }

        try
        {
            await ArchiveExtractionService.ExtractAsync(archivePath, outputDirectory, password, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!allowPasswordRetry)
        {
            throw new InvalidDataException($"分卷存在缺失，本次已按人工确认尝试一次并失败，不再重试：{ex.Message}", ex);
        }
        catch
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
            Directory.CreateDirectory(outputDirectory);
            var replacement = PromptPassword($"{title}验证失败", archivePath, password, progress);
            if (replacement is null) throw;
            password = replacement;
            await ArchiveExtractionService.ExtractAsync(archivePath, outputDirectory, password, token);
        }
        CredentialService.SavePassword(_state, version.Id, fingerprint, password, stepOrder, Path.GetFileName(archivePath), archiveRelativePath, DateTime.Now);
    }

    private async Task<(string Path, string Format)> TryLargestFileAsArchiveAsync(GameVersionItem version, string largestFile, string outputDirectory, PreparationProgressWindow progress, CancellationToken token, string originalRelativePath)
    {
        var currentPath = largestFile;
        Exception? zipError = null;
        try
        {
            currentPath = RenameForAttempt(currentPath, ".zip");
            progress.UpdateStatus($"正在将最大文件作为 ZIP 尝试第二次解压：{Path.GetFileName(currentPath)}");
            await ExtractWithCredentialAsync(version, currentPath, outputDirectory, "第二次解压密码（ZIP 尝试）", progress, token, 2, originalRelativePath, true);
            return (currentPath, "ZIP");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            zipError = ex;
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
        }

        try
        {
            currentPath = RenameForAttempt(currentPath, ".rar");
            progress.UpdateStatus($"ZIP 尝试失败，正在将最大文件作为 RAR 尝试：{Path.GetFileName(currentPath)}");
            await ExtractWithCredentialAsync(version, currentPath, outputDirectory, "第二次解压密码（RAR 尝试）", progress, token, 2, originalRelativePath, true);
            return (currentPath, "RAR");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception rarError)
        {
            throw new InvalidDataException($"最大文件依次按 ZIP、RAR 尝试解压均失败。ZIP：{zipError?.Message}；RAR：{rarError.Message}", rarError);
        }
    }

    private static string RenameForAttempt(string path, string extension)
    {
        if (Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) return path;
        var target = Path.ChangeExtension(path, extension);
        if (File.Exists(target)) File.Delete(target);
        File.Move(path, target);
        return target;
    }

    private string? PromptPassword(string title, string archivePath, string? existingPassword, PreparationProgressWindow progress)
    {
        progress.Hide(); IsEnabled = true;
        try
        {
            var window = new PasswordInputWindow(title, Path.GetFileName(archivePath), existingPassword) { Owner = this };
            return window.ShowDialog() == true ? window.Password : null;
        }
        finally { IsEnabled = false; progress.Show(); }
    }

    private void UpdatePreparationTask(OperationTaskItem task, PreparationProgressWindow progress, int percentage, string message, string currentPath, string gameStatus)
    {
        task.Progress = percentage; task.Message = message; task.CurrentPath = currentPath; _game.Status = gameStatus;
        progress.UpdateStatus(message, percentage); _save($"准备任务进度：{percentage}%");
    }

    private static void ShowError(string message) => MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
}
