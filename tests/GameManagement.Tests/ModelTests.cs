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

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
