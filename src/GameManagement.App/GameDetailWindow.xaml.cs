using System.Diagnostics;
using System.IO;
using System.Windows;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class GameDetailWindow : Window
{
    private readonly GameItem _game;
    private readonly AppState _state;
    private readonly Action<string> _save;

    public GameDetailWindow(GameItem game, AppState state, Action<string> save)
    {
        InitializeComponent();
        _game = game;
        _state = state;
        _save = save;
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
        if (version is null) { ShowError("当前游戏没有可准备的版本。"); return; }
        if (!File.Exists(version.SourcePath) && !Directory.Exists(version.SourcePath)) { ShowError("原始文件或目录不存在，请先执行重新定位。"); return; }
        if (!string.IsNullOrWhiteSpace(_game.PlayableRootPath) && Directory.Exists(_game.PlayableRootPath)) { ShowError("当前游戏已经存在可游玩目录，不允许重复准备。"); return; }

        var disks = _state.GameDisks.Where(disk => disk.Enabled && Directory.Exists(disk.RootPath)).OrderByDescending(disk => disk.IsDefault).ThenBy(disk => disk.DisplayName).ToList();
        if (disks.Count == 0) { ShowError("请先在设置中添加并启用至少一个游戏盘。"); return; }
        var sourceSize = version.SourceSize > 0 ? version.SourceSize : await Task.Run(() => SpaceEstimationService.GetSourceSize(version.SourcePath));
        var estimatedSpace = SpaceEstimationService.EstimateRequiredSpace(sourceSize);
        var diskChoice = SelectChoice("选择游戏盘", $"请选择本次准备游戏使用的游戏盘。预计至少需要 {Models.SizeFormatter.Format(estimatedSpace)} 临时与解压空间：", disks.Select(disk => new ChoiceItem { Name = disk.IsDefault ? $"{disk.DisplayName}（默认）" : disk.DisplayName, Description = $"{disk.RootPath}（剩余 {disk.FreeSpaceText}，最低保留 {disk.MinimumFreeSpaceText}）", Value = disk }));
        if (diskChoice?.Value is not GameDiskItem disk) return;
        var availableSpace = SpaceEstimationService.GetAvailableSpace(disk.RootPath);
        if (availableSpace < estimatedSpace + disk.MinimumFreeSpaceBytes)
        {
            ShowError($"目标游戏盘空间不足。\n预计需要：{Models.SizeFormatter.Format(estimatedSpace)}\n最低保留：{Models.SizeFormatter.Format(disk.MinimumFreeSpaceBytes)}\n当前可用：{Models.SizeFormatter.Format(availableSpace)}");
            return;
        }

        var workRoot = Path.Combine(disk.RootPath, "GameTemp", _game.Id.ToString(), version.Id.ToString());
        var sourceRoot = Path.Combine(workRoot, "source");
        var step1 = Path.Combine(workRoot, "step1");
        var step2 = Path.Combine(workRoot, "step2");
        var stagedFinal = Path.Combine(workRoot, "final");
        var finalTarget = Path.Combine(disk.RootPath, "Games", _game.Id.ToString());
        if (Directory.Exists(finalTarget)) { ShowError($"目标游戏目录已经存在：\n{finalTarget}"); return; }
        if (Directory.Exists(workRoot))
        {
            if (MessageBox.Show($"检测到上次失败、中断或取消后保留的临时目录：\n{workRoot}\n\n重新准备需要永久删除该临时目录，是否继续？", "临时目录恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { Directory.Delete(workRoot, true); }
            catch (Exception ex) { ShowError($"无法清理旧临时目录：{ex.Message}"); return; }
        }

        using var cancellation = new CancellationTokenSource();
        var task = new OperationTaskItem { Name = $"准备游戏：{_game.DisplayName}", TaskType = "准备游玩", GameId = _game.Id, GameVersionId = version.Id, Status = "运行中", Message = "正在初始化准备任务", WorkingDirectory = workRoot };
        _state.OperationTasks.Add(task); _save("准备游玩任务已创建");
        var progress = new PreparationProgressWindow { Owner = this };
        progress.EnableCancellation(cancellation.Cancel);
        progress.Show(); IsEnabled = false;
        try
        {
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
            var firstArchives = await Task.Run(() => ArchiveDiscoveryService.Discover(copiedSource, cancellation.Token), cancellation.Token);
            if (firstArchives.Count == 0) throw new InvalidOperationException("原始来源中没有发现 ZIP 或 RAR 文件。");
            progress.Hide(); IsEnabled = true;
            var firstArchive = SelectArchive("选择第一次解压文件", "请选择第一次解压使用的 ZIP、RAR 或分卷入口文件：", firstArchives);
            if (firstArchive is null) throw new OperationCanceledException("用户取消了第一次解压文件选择。", cancellation.Token);
            IsEnabled = false; progress.Show();
            UpdatePreparationTask(task, progress, 35, $"正在执行第一次解压：{Path.GetFileName(firstArchive)}", firstArchive, "第一次解压中");
            await ExtractWithCredentialAsync(version, firstArchive, step1, "第一次解压密码", progress, cancellation.Token);
            version.FirstArchiveRelativePath = File.Exists(copiedSource) ? Path.GetFileName(firstArchive) : Path.GetRelativePath(copiedSource, firstArchive);

            UpdatePreparationTask(task, progress, 50, "正在扫描第二次解压候选…", step1, "第二次解压中");
            var secondArchives = await Task.Run(() => ArchiveDiscoveryService.Discover(step1, cancellation.Token), cancellation.Token);
            var finalExtractionRoot = step2;
            if (secondArchives.Count > 0)
            {
                progress.Hide(); IsEnabled = true;
                var secondArchive = SelectArchive("选择第二次解压文件", "请选择第二次解压使用的 ZIP 或 RAR 文件：", secondArchives);
                if (secondArchive is null) throw new OperationCanceledException("用户取消了第二次解压文件选择。", cancellation.Token);
                var detectedFormat = ArchiveDiscoveryService.DetectFormat(secondArchive) ?? throw new InvalidOperationException("无法判断第二次压缩文件格式。");
                IsEnabled = false; progress.Show();
                UpdatePreparationTask(task, progress, 60, $"正在定位真实 {detectedFormat} 数据并生成规范化临时文件…", secondArchive, "第二次解压中");
                secondArchive = await ArchiveDiscoveryService.CreateNormalizedWorkingArchiveAsync(secondArchive, detectedFormat, cancellation.Token);
                UpdatePreparationTask(task, progress, 65, $"正在执行第二次解压：{Path.GetFileName(secondArchive)}", secondArchive, "第二次解压中");
                await ExtractWithCredentialAsync(version, secondArchive, step2, "第二次解压密码", progress, cancellation.Token);
                version.SecondArchiveRelativePath = Path.GetRelativePath(step1, secondArchive);
            }
            else
            {
                var largestFile = await Task.Run(() => ArchiveDiscoveryService.FindLargestUnrecognizedFile(step1, cancellation.Token), cancellation.Token);
                if (largestFile is null) throw new InvalidOperationException("第一次解压结果中没有找到第二次压缩文件，也没有可以尝试改名的文件。");
                UpdatePreparationTask(task, progress, 60, $"未识别到压缩格式，准备尝试最大文件：{Path.GetFileName(largestFile)}", largestFile, "第二次解压中");
                var attemptedArchive = await TryLargestFileAsArchiveAsync(version, largestFile, step2, progress, cancellation.Token);
                version.SecondArchiveRelativePath = Path.GetRelativePath(step1, attemptedArchive);
            }

            UpdatePreparationTask(task, progress, 75, "正在递归查找第一个有效 EXE 并确定游戏目录…", finalExtractionRoot, "等待确认游戏目录");
            var launchDiscovery = await Task.Run(() => ExecutableDiscoveryService.Discover(finalExtractionRoot, cancellation.Token), cancellation.Token);
            if (launchDiscovery is null) throw new InvalidOperationException("解压结果中没有找到可用的 EXE 文件。");
            progress.Hide(); IsEnabled = true;
            var launchFile = SelectLaunchFile(launchDiscovery.GameRoot, launchDiscovery.LaunchFiles);
            if (launchFile is null) throw new OperationCanceledException("用户取消了启动文件选择。", cancellation.Token);
            var selectedGameRoot = launchDiscovery.GameRoot;

            IsEnabled = false; progress.Show(); UpdatePreparationTask(task, progress, 85, "正在整理最终游戏目录…", selectedGameRoot, "等待确认游戏目录");
            await SourceCopyService.CopyDirectoryAsync(selectedGameRoot, stagedFinal, cancellation.Token);
            var executableRelativePath = Path.GetRelativePath(selectedGameRoot, launchFile);

            UpdatePreparationTask(task, progress, 92, "正在计算游戏文件 SHA-256 基线，这可能需要较长时间…", stagedFinal, "等待确认游戏目录");
            var baselines = await BaselineService.BuildAsync(version.Id, stagedFinal, cancellation.Token);
            _state.FileBaselines.RemoveAll(item => item.GameVersionId == version.Id);
            _state.FileBaselines.AddRange(baselines);

            cancellation.Token.ThrowIfCancellationRequested();
            Directory.Move(stagedFinal, finalTarget);
            var finalExecutable = Path.Combine(finalTarget, executableRelativePath);

            version.GameDiskId = disk.Id;
            _game.CurrentGameDiskId = disk.Id;
            _game.PlayableRootPath = finalTarget;
            _game.ExecutableRelativePath = Path.GetRelativePath(finalTarget, finalExecutable);
            _game.Status = "可游玩";
            task.Status = "完成"; task.Progress = 100; task.Message = $"游戏准备完成，共建立 {baselines.Count} 个文件基线"; task.CurrentPath = finalTarget; task.CompletedAt = DateTime.Now;
            _save(task.Message);

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
            task.Status = "已取消"; task.Message = "用户取消了准备任务，临时目录已保留。"; task.ErrorMessage = ex.Message; task.CompletedAt = DateTime.Now;
            _game.Status = "未准备"; _save(task.Message);
            MessageBox.Show($"准备任务已取消。\n\n临时目录已保留：\n{workRoot}", "任务已取消", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            task.Status = "失败"; task.Message = ex.Message; task.ErrorMessage = ex.ToString(); task.CompletedAt = DateTime.Now;
            _game.Status = "操作失败"; _save("准备任务失败，临时目录已保留");
            AppLogger.Error($"准备游戏失败：{_game.DisplayName}", ex);
            MessageBox.Show($"游戏准备失败：{ex.Message}\n\n临时目录已保留：\n{workRoot}", "准备失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            progress.CloseSafely();
        }
    }
    private void Archive_Click(object sender, RoutedEventArgs e) => ShowFeature("归档游戏", "比较文件基线，保存并校验本地存档及外部备份，然后单独处理可游玩目录。");
    private void SpecialArchive_Click(object sender, RoutedEventArgs e) => ShowFeature("特殊归档", "选择已有的混乱游戏目录，与从原始压缩文件构建的干净基准目录比较后提取存档。");
    private void ManualBackup_Click(object sender, RoutedEventArgs e) => ShowFeature("手动备份", "将当前游戏的本地存档创建为无密码 ZIP，完成文件清单和 Hash 校验。");
    private void SaveDirectories_Click(object sender, RoutedEventArgs e) => ShowFeature("存档目录设置", "添加、修改、启用、禁用或删除该游戏关联的多个 Windows 系统存档目录。");
    private void Snapshots_Click(object sender, RoutedEventArgs e) => ShowFeature("存档与快照", "查看当前存档、最近三个正常快照、最近三个异常快照和文件清单。");
    private void Versions_Click(object sender, RoutedEventArgs e) => OpenVersionManagement(null);
    private void RecognizeGame_Click(object sender, RoutedEventArgs e) => ShowFeature("识别游戏目录", "递归查找第一个有效 EXE，以其所属文件夹作为游戏目录，然后展示该目录直属的全部 EXE 和 index.html 供用户选择启动文件。");
    private void RelocateSource_Click(object sender, RoutedEventArgs e) => OpenVersionManagement(_game.CurrentVersionId);
    private void DeleteSource_Click(object sender, RoutedEventArgs e) => ShowFeature("删除原始文件", "该操作最终需要两次人工确认，并且只能将原始压缩文件和相关分卷移入 Windows 回收站。");

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        try { ShellService.LaunchGame(_game); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Error); }
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

    private void OpenVersionManagement(Guid? selectedVersionId)
    {
        new VersionManagementWindow(_game, _state, _save, selectedVersionId) { Owner = this }.ShowDialog();
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

    private string? SelectArchive(string title, string prompt, IReadOnlyList<string> archives)
    {
        var choices = archives.Select(path => new ChoiceItem
        {
            Name = Path.GetFileName(path),
            Description = $"{ArchiveDiscoveryService.DetectFormat(path) ?? "未知"}｜{Models.SizeFormatter.Format(new FileInfo(path).Length)}｜{path}",
            Value = path
        });
        return SelectChoice(title, prompt, choices)?.Value as string;
    }

    private string? SelectLaunchFile(string rootDirectory, IReadOnlyList<string> launchFiles)
    {
        var choices = launchFiles.Select(path => new ChoiceItem
        {
            Name = Path.GetFileName(path),
            Description = $"{(Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? "EXE" : "网页入口")}｜{Models.SizeFormatter.Format(new FileInfo(path).Length)}｜{Path.GetRelativePath(rootDirectory, path)}",
            Value = path
        });
        return SelectChoice("选择游戏启动文件", $"已确定游戏目录：\n{rootDirectory}\n\n请选择该目录直属的 EXE 或 index.html 作为启动文件：", choices)?.Value as string;
    }

    private async Task ExtractWithCredentialAsync(GameVersionItem version, string archivePath, string outputDirectory, string title, PreparationProgressWindow progress, CancellationToken token)
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
        catch
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
            Directory.CreateDirectory(outputDirectory);
            var replacement = PromptPassword($"{title}验证失败", archivePath, password, progress);
            if (replacement is null) throw;
            password = replacement;
            await ArchiveExtractionService.ExtractAsync(archivePath, outputDirectory, password, token);
        }
        CredentialService.SavePassword(_state, version.Id, fingerprint, password);
    }

    private async Task<string> TryLargestFileAsArchiveAsync(GameVersionItem version, string largestFile, string outputDirectory, PreparationProgressWindow progress, CancellationToken token)
    {
        var currentPath = largestFile;
        Exception? zipError = null;
        try
        {
            currentPath = RenameForAttempt(currentPath, ".zip");
            progress.UpdateStatus($"正在将最大文件作为 ZIP 尝试第二次解压：{Path.GetFileName(currentPath)}");
            await ExtractWithCredentialAsync(version, currentPath, outputDirectory, "第二次解压密码（ZIP 尝试）", progress, token);
            return currentPath;
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
            await ExtractWithCredentialAsync(version, currentPath, outputDirectory, "第二次解压密码（RAR 尝试）", progress, token);
            return currentPath;
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
