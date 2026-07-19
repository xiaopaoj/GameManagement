using GameManagement.Models;
using GameManagement.Services;
using GameManagement.ViewModels;
using System.IO.Compression;
using System.Text.Json;

namespace GameManagement.Tests;

public sealed class ModelTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void 文件大小应使用易读单位(long size, string expected)
    {
        Assert.Equal(expected, SizeFormatter.Format(size));
    }

    [Fact]
    public void 新游戏和版本应生成非空标识()
    {
        var game = new GameItem();
        var version = new GameVersionItem();

        Assert.NotEqual(Guid.Empty, game.Id);
        Assert.NotEqual(Guid.Empty, version.Id);
        Assert.DoesNotContain(game.Id.ToString(), character => character > 127);
    }

    [Theory]
    [InlineData("示例游戏.zip", SourceKinds.ArchiveFile, "示例游戏")]
    [InlineData("示例游戏.rar", SourceKinds.ArchiveFile, "示例游戏")]
    [InlineData("示例游戏目录", SourceKinds.ArchiveDirectory, "示例游戏目录")]
    public void 批量添加时应自动生成游戏名称(string name, string kind, string expected)
    {
        var candidate = new ScanCandidate { Name = name, Kind = kind };

        Assert.Equal(expected, MainViewModel.GetCandidateDisplayName(candidate));
    }

    [Fact]
    public void 直接压缩文件来源应只返回自身()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "game.zip");
            File.WriteAllBytes(archive, []);

            var result = ArchiveDiscoveryService.Discover(archive);

            Assert.Single(result);
            Assert.Equal(Path.GetFullPath(archive), result[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 目录来源应递归发现压缩文件()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "a.zip"), []);
            File.WriteAllBytes(Path.Combine(nested, "b.rar"), []);
            File.WriteAllBytes(Path.Combine(nested, "ignore.7z"), []);

            var result = ArchiveDiscoveryService.Discover(root);

            Assert.Equal(2, result.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 游戏库扫描应只枚举直属目录和ZIPRAR文件()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var gameDirectory = Path.Combine(root, "目录游戏");
        var nested = Path.Combine(gameDirectory, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            var directZip = Path.Combine(root, "直接游戏.zip");
            File.WriteAllBytes(directZip, []);
            File.WriteAllBytes(Path.Combine(nested, "内部游戏.rar"), []);
            File.WriteAllBytes(Path.Combine(root, "伪装压缩包.bin"), [0x50, 0x4B, 0x03, 0x04]);
            var reports = new List<ScanProgressInfo>();
            var scanner = new FileScanner();

            var result = await scanner.ScanAsync(
                [new ScanPathItem { Path = root }],
                [],
                new ImmediateProgress<ScanProgressInfo>(reports.Add),
                CancellationToken.None);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, item => item.FullPath == gameDirectory && item.ArchiveCount == -1);
            Assert.Contains(result, item => item.FullPath == directZip && item.ArchiveCount == 1);
            Assert.DoesNotContain(result, item => item.Name == "内部游戏.rar" || item.Name == "伪装压缩包.bin");
            Assert.Equal(100, reports[^1].Percentage);
            Assert.True(reports[^1].IsCompleted);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 空目录扫描完成后进度应为百分之百()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reports = new List<ScanProgressInfo>();

            var result = await new FileScanner().ScanAsync(
                [new ScanPathItem { Path = root }],
                [],
                new ImmediateProgress<ScanProgressInfo>(reports.Add),
                CancellationToken.None);

            Assert.Empty(result);
            Assert.Equal(100, reports[^1].Percentage);
            Assert.True(reports[^1].IsCompleted);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 单文件来源元数据应包含大小时间和SHA256()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "game.zip");
        try
        {
            await File.WriteAllTextAsync(source, "source-content");

            var metadata = await SourceMetadataService.CaptureAsync(source);

            Assert.Equal(1, metadata.FileCount);
            Assert.Equal(new FileInfo(source).Length, metadata.TotalSize);
            Assert.NotNull(metadata.ModifiedAt);
            Assert.Equal(64, metadata.Fingerprint.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 目录来源内容变化后聚合指纹应变化()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firstFile = Path.Combine(root, "a.zip");
            await File.WriteAllTextAsync(firstFile, "first");
            var first = await SourceMetadataService.CaptureAsync(root);

            await File.WriteAllTextAsync(firstFile, "changed");
            await File.WriteAllTextAsync(Path.Combine(root, "b.rar"), "second");
            var second = await SourceMetadataService.CaptureAsync(root);

            Assert.Equal(1, first.FileCount);
            Assert.Equal(2, second.FileCount);
            Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 软件重启时运行中任务应标记为已中断()
    {
        var running = new OperationTaskItem { Status = "运行中", Message = "处理中" };
        var completed = new OperationTaskItem { Status = "完成", Message = "已完成" };
        var state = new AppState { OperationTasks = [running, completed] };
        var recoveredAt = new DateTime(2026, 7, 19, 12, 0, 0);

        var count = OperationTaskRecoveryService.MarkInterrupted(state, recoveredAt);

        Assert.Equal(1, count);
        Assert.Equal("已中断", running.Status);
        Assert.Equal(recoveredAt, running.CompletedAt);
        Assert.Equal("完成", completed.Status);
    }

    [Fact]
    public void 临时目录安全检查应只允许GameTemp和GameSaveTemp的子目录()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var disk = new GameDiskItem { RootPath = root };

        Assert.True(TemporaryDirectoryService.IsManagedTaskDirectory(Path.Combine(root, "GameTemp", "game-id", "version-id"), [disk]));
        Assert.True(TemporaryDirectoryService.IsManagedTaskDirectory(Path.Combine(root, "GameSaveTemp", "game-id"), [disk]));
        Assert.False(TemporaryDirectoryService.IsManagedTaskDirectory(Path.Combine(root, "GameTemp"), [disk]));
        Assert.False(TemporaryDirectoryService.IsManagedTaskDirectory(Path.Combine(root, "Games", "game-id"), [disk]));
        Assert.False(TemporaryDirectoryService.IsManagedTaskDirectory(root, [disk]));
    }

    [Fact]
    public void 操作任务应能够完整序列化并恢复()
    {
        var task = new OperationTaskItem
        {
            Name = "准备游戏",
            TaskType = "准备游玩",
            Status = "失败",
            Progress = 65,
            CurrentPath = @"D:\Games\GameTemp\game\version",
            WorkingDirectory = @"D:\Games\GameTemp\game\version",
            ErrorMessage = "测试错误"
        };

        var json = JsonSerializer.Serialize(task);
        var restored = JsonSerializer.Deserialize<OperationTaskItem>(json);

        Assert.NotNull(restored);
        Assert.Equal(task.Id, restored.Id);
        Assert.Equal("失败", restored.Status);
        Assert.Equal(65, restored.Progress);
        Assert.Equal(task.WorkingDirectory, restored.WorkingDirectory);
        Assert.Equal("测试错误", restored.ErrorMessage);
    }

    [Fact]
    public async Task 文件扫描应响应预先取消的令牌()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileScanner().ScanAsync([new ScanPathItem { Path = root }], [], null, cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PartRAR分卷应聚合并发现中间缺失卷()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var part1 = Path.Combine(root, "game.part01.rar");
            File.WriteAllBytes(part1, []);
            File.WriteAllBytes(Path.Combine(root, "game.part03.rar"), []);

            var group = ArchiveVolumeService.BuildGroup(part1);

            Assert.True(group.IsMultiVolume);
            Assert.Equal(2, group.Files.Count);
            Assert.Single(group.MissingFiles);
            Assert.EndsWith("game.part02.rar", group.MissingFiles[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(part1, group.EntryPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 旧RAR分卷应包含RAR入口和连续R卷()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var rar = Path.Combine(root, "game.rar");
            File.WriteAllBytes(rar, []);
            File.WriteAllBytes(Path.Combine(root, "game.r00"), []);
            File.WriteAllBytes(Path.Combine(root, "game.r01"), []);

            var group = ArchiveVolumeService.BuildGroup(rar);

            Assert.Equal(3, group.Files.Count);
            Assert.Empty(group.MissingFiles);
            Assert.Equal(rar, group.EntryPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 分卷ZIP应要求从001开始连续存在()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var part2 = Path.Combine(root, "game.zip.002");
            File.WriteAllBytes(part2, []);

            var group = ArchiveVolumeService.BuildGroup(part2);

            Assert.Single(group.MissingFiles);
            Assert.EndsWith("game.zip.001", group.MissingFiles[0], StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 单个分卷入口来源应复制整组分卷并计算整组元数据()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(root);
        try
        {
            var part1 = Path.Combine(root, "game.part01.rar");
            var part2 = Path.Combine(root, "game.part02.rar");
            File.WriteAllBytes(part1, new byte[10]);
            File.WriteAllBytes(part2, new byte[20]);

            var metadata = await SourceMetadataService.CaptureAsync(part1);
            var copiedEntry = await SourceCopyService.CopyToWorkDirectoryAsync(part1, output);

            Assert.Equal(2, metadata.FileCount);
            Assert.Equal(30, metadata.TotalSize);
            Assert.Equal(Path.Combine(output, "game.part01.rar"), copiedEntry);
            Assert.True(File.Exists(Path.Combine(output, "game.part02.rar")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 密码记录应保存解压步骤路径和验证时间()
    {
        var state = new AppState();
        var versionId = Guid.NewGuid();
        var verifiedAt = new DateTime(2026, 7, 19, 13, 0, 0);

        CredentialService.SavePassword(state, versionId, "fingerprint", "password", 2, "second.rar", "nested/second.rar", verifiedAt);

        var item = Assert.Single(state.Credentials);
        Assert.Equal(2, item.StepOrder);
        Assert.Equal("second.rar", item.ArchiveDisplayName);
        Assert.Equal("nested/second.rar", item.ArchiveRelativePath);
        Assert.Equal(verifiedAt, item.VerifiedAt);
        Assert.Equal("password", CredentialService.FindPassword(state, versionId, "fingerprint"));
    }

    [Fact]
    public async Task ZIP无密码记录应能够重新验证()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var archive = Path.Combine(root, "game.zip");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "test");
            ZipFile.CreateFromDirectory(source, archive);

            await ArchiveExtractionService.ValidatePasswordAsync(archive, string.Empty);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 密码应通过DPAPI加密并可由当前用户解密()
    {
        var versionId = Guid.NewGuid();
        var encrypted = CredentialService.Encrypt("测试密码", versionId);

        Assert.NotEqual("测试密码", encrypted);
        Assert.Equal("测试密码", CredentialService.Decrypt(encrypted, versionId));
    }

    [Fact]
    public async Task ZIP文件应能够解压到目标目录()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var output = Path.Combine(root, "output");
        var archive = Path.Combine(root, "game.zip");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "game.exe"), "test");
            ZipFile.CreateFromDirectory(source, archive);

            await ArchiveExtractionService.ExtractAsync(archive, output, string.Empty);

            Assert.True(File.Exists(Path.Combine(output, "game.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EXE识别应排除辅助程序()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Game.exe"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "UnityCrashHandler64.exe"), [1, 2, 3, 4]);

            var result = ExecutableDiscoveryService.Discover(root);

            Assert.NotNull(result);
            Assert.Equal(root, result.GameRoot);
            Assert.Equal(2, result.LaunchFiles.Count);
            Assert.Contains(result.LaunchFiles, path => Path.GetFileName(path) == "Game.exe");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void EXE识别应递归确定第一个有效EXE所属目录()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            var nestedExe = Path.Combine(nested, "NestedGame.exe");
            File.WriteAllBytes(nestedExe, [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(nested, "index.html"), "<html></html>");

            var result = ExecutableDiscoveryService.Discover(root);

            Assert.NotNull(result);
            Assert.Equal(nested, result.GameRoot);
            Assert.Contains(nestedExe, result.LaunchFiles);
            Assert.Contains(result.LaunchFiles, path => Path.GetFileName(path).Equals("index.html", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 启动候选评分应优先目录同名主程序并降低辅助程序()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ScoreGame");
        Directory.CreateDirectory(root);
        try
        {
            var main = Path.Combine(root, "ScoreGame.exe");
            var helper = Path.Combine(root, "UnityCrashHandler64.exe");
            File.WriteAllBytes(main, new byte[32]);
            File.WriteAllBytes(helper, new byte[128]);

            var candidates = ExecutableDiscoveryService.ScoreCandidates(root, [helper, main]);

            Assert.Equal(main, candidates[0].Path);
            Assert.True(candidates[0].Score > candidates[1].Score);
            Assert.True(candidates[1].IsExcluded);
        }
        finally { Directory.Delete(Directory.GetParent(root)!.FullName, true); }
    }

    [Fact]
    public void 游戏运行状态应记录启动退出和持续时间()
    {
        var game = new GameItem { PlayableRootPath = Path.GetTempPath(), Status = "可游玩" };
        var startedAt = new DateTime(2026, 7, 19, 20, 0, 0);

        GameRuntimeStateService.MarkStarted(game, 1234, startedAt);
        GameRuntimeStateService.MarkExited(game, 0, startedAt.AddMinutes(5));

        Assert.Equal("可游玩", game.Status);
        Assert.Equal(startedAt, game.LastPlayedAt);
        Assert.Equal(startedAt.AddMinutes(5), game.LastExitedAt);
        Assert.Equal(300, game.LastRunDurationSeconds);
        Assert.Equal(0, game.LastExitCode);
        Assert.Null(game.RunningProcessId);
    }

    [Fact]
    public void 不存在的EXE不应生成图标缓存()
    {
        var result = IconExtractionService.ExtractToCache(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe"), Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task 文件基线应包含相对路径大小和SHA256()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "save.dat"), "baseline");

            var result = await BaselineService.BuildAsync(Guid.NewGuid(), root);

            Assert.Single(result);
            Assert.Equal("save.dat", result[0].RelativePath);
            Assert.Equal(64, result[0].Sha256.Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 错误扩展名的ZIP应通过文件头识别()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var archive = Path.Combine(root, "large.data");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "file.txt"), "test");
            ZipFile.CreateFromDirectory(source, archive);

            Assert.Equal("ZIP", ArchiveDiscoveryService.DetectFormat(archive));
            Assert.Contains(Path.GetFullPath(archive), ArchiveDiscoveryService.Discover(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 空间估算应为来源大小的四倍()
    {
        Assert.Equal(400, SpaceEstimationService.EstimateRequiredSpace(100));
    }

    [Fact]
    public void MP4前缀和ZIP尾部的混合文件应识别为ZIP()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "content");
        var zip = Path.Combine(root, "source.zip");
        var disguised = Path.Combine(root, "3.mp4");
        Directory.CreateDirectory(content);
        try
        {
            File.WriteAllText(Path.Combine(content, "file.txt"), "test");
            ZipFile.CreateFromDirectory(content, zip);
            using (var output = File.Create(disguised))
            {
                output.Write(new byte[] { 0, 0, 0, 32, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D });
                using var input = File.OpenRead(zip);
                input.CopyTo(output);
            }

            Assert.Equal("ZIP", ArchiveDiscoveryService.DetectFormat(disguised));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 识别格式后应在临时目录修正扩展名()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var original = Path.Combine(root, "game.mp4");
        try
        {
            File.WriteAllBytes(original, [1, 2, 3]);

            var renamed = ArchiveDiscoveryService.EnsureCorrectExtension(original, "ZIP");

            Assert.EndsWith(".zip", renamed, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(renamed));
            Assert.False(File.Exists(original));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void 识别失败时应选择最大的文件()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "small.dat"), new byte[10]);
            var largest = Path.Combine(root, "large.mp4");
            File.WriteAllBytes(largest, new byte[100]);

            Assert.Equal(largest, ArchiveDiscoveryService.FindLargestUnrecognizedFile(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 带MP4前缀的ZIP应生成规范化文件并成功解压()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "content");
        var zip = Path.Combine(root, "source.zip");
        var disguised = Path.Combine(root, "3.mp4");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(content);
        try
        {
            File.WriteAllText(Path.Combine(content, "game.exe"), "test");
            ZipFile.CreateFromDirectory(content, zip);
            await using (var target = File.Create(disguised))
            {
                await target.WriteAsync(new byte[] { 0, 0, 0, 32, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D });
                await using var source = File.OpenRead(zip);
                await source.CopyToAsync(target);
            }

            var normalized = await ArchiveDiscoveryService.CreateNormalizedWorkingArchiveAsync(disguised, "ZIP");
            await ArchiveExtractionService.ExtractAsync(normalized, output, string.Empty);

            Assert.NotEqual(disguised, normalized);
            Assert.True(File.Exists(Path.Combine(output, "game.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 增量比较应标记基线文件大型文件资源包和具体排除规则()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var playable = Path.Combine(root, "game");
        Directory.CreateDirectory(playable);
        var game = new GameItem { DisplayName = "测试游戏", PlayableRootPath = playable };
        var version = new GameVersionItem();
        game.CurrentVersionId = version.Id;
        try
        {
            var savePath = Path.Combine(playable, "save.dat");
            await File.WriteAllTextAsync(savePath, "原始内容");
            var state = new AppState { FileBaselines = await BaselineService.BuildAsync(game.Id, version.Id, playable) };
            state.SaveFileRules.Add(new SaveFileRuleItem { GameId = game.Id, RelativePath = "save.dat" });
            state.SaveFileExclusions.Add(new SaveFileExclusionItem { GameId = game.Id, RelativePath = "ignored.log" });
            await File.WriteAllTextAsync(savePath, "修改后的存档内容");
            await File.WriteAllTextAsync(Path.Combine(playable, "resources.pak"), "资源包");
            await File.WriteAllTextAsync(Path.Combine(playable, "ignored.log"), "日志");
            await using (var large = File.Create(Path.Combine(playable, "large.bin"))) large.SetLength(SaveChangeDetectionService.LargeFileThreshold);

            var result = await SaveChangeDetectionService.DetectAsync(state, game, SaveSnapshotKinds.Normal);

            var confirmedSave = Assert.Single(result, item => item.RelativePath == "save.dat");
            Assert.True(confirmedSave.PreviouslyConfirmed);
            Assert.False(confirmedSave.DefaultExcluded);
            Assert.Contains(result, item => item.RelativePath == "large.bin" && item.DefaultExcluded && item.ExclusionReason.Contains("100 MB"));
            Assert.Contains(result, item => item.RelativePath == "resources.pak" && item.DefaultExcluded && item.ExclusionReason.Contains("资源包"));
            Assert.Contains(result, item => item.RelativePath == "ignored.log" && item.Decision == SaveCandidateDecisions.Excluded);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 确认候选后应创建并校验正常与异常快照且避免重复快照()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var playable = Path.Combine(root, "playable");
        var diskRoot = Path.Combine(root, "disk");
        Directory.CreateDirectory(playable); Directory.CreateDirectory(diskRoot);
        var disk = new GameDiskItem { RootPath = diskRoot };
        var game = new GameItem { DisplayName = "快照测试", PlayableRootPath = playable, CurrentGameDiskId = disk.Id, CurrentVersionId = Guid.NewGuid() };
        var state = new AppState { Games = [game], GameDisks = [disk] };
        try
        {
            var source = Path.Combine(playable, "save.dat");
            await File.WriteAllTextAsync(source, "第一次存档");
            var first = new SaveCandidateItem { GameId = game.Id, GameVersionId = game.CurrentVersionId!.Value, GameName = game.DisplayName, SourcePath = source, RelativePath = "save.dat", ChangeType = "新增", SnapshotKind = SaveSnapshotKinds.Normal };
            state.SaveCandidates.Add(first);

            var normal = await SaveSnapshotService.ApplyAndCreateAsync(state, game, [first.Id]);

            Assert.True(normal.ContentChanged);
            Assert.NotNull(normal.Snapshot);
            Assert.True(normal.Snapshot!.Verified);
            Assert.True(File.Exists(Path.Combine(GameSavePathService.GetCurrentDirectory(state, game), "save.dat")));
            Assert.True(File.Exists(normal.Snapshot.ManifestPath));
            Assert.True(game.HasLocalSave);
            Assert.DoesNotContain(Directory.EnumerateDirectories(GameSavePathService.GetGameSaveRoot(state, game)), path => Path.GetFileName(path).StartsWith(".current-", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));

            var duplicate = new SaveCandidateItem { GameId = game.Id, GameVersionId = game.CurrentVersionId.Value, GameName = game.DisplayName, SourcePath = source, RelativePath = "save.dat", ChangeType = "修改", SnapshotKind = SaveSnapshotKinds.Normal };
            state.SaveCandidates.Add(duplicate);
            var duplicateResult = await SaveSnapshotService.ApplyAndCreateAsync(state, game, [duplicate.Id]);
            Assert.False(duplicateResult.ContentChanged);
            Assert.Null(duplicateResult.Snapshot);

            await File.WriteAllTextAsync(source, "异常退出后的存档");
            game.LastExitCode = 1;
            var abnormalCandidate = new SaveCandidateItem { GameId = game.Id, GameVersionId = game.CurrentVersionId.Value, GameName = game.DisplayName, SourcePath = source, RelativePath = "save.dat", ChangeType = "修改", SnapshotKind = SaveSnapshotKinds.Abnormal };
            state.SaveCandidates.Add(abnormalCandidate);
            var abnormal = await SaveSnapshotService.ApplyAndCreateAsync(state, game, [abnormalCandidate.Id]);

            Assert.True(abnormal.ContentChanged);
            Assert.Equal(SaveSnapshotKinds.Abnormal, abnormal.Snapshot!.SnapshotKind);
            Assert.Contains("abnormal-snapshots", abnormal.Snapshot.DirectoryPath);
            Assert.Equal(2, state.SaveSnapshots.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 同类快照超过三个时只标记建议清理不自动删除()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var playable = Path.Combine(root, "playable");
        var diskRoot = Path.Combine(root, "disk");
        Directory.CreateDirectory(playable); Directory.CreateDirectory(diskRoot);
        var disk = new GameDiskItem { RootPath = diskRoot };
        var game = new GameItem { DisplayName = "保留测试", PlayableRootPath = playable, CurrentGameDiskId = disk.Id, CurrentVersionId = Guid.NewGuid() };
        var state = new AppState { Games = [game], GameDisks = [disk] };
        try
        {
            var source = Path.Combine(playable, "save.dat");
            for (var index = 0; index < 4; index++)
            {
                await File.WriteAllTextAsync(source, $"存档版本 {index}");
                var candidate = new SaveCandidateItem { GameId = game.Id, GameVersionId = game.CurrentVersionId!.Value, GameName = game.DisplayName, SourcePath = source, RelativePath = "save.dat", ChangeType = index == 0 ? "新增" : "修改", SnapshotKind = SaveSnapshotKinds.Normal };
                state.SaveCandidates.Add(candidate);
                var result = await SaveSnapshotService.ApplyAndCreateAsync(state, game, [candidate.Id]);
                Assert.True(result.ContentChanged);
            }

            Assert.Equal(4, state.SaveSnapshots.Count);
            Assert.Single(state.SaveSnapshots, snapshot => snapshot.CleanupSuggested);
            Assert.All(state.SaveSnapshots, snapshot => Assert.True(Directory.Exists(snapshot.DirectoryPath)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 系统存档监控应检测新增修改删除并清理扫描缓存()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var modifiedPath = Path.Combine(root, "modified.sav");
        var deletedPath = Path.Combine(root, "deleted.sav");
        await File.WriteAllTextAsync(modifiedPath, "原内容");
        await File.WriteAllTextAsync(deletedPath, "待删除");
        var game = new GameItem { DisplayName = "系统存档监控", SystemSaveInitialScanCompleted = true, CurrentVersionId = Guid.NewGuid() };
        var state = new AppState { Games = [game] };
        state.SystemSaveDirectories.Add(new SystemSaveDirectoryRuleItem { GameId = game.Id, Path = root, DisplayName = "测试目录" });
        try
        {
            var session = await SystemSaveMonitoringService.BeginSessionAsync(state, game);
            Assert.NotNull(session);
            Assert.True(File.Exists(session!.SnapshotFilePath));

            await File.WriteAllTextAsync(modifiedPath, "修改后的内容更长");
            File.Delete(deletedPath);
            await File.WriteAllTextAsync(Path.Combine(root, "created.sav"), "新增内容");

            var candidates = await SystemSaveMonitoringService.CompleteSessionAsync(state, game, SaveSnapshotKinds.Normal);

            Assert.Contains(candidates, item => item.RelativePath == "modified.sav" && item.ChangeType == "修改");
            Assert.Contains(candidates, item => item.RelativePath == "deleted.sav" && item.ChangeType == "删除");
            Assert.Contains(candidates, item => item.RelativePath == "created.sav" && item.ChangeType == "新增");
            Assert.All(candidates, item => Assert.Equal(root, item.SourceRootPath, ignoreCase: true));
            Assert.Equal("已完成", session.Status);
            Assert.False(File.Exists(session.SnapshotFilePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 首次系统扫描应以文件父目录为监控根目录且保留共享确认要求()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "厂商", "游戏");
        Directory.CreateDirectory(child);
        var path = Path.Combine(child, "save.dat");
        await File.WriteAllTextAsync(path, "存档");
        var game = new GameItem { DisplayName = "首次扫描", CurrentVersionId = Guid.NewGuid() };
        var other = new GameItem { DisplayName = "共享游戏" };
        var state = new AppState { Games = [game, other] };
        state.SystemSaveDirectories.Add(new SystemSaveDirectoryRuleItem { GameId = game.Id, Path = child, DisplayName = "共享目录" });
        state.SystemSaveDirectories.Add(new SystemSaveDirectoryRuleItem { GameId = other.Id, Path = child, DisplayName = "共享目录" });
        try
        {
            var candidates = await SystemSaveMonitoringService.CompareAsync(
                state,
                game,
                SaveSnapshotKinds.Normal,
                true,
                [],
                [new SystemFileSnapshotItem { FullPath = path, RootPath = root, FileSize = new FileInfo(path).Length, ModifiedAt = File.GetLastWriteTime(path) }],
                CancellationToken.None);

            var candidate = Assert.Single(candidates);
            Assert.Equal(child, candidate.SourceRootPath, ignoreCase: true);
            Assert.Equal("save.dat", candidate.RelativePath);
            Assert.True(candidate.SharedDirectory);
            Assert.True(candidate.DefaultExcluded);
            Assert.Contains("共享目录", candidate.ExclusionReason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 确认系统存档候选后应写入隔离目录并记录原始路径()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var systemRoot = Path.Combine(root, "system-save");
        var playable = Path.Combine(root, "playable");
        var diskRoot = Path.Combine(root, "disk");
        Directory.CreateDirectory(systemRoot); Directory.CreateDirectory(playable); Directory.CreateDirectory(diskRoot);
        var disk = new GameDiskItem { RootPath = diskRoot };
        var game = new GameItem
        {
            DisplayName = "系统存档快照",
            PlayableRootPath = playable,
            CurrentGameDiskId = disk.Id,
            CurrentVersionId = Guid.NewGuid(),
            SystemSaveInitialScanCompleted = true
        };
        var state = new AppState { Games = [game], GameDisks = [disk] };
        state.SystemSaveDirectories.Add(new SystemSaveDirectoryRuleItem { GameId = game.Id, Path = systemRoot, DisplayName = "系统存档" });
        try
        {
            await SystemSaveMonitoringService.BeginSessionAsync(state, game);
            var source = Path.Combine(systemRoot, "slot1.sav");
            await File.WriteAllTextAsync(source, "第一槽存档");
            var candidates = await SystemSaveMonitoringService.CompleteSessionAsync(state, game, SaveSnapshotKinds.Normal);
            SystemSaveMonitoringService.ReplaceDetectedCandidates(state, game, candidates);
            var candidate = Assert.Single(candidates);

            var result = await SaveSnapshotService.ApplyAndCreateAsync(state, game, [candidate.Id]);

            Assert.True(result.ContentChanged);
            var currentFile = Path.Combine(GameSavePathService.GetCurrentDirectory(state, game), candidate.StorageRelativePath);
            Assert.True(File.Exists(currentFile));
            Assert.Contains(state.SaveFileRules, item => item.GameId == game.Id && item.SourceKind == "系统目录" && item.SourceRootPath.Equals(systemRoot, StringComparison.OrdinalIgnoreCase));
            var manifest = JsonSerializer.Deserialize<SaveSnapshotManifest>(await File.ReadAllTextAsync(result.Snapshot!.ManifestPath));
            var manifestFile = Assert.Single(manifest!.Files);
            Assert.Equal(source, manifestFile.OriginalPath, ignoreCase: true);
            Assert.Equal("系统目录", manifestFile.SourceKind);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
