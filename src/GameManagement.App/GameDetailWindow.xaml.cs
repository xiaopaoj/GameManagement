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


    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        var version = _game.Versions.FirstOrDefault(item => item.Id == _game.CurrentVersionId) ?? _game.Versions.FirstOrDefault();
        if (version is null) { ShowError("当前游戏没有可准备的版本。"); FailPendingVersionSwitch(); return; }
        if (!File.Exists(version.SourcePath) && !Directory.Exists(version.SourcePath)) { ShowError("原始文件或目录不存在，请先执行重新定位。"); FailPendingVersionSwitch(); return; }
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath)) { ShowError("当前游戏已经存在可游玩目录，不允许重复准备。"); FailPendingVersionSwitch(); return; }

        var disks = _state.GameDisks.Where(disk => disk.Enabled && Directory.Exists(disk.RootPath)).OrderByDescending(disk => disk.IsDefault).ThenBy(disk => disk.DisplayName).ToList();
        if (disks.Count == 0) { ShowError("请先在设置中添加并启用至少一个游戏盘。"); FailPendingVersionSwitch(); return; }
        var estimate = await SpaceEstimationService.EstimateForSourceAsync(version.SourcePath);
        var estimatedSpace = estimate.TotalBytes;
        var estimateDetail = estimate.ContentMetadataAvailable
            ? $"来源复制 {Models.SizeFormatter.Format(estimate.SourceCopyBytes)} + 首层展开 {Models.SizeFormatter.Format(estimate.FirstExtractionBytes)} + 二次解压与最终目录预留 {Models.SizeFormatter.Format(estimate.SecondExtractionAndFinalReserveBytes)} + 安全余量 {Models.SizeFormatter.Format(estimate.SafetyReserveBytes)}"
            : $"无法读取压缩条目元数据，已按来源体积四倍安全回退（来源 {Models.SizeFormatter.Format(estimate.SourceCopyBytes)}）";
        var diskChoice = SelectChoice("选择游戏盘", $"请选择本次准备游戏使用的游戏盘。预计至少需要 {Models.SizeFormatter.Format(estimatedSpace)} 临时与解压空间。\n{estimateDetail}", disks.Select(disk => new ChoiceItem { Name = disk.IsDefault ? $"{disk.DisplayName}（默认）" : disk.DisplayName, Description = $"{disk.RootPath}（剩余 {disk.FreeSpaceText}，最低保留 {disk.MinimumFreeSpaceText}）", Value = disk }));
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
            if (firstGroups.Count == 0) throw new InvalidOperationException("原始来源中没有发现 ZIP、RAR、7z 或可识别的分卷组。");
            progress.Hide(); IsEnabled = true;
            var firstGroup = SelectArchiveGroupWithHistory("选择第一次解压文件", "请选择第一次解压使用的 ZIP、RAR、7z 或分卷组：", firstGroups, copiedSource, version.FirstArchiveRelativePath, "第一次解压");
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
                var replayArchive = RenameForAttempt(recordedFallbackPath, GetArchiveExtension(version.SecondArchiveFormat));
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
            var launchSelection = ExecutableDiscoveryService.ResolveRecordedSelection(finalExtractionRoot, version.ExecutableRelativePath, launchDiscovery?.GameRoot)
                ?? SelectLaunchFile(finalExtractionRoot, launchDiscovery);
            if (launchSelection is null) throw new OperationCanceledException("用户取消了启动文件选择。", cancellation.Token);
            var launchFile = launchSelection.LaunchFile;
            var selectedGameRoot = launchSelection.GameRoot;

            IsEnabled = false; progress.Show(); UpdatePreparationTask(task, progress, 85, "正在整理最终游戏目录…", selectedGameRoot, "等待确认游戏目录");
            await SourceCopyService.CopyDirectoryAsync(selectedGameRoot, stagedFinal, cancellation.Token);
            var executableRelativePath = Path.GetRelativePath(selectedGameRoot, launchFile);
            version.ExecutableRelativePath = executableRelativePath;

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
            var extractedIconPath = IconExtractionService.ExtractToCache(finalExecutable, _game.Id);
            if (!string.IsNullOrWhiteSpace(extractedIconPath))
            {
                version.IconRelativePath = extractedIconPath;
                _game.IconRelativePath = extractedIconPath;
            }
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
                RestoreAfterProgressDialog(progress);
                MessageBox.Show(this, "游戏准备完成。", "准备游玩", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception cleanupError)
            {
                task.Message = "游戏准备完成，但临时目录自动清理失败，可在任务中心手动清理。";
                task.ErrorMessage = cleanupError.ToString(); _save(task.Message); AppLogger.Error("成功任务临时目录清理失败", cleanupError);
                RestoreAfterProgressDialog(progress);
                MessageBox.Show(this, $"游戏准备已经完成，但临时目录清理失败：{cleanupError.Message}\n\n可在任务中心稍后手动清理。", "准备完成，清理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException ex)
        {
            RollbackFinalCommit(finalTarget, stagedFinal, finalCommitted);
            task.Status = "已取消"; task.Message = "用户取消了准备任务，临时目录已保留。"; task.ErrorMessage = ex.Message; task.CompletedAt = DateTime.Now;
            _game.Status = _versionSwitchInProgress ? "版本切换失败" : "未准备"; _versionSwitchInProgress = false; _save(task.Message);
            RestoreAfterProgressDialog(progress);
            MessageBox.Show(this, $"准备任务已取消。\n\n临时目录已保留：\n{workRoot}", "任务已取消", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            RollbackFinalCommit(finalTarget, stagedFinal, finalCommitted);
            task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now;
            _game.Status = _versionSwitchInProgress ? "版本切换失败" : "操作失败"; _versionSwitchInProgress = false; _save("准备任务失败，临时目录已保留");
            AppLogger.Error($"准备游戏失败：{_game.DisplayName}", ex);
            RestoreAfterProgressDialog(progress);
            MessageBox.Show(this, $"游戏准备失败：{ex.Message}\n\n临时目录已保留：\n{workRoot}", "准备失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            progress.CloseSafely();
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(RestoreWindowActivation));
        }
    }

    private void RestoreAfterProgressDialog(PreparationProgressWindow progress)
    {
        if (progress.IsVisible) progress.Hide();
        RestoreWindowActivation();
    }

    private void RestoreWindowActivation()
    {
        if (!IsVisible) return;
        IsEnabled = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private async void Archive_Click(object sender, RoutedEventArgs e) => await ArchiveCurrentAsync(false);
    private async void SpecialArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_game.Status == "运行中") { ShowError("主游戏程序运行期间禁止特殊归档。"); return; }
        var versionChoice = SelectChoice("选择特殊归档版本", "请选择混乱目录对应的游戏版本：", _game.Versions.Select(item => new ChoiceItem { Name = item.VersionName, Description = item.SourcePath, Value = item }));
        if (versionChoice?.Value is not GameVersionItem version) return;
        var folderDialog = new OpenFolderDialog { Title = "选择需要特殊归档的混乱游戏目录", Multiselect = false };
        if (folderDialog.ShowDialog(this) != true) return;
        var mixedRoot = folderDialog.FolderName;
        var disks = _state.GameDisks.Where(item => item.Enabled && Directory.Exists(item.RootPath)).OrderByDescending(item => item.IsDefault).ThenBy(item => item.DisplayName).ToList();
        var diskChoice = SelectChoice("选择特殊归档操作盘", "请选择用于 GameSaveTemp 和本地存档的游戏盘：", disks.Select(item => new ChoiceItem { Name = item.DisplayName, Description = item.RootPath, Value = item }));
        if (diskChoice?.Value is not GameDiskItem disk) return;
        if (_game.HasLocalSave && CrossDiskSaveCopyService.RequiresCopy(_state, _game, disk))
        {
            if (CrossDiskSaveCopyService.TargetCurrentExists(_game, disk)
                && MessageBox.Show("目标游戏盘已有该游戏 current，特殊归档前将保留来源并覆盖目标 current。是否继续？", "跨盘存档覆盖确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { await CrossDiskSaveCopyService.CopyAsync(_state, _game, disk); }
            catch (Exception ex) { ShowError($"复制主存档失败：{ex.Message}"); return; }
        }
        else if (!_game.HasLocalSave) _game.CurrentSaveGameDiskId = disk.Id;

        var workRoot = Path.Combine(disk.RootPath, "GameSaveTemp", _game.Id.ToString(), version.Id.ToString(), Guid.NewGuid().ToString("N"));
        var task = new OperationTaskItem { Name = $"特殊归档：{_game.DisplayName}", TaskType = "特殊归档", GameId = _game.Id, GameVersionId = version.Id, Status = "运行中", Message = "正在初始化特殊归档", WorkingDirectory = workRoot, CurrentPath = mixedRoot };
        _state.OperationTasks.Add(task); _save("特殊归档任务已创建");
        using var cancellation = new CancellationTokenSource();
        var progress = new PreparationProgressWindow("正在执行特殊归档") { Owner = this };
        progress.EnableCancellation(cancellation.Cancel);
        IsEnabled = false; progress.Show();
        try
        {
            Directory.CreateDirectory(workRoot);
            string? cleanRoot = null;
            Exception? baselineError = null;
            try { cleanRoot = await BuildSpecialCleanDirectoryAsync(version, workRoot, task, progress, cancellation.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { baselineError = ex; AppLogger.Error("特殊归档无法建立完整干净基线", ex); }

            List<SpecialArchiveDifferenceItem> differences;
            var completeBaseline = cleanRoot is not null && Directory.Exists(cleanRoot);
            if (completeBaseline)
            {
                task.Progress = 65; task.Message = "正在比较干净基线与混乱目录"; progress.UpdateStatus(task.Message, 65);
                differences = await SpecialArchiveComparisonService.CompareAsync(cleanRoot!, mixedRoot, cancellation.Token);
                _game.SpecialArchiveBaselineStatus = "完整基线";
            }
            else
            {
                progress.Hide(); IsEnabled = true;
                if (MessageBox.Show($"无法从原始压缩文件建立完整基线：\n{baselineError?.Message}\n\n是否进入“无完整基线”人工选择模式？软件不会自动判断，也不会在备份完成前删除目录。", "无完整基线", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) throw new OperationCanceledException("用户取消了无完整基线人工选择模式。", cancellation.Token);
                IsEnabled = false; progress.Show();
                differences = await SpecialArchiveComparisonService.BuildManualSelectionAsync(mixedRoot, cancellation.Token);
                _game.SpecialArchiveBaselineStatus = "无完整基线";
            }

            progress.Hide(); IsEnabled = true;
            var selectionWindow = new SpecialArchiveSelectionWindow(differences, completeBaseline) { Owner = this };
            if (selectionWindow.ShowDialog() != true) throw new OperationCanceledException("用户取消了特殊归档文件选择。", cancellation.Token);
            IsEnabled = false; progress.Show();
            var candidates = SpecialArchiveComparisonService.CreateSaveCandidates(_game, version, mixedRoot, selectionWindow.SelectedFiles);
            _state.SaveCandidates.RemoveAll(item => item.GameId == _game.Id && item.SourceKind == "游戏目录");
            _state.SaveCandidates.AddRange(candidates);
            task.Progress = 78; task.Message = $"正在创建并校验本地存档快照，共 {candidates.Count} 个文件"; progress.UpdateStatus(task.Message, 78);
            var snapshotResult = await SaveSnapshotService.ApplyAndCreateAsync(_state, _game, candidates.Select(item => item.Id).ToList(), cancellation.Token);
            _save("特殊归档本地存档快照已创建并校验");

            var manifest = await CurrentSaveManifestService.LoadAsync(_state, _game, cancellation.Token) ?? throw new InvalidDataException("特殊归档 current 清单不存在。");
            OrdinaryArchiveService.MarkArchived(_game, manifest);
            _game.ArchiveMessage = $"特殊归档完成；基线状态：{_game.SpecialArchiveBaselineStatus}。本地存档与清单已校验，未要求外部 ZIP 备份。";
            _save("特殊归档已完成，本次未校验外部 ZIP 备份");

            progress.Hide(); IsEnabled = true;
            if (MessageBox.Show($"特殊归档已完成。是否将所选混乱游戏目录移入 Windows 回收站？\n\n{mixedRoot}", "特殊归档目录清理确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try
                {
                    RecycleBinService.MoveDirectory(mixedRoot);
                    _state.DeletionHistory.Add(new DeletionHistoryItem { GameId = _game.Id, GameVersionId = version.Id, ObjectType = "特殊归档混乱目录", ObjectPath = mixedRoot, DeleteMethod = "Windows 回收站", Status = "成功" });
                    if (PathsEqual(_game.PlayableRootPath, mixedRoot)) OrdinaryArchiveService.MarkCleanupSucceeded(_game);
                }
                catch (Exception ex)
                {
                    _state.DeletionHistory.Add(new DeletionHistoryItem { GameId = _game.Id, GameVersionId = version.Id, ObjectType = "特殊归档混乱目录", ObjectPath = mixedRoot, DeleteMethod = "Windows 回收站", Status = "失败", Message = ex.Message });
                    OrdinaryArchiveService.MarkCleanupFailed(_game, ex.Message);
                    MessageBox.Show($"特殊归档已经完成，但混乱目录未能进入回收站：{ex.Message}", "目录清理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            IsEnabled = false; progress.Show();
            task.Status = "完成"; task.Progress = 100; task.Message = $"特殊归档完成，{_game.SpecialArchiveBaselineStatus}"; task.CompletedAt = DateTime.Now;
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true);
            task.WorkingDirectory = string.Empty; _save(task.Message); RefreshBindings();
            MessageBox.Show(task.Message, "特殊归档完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException ex)
        {
            task.Status = "已取消"; task.Message = "特殊归档已取消，临时目录已保留。"; task.ErrorMessage = ex.Message; task.CompletedAt = DateTime.Now; _save(task.Message);
        }
        catch (Exception ex)
        {
            task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now; _save("特殊归档失败，临时目录已保留"); AppLogger.Error($"特殊归档失败：{_game.DisplayName}", ex); ShowError(ex.Message);
        }
        finally { IsEnabled = true; progress.CloseSafely(); RefreshBindings(); }
    }
    private async void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        var progress = new PreparationProgressWindow("正在创建并校验单游戏无密码 ZIP 备份") { Owner = this };
        using var cancellation = new CancellationTokenSource();
        progress.EnableCancellation(cancellation.Cancel);
        IsEnabled = false; progress.Show();
        try
        {
            var result = await ExternalBackupService.CreateManualGameBackupAsync(_state, _game, cancellation.Token);
            _save(result.Message);
            MessageBox.Show(result.Message, "手动备份完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { MessageBox.Show("手动备份已取消，临时 ZIP 已自动清理。", "已取消", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { AppLogger.Error($"手动备份失败：{_game.DisplayName}", ex); ShowError(ex.Message); }
        finally { IsEnabled = true; progress.CloseSafely(); }
    }
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
            var version = _game.Versions.FirstOrDefault(item => item.Id == _game.CurrentVersionId);
            if (version is not null) version.ExecutableRelativePath = Path.GetRelativePath(selection.GameRoot, selection.LaunchFile);
            var extractedIconPath = IconExtractionService.ExtractToCache(selection.LaunchFile, _game.Id);
            if (!string.IsNullOrWhiteSpace(extractedIconPath))
            {
                _game.IconRelativePath = extractedIconPath;
                var currentVersion = _game.Versions.FirstOrDefault(item => item.Id == _game.CurrentVersionId);
                if (currentVersion is not null) currentVersion.IconRelativePath = extractedIconPath;
            }
            ConfirmDirectoryNameAsNote(selection.GameRoot);
            _save("游戏目录、启动文件和图标已重新识别");
            RefreshBindings();
        }
        catch (Exception ex) { AppLogger.Error("重新识别游戏启动文件失败", ex); ShowError(ex.Message); }
        finally { IsEnabled = true; }
    }
    private void RelocateSource_Click(object sender, RoutedEventArgs e) => OpenVersionManagement(_game.CurrentVersionId);
    private void DeleteSource_Click(object sender, RoutedEventArgs e)
    {
        var choice = SelectChoice("选择删除原始文件的版本", "请选择需要将原始压缩包及相关分卷移入回收站的版本：", _game.Versions.Select(item => new ChoiceItem { Name = item.VersionName, Description = item.SourcePath, Value = item }));
        if (choice?.Value is not GameVersionItem version) return;
        if (!DeleteVersionSourcesWithConfirmation(version)) return;
        if (_game.CurrentVersionId == version.Id && !File.Exists(version.SourcePath) && !Directory.Exists(version.SourcePath)) _game.Status = "源文件失效";
        _save($"版本“{version.VersionName}”的原始压缩文件已移入回收站"); RefreshBindings();
    }

    private bool DeleteVersionSourcesWithConfirmation(GameVersionItem version)
    {
        var files = SourceDeletionService.ResolveSourceFiles(version);
        if (files.Count == 0) { ShowError("无法定位主压缩文件及相关分卷，禁止删除。请先重新定位原始来源。"); return false; }
        var totalSize = files.Sum(path => { try { return new FileInfo(path).Length; } catch { return 0L; } });
        var detail = string.Join("\n", files.Take(12));
        if (files.Count > 12) detail += $"\n……另有 {files.Count - 12} 个文件";
        if (MessageBox.Show($"第一次确认：删除后该版本可能无法再次准备。\n\n游戏：{_game.DisplayName}\n版本：{version.VersionName}\n原始路径：{version.SourcePath}\n文件数量：{files.Count}\n总体积：{Models.SizeFormatter.Format(totalSize)}\n\n将进入回收站的文件：\n{detail}\n\n是否继续？", "删除原始文件风险确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        var input = new TextInputWindow("第二次确认", $"请输入完整版本名称“{version.VersionName}”以确认将原始压缩包和分卷移入 Windows 回收站：", string.Empty) { Owner = this };
        if (input.ShowDialog() != true || !input.Value.Equals(version.VersionName, StringComparison.Ordinal)) { ShowError("版本名称不匹配，删除操作已取消。"); return false; }
        try { SourceDeletionService.MoveSourcesToRecycleBin(_state, _game, version, files); return true; }
        catch (Exception ex) { ShowError($"原始文件未能全部进入回收站：{ex.Message}\n\n软件不会降级为永久删除。"); return false; }
    }

    private void DeleteGameRecord_Click(object sender, RoutedEventArgs e)
    {
        var blockers = GameRecordDeletionService.GetBlockers(_state, _game);
        if (blockers.Count > 0)
        {
            MessageBox.Show($"删除游戏主记录前必须先清空所有关联数据：\n\n- {string.Join("\n- ", blockers)}", "禁止删除游戏记录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var input = new TextInputWindow("删除游戏主记录", $"此操作只删除数据库中的游戏主记录。请输入完整游戏名称“{_game.DisplayName}”确认：", string.Empty) { Owner = this };
        if (input.ShowDialog() != true || !input.Value.Equals(_game.DisplayName, StringComparison.Ordinal)) { ShowError("游戏名称不匹配，删除操作已取消。"); return; }
        try { GameRecordDeletionService.Remove(_state, _game); _save("游戏主记录已删除"); DialogResult = true; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_game.Status == "运行中") { ShowError("该游戏的主程序已经在运行。 "); return; }
        if (!_game.HasSystemSave)
        {
            try
            {
                SystemSaveMonitoringService.CancelLatestSession(_state, _game, "已跳过");
                _save("该游戏已标记为不存在系统存档，本次跳过系统目录扫描");
                GameProcessMonitorService.Launch(_game, (game, message) =>
                {
                    _gameStateChanged(game, message);
                    Dispatcher.BeginInvoke(() => { if (IsLoaded) RefreshBindings(); });
                });
                RefreshBindings();
            }
            catch (Exception ex) { _save("游戏启动失败"); MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
            return;
        }
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

    private void HasSystemSave_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || DataContext is not GameItem game) return;
        if (!game.HasSystemSave)
        {
            SystemSaveMonitoringService.CancelLatestSession(_state, game, "已关闭系统存档扫描");
            _state.SaveCandidates.RemoveAll(item => item.GameId == game.Id && item.SourceKind == "系统目录" && item.Decision == SaveCandidateDecisions.Pending);
        }
        _save(game.HasSystemSave ? "已启用该游戏的系统存档扫描" : "已标记该游戏不存在系统存档，后续不再扫描系统目录");
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
        _game.IconRelativePath = targetVersion.IconRelativePath;
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
                MessageBox.Show($"当前不能完成归档：\n\n- {string.Join("\n- ", readiness.Problems)}", "归档条件未满足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (readiness.RequiresNoSaveConfirmation
                && MessageBox.Show("该游戏已经准备完成，但尚未运行，因此没有生成存档清单。\n\n确认直接归档吗？此操作不会保存任何游戏存档。", "未运行游戏归档确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                task.Status = "已取消"; task.Message = "用户取消了未运行游戏的直接归档"; task.CompletedAt = DateTime.Now;
                _game.Status = "可游玩"; _save(task.Message); RefreshBindings();
                return false;
            }

            OrdinaryArchiveService.MarkArchived(_game, readiness.Manifest);
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
                _state.DeletionHistory.Add(new DeletionHistoryItem { GameId = _game.Id, GameVersionId = _game.CurrentVersionId, ObjectType = "普通归档游戏目录", ObjectPath = playable, DeleteMethod = "Windows 回收站", Status = "成功" });
                OrdinaryArchiveService.MarkCleanupSucceeded(_game);
                task.Status = "完成"; task.Progress = 100; task.Message = "归档完成，游戏目录已移入回收站"; task.CompletedAt = DateTime.Now; _save(task.Message); RefreshBindings();
                return true;
            }
            catch (Exception ex)
            {
                _state.DeletionHistory.Add(new DeletionHistoryItem { GameId = _game.Id, GameVersionId = _game.CurrentVersionId, ObjectType = "普通归档游戏目录", ObjectPath = playable, DeleteMethod = "Windows 回收站", Status = "失败", Message = ex.Message });
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

    private async Task<string> BuildSpecialCleanDirectoryAsync(GameVersionItem version, string workRoot, OperationTaskItem task, PreparationProgressWindow progress, CancellationToken token)
    {
        if (!File.Exists(version.SourcePath) && !Directory.Exists(version.SourcePath)) throw new FileNotFoundException("特殊归档版本的原始来源不存在。", version.SourcePath);
        var sourceRoot = Path.Combine(workRoot, "source");
        var step1 = Path.Combine(workRoot, "step1");
        var step2 = Path.Combine(workRoot, "step2");
        var clean = Path.Combine(workRoot, "clean");
        task.Progress = 8; task.Message = "正在复制原始压缩文件到 GameSaveTemp"; task.CurrentPath = version.SourcePath; progress.UpdateStatus(task.Message, 8);
        var copiedSource = await SourceCopyService.CopyToWorkDirectoryAsync(version.SourcePath, sourceRoot, token);
        task.Progress = 18; task.Message = "正在扫描第一次解压候选"; progress.UpdateStatus(task.Message, 18);
        var firstGroups = await Task.Run(() => ArchiveVolumeService.DiscoverGroups(copiedSource, token), token);
        if (firstGroups.Count == 0) throw new InvalidOperationException("原始来源中没有找到第一次解压使用的 ZIP、RAR、7z 或分卷组。");
        progress.Hide(); IsEnabled = true;
        var firstGroup = SelectArchiveGroupWithHistory("特殊归档：第一次解压", "请选择用于构建干净基线的第一次压缩文件：", firstGroups, copiedSource, version.FirstArchiveRelativePath, "第一次解压");
        if (firstGroup is null) throw new OperationCanceledException("用户取消第一次解压选择。", token);
        ConfirmMissingVolumes(firstGroup, "第一次解压");
        IsEnabled = false; progress.Show();
        task.Progress = 28; task.Message = "正在执行第一次解压"; task.CurrentPath = firstGroup.EntryPath; progress.UpdateStatus(task.Message, 28);
        var firstRelative = GetArchiveRelativePath(copiedSource, firstGroup.EntryPath);
        await ExtractWithCredentialAsync(version, firstGroup.EntryPath, step1, "特殊归档第一次解压密码", progress, token, 1, firstRelative, firstGroup.MissingFiles.Count == 0);

        task.Progress = 38; task.Message = "正在扫描第二次解压候选"; task.CurrentPath = step1; progress.UpdateStatus(task.Message, 38);
        var secondGroups = await Task.Run(() => ArchiveVolumeService.DiscoverGroups(step1, token), token);
        if (secondGroups.Count > 0)
        {
            progress.Hide(); IsEnabled = true;
            var secondGroup = SelectArchiveGroupWithHistory("特殊归档：第二次解压", "请选择用于构建干净基线的第二次压缩文件：", secondGroups, step1, version.SecondArchiveRelativePath, "第二次解压");
            if (secondGroup is null) throw new OperationCanceledException("用户取消第二次解压选择。", token);
            ConfirmMissingVolumes(secondGroup, "第二次解压");
            IsEnabled = false; progress.Show();
            var workingArchive = secondGroup.IsMultiVolume ? secondGroup.EntryPath : await ArchiveDiscoveryService.CreateNormalizedWorkingArchiveAsync(secondGroup.EntryPath, secondGroup.Format, token);
            task.Progress = 48; task.Message = "正在执行第二次解压"; task.CurrentPath = workingArchive; progress.UpdateStatus(task.Message, 48);
            await ExtractWithCredentialAsync(version, workingArchive, step2, "特殊归档第二次解压密码", progress, token, 2, Path.GetRelativePath(step1, secondGroup.EntryPath), secondGroup.MissingFiles.Count == 0);
        }
        else
        {
            var largest = await Task.Run(() => ArchiveDiscoveryService.FindLargestUnrecognizedFile(step1, token), token) ?? throw new InvalidOperationException("第一次解压结果中没有第二次压缩文件或可尝试的最大文件。");
            task.Progress = 48; task.Message = "正在将最大混淆文件依次按 ZIP、RAR 尝试第二次解压"; task.CurrentPath = largest; progress.UpdateStatus(task.Message, 48);
            await TryLargestFileAsArchiveAsync(version, largest, step2, progress, token, Path.GetRelativePath(step1, largest));
        }

        task.Progress = 56; task.Message = "正在识别干净游戏目录"; task.CurrentPath = step2; progress.UpdateStatus(task.Message, 56);
        var discovery = await Task.Run(() => ExecutableDiscoveryService.Discover(step2, token), token);
        progress.Hide(); IsEnabled = true;
        var selection = SelectLaunchFile(step2, discovery) ?? throw new OperationCanceledException("用户取消干净游戏目录选择。", token);
        IsEnabled = false; progress.Show();
        task.Progress = 62; task.Message = "正在复制干净基准目录"; task.CurrentPath = selection.GameRoot; progress.UpdateStatus(task.Message, 62);
        await SourceCopyService.CopyDirectoryAsync(selection.GameRoot, clean, token);
        return clean;
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

    private static bool PathsEqual(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

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

    private static string GetArchiveExtension(string format) => format.ToUpperInvariant() switch
    {
        "ZIP" => ".zip",
        "7Z" => ".7z",
        _ => ".rar"
    };

    private string? PromptPassword(string title, string archivePath, string? existingPassword, PreparationProgressWindow progress)
    {
        progress.Hide(); IsEnabled = true;
        try
        {
            var window = new PasswordInputWindow(title, Path.GetFileName(archivePath), existingPassword, CredentialService.GetPasswordHistory(_state)) { Owner = this };
            if (window.ShowDialog() != true) return null;
            CredentialService.AddPasswordHistory(_state, window.Password);
            _save("解压密码历史已更新");
            return window.Password;
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
