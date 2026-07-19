using System.IO;
using System.Text.Json;
using GameManagement.Models;
using Microsoft.VisualBasic.FileIO;

namespace GameManagement.Services;

public sealed record ArchiveReadinessResult(bool Ready, IReadOnlyList<string> Problems, SaveSnapshotManifest? Manifest, ExternalBackupItem? Backup);

public static class CurrentSaveManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static string GetManifestPath(AppState state, GameItem game) => Path.Combine(GameSavePathService.GetGameSaveRoot(state, game), "manifest.json");

    public static async Task<SaveSnapshotManifest?> LoadAsync(AppState state, GameItem game, CancellationToken token = default)
    {
        var path = GetManifestPath(state, game);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SaveSnapshotManifest>(stream, JsonOptions, token);
    }

    public static async Task<bool> VerifyAsync(AppState state, GameItem game, SaveSnapshotManifest manifest, CancellationToken token = default)
    {
        var current = GameSavePathService.GetCurrentDirectory(state, game);
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = GetSafeChildPath(current, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.FileSize) return false;
            if (!(await FileFingerprintService.ComputeSha256Async(path, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static string GetSafeChildPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("存档相对路径超出允许目录范围。");
        return target;
    }
}

public static class OrdinaryArchiveService
{
    public static async Task<ArchiveReadinessResult> CheckReadinessAsync(AppState state, GameItem game, CancellationToken token = default)
    {
        var problems = new List<string>();
        if (game.Status == "运行中" || game.RunningProcessId.HasValue) problems.Add("主游戏程序仍在运行。");
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || !Directory.Exists(game.PlayableRootPath)) problems.Add("可游玩目录不存在。");
        if (!game.SystemSaveInitialScanCompleted) problems.Add("首次 Windows 常见存档目录扫描尚未人工确认完成。");
        if (state.SystemMonitorSessions.Any(item => item.GameId == game.Id && item.Status == "监控中")) problems.Add("仍存在未完成的系统存档监控会话。");
        if (state.SaveCandidates.Any(item => item.GameId == game.Id && item.Decision == SaveCandidateDecisions.Pending)) problems.Add("仍有待确认的存档变化。");

        SaveSnapshotManifest? manifest = null;
        try
        {
            manifest = await CurrentSaveManifestService.LoadAsync(state, game, token);
            if (manifest is null) problems.Add("当前存档清单不存在，请先完成存档收集。");
            else if (!await CurrentSaveManifestService.VerifyAsync(state, game, manifest, token)) problems.Add("当前存档文件或 Hash 校验失败。");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            problems.Add($"读取当前存档清单失败：{ex.Message}");
        }

        ExternalBackupItem? backup = null;
        if (manifest is not null)
        {
            backup = state.ExternalBackups
                .Where(item => item.GameId == game.Id && item.Verified && File.Exists(item.FilePath) && item.ContentFingerprint.Equals(manifest.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.VerifiedAt ?? item.CreatedAt)
                .FirstOrDefault();
            if (backup is null) problems.Add("最新存档尚未生成并校验有效的外部 ZIP 备份。");
        }
        return new ArchiveReadinessResult(problems.Count == 0, problems, manifest, backup);
    }

    public static void MarkArchived(GameItem game, SaveSnapshotManifest manifest)
    {
        game.ArchiveStatus = "已归档";
        game.Status = "已归档";
        game.ArchivedAt = DateTime.Now;
        game.ArchivedVersionId = game.CurrentVersionId;
        game.ArchivedSnapshotId = manifest.SnapshotId == Guid.Empty ? null : manifest.SnapshotId;
        game.ArchivedContentFingerprint = manifest.ContentFingerprint;
        game.ArchiveMessage = "本地存档、清单及外部 ZIP 备份均已校验完成。";
        game.DirectoryCleanupStatus = !string.IsNullOrWhiteSpace(game.PlayableRootPath) && Directory.Exists(game.PlayableRootPath) ? "等待清理" : "目录不存在";
    }

    public static void MarkCleanupSucceeded(GameItem game)
    {
        game.DirectoryCleanupStatus = "已移入回收站";
        game.PlayableRootPath = null;
        game.ExecutableRelativePath = null;
        game.RunningProcessId = null;
    }

    public static void MarkCleanupFailed(GameItem game, string message)
    {
        game.DirectoryCleanupStatus = "清理失败";
        game.ArchiveMessage = $"归档已完成，但游戏目录清理失败：{message}";
    }
}

public static class RecycleBinService
{
    public static void MoveDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前系统不支持 Windows 回收站，操作已终止。");
        if (!Directory.Exists(path)) return;
        FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }

    public static void MoveFile(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("当前系统不支持 Windows 回收站，操作已终止。");
        if (!File.Exists(path)) return;
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }
}

public static class CrossDiskSaveCopyService
{
    public static bool RequiresCopy(AppState state, GameItem game, GameDiskItem targetDisk)
    {
        var sourceDiskId = game.CurrentSaveGameDiskId ?? game.CurrentGameDiskId;
        return sourceDiskId.HasValue && sourceDiskId.Value != targetDisk.Id && Directory.Exists(GetCurrentDirectory(state, game, sourceDiskId.Value));
    }

    public static bool TargetCurrentExists(GameItem game, GameDiskItem targetDisk) => Directory.Exists(Path.Combine(targetDisk.RootPath, "GameSave", game.Id.ToString("N"), "current"));

    public static async Task CopyAsync(AppState state, GameItem game, GameDiskItem targetDisk, CancellationToken token = default)
    {
        var sourceDiskId = game.CurrentSaveGameDiskId ?? game.CurrentGameDiskId ?? throw new InvalidOperationException("找不到当前主存档所在游戏盘。");
        if (sourceDiskId == targetDisk.Id) { game.CurrentSaveGameDiskId = targetDisk.Id; return; }
        var sourceRoot = GetGameSaveRoot(state, game, sourceDiskId);
        var sourceCurrent = Path.Combine(sourceRoot, "current");
        var sourceManifest = Path.Combine(sourceRoot, "manifest.json");
        if (!Directory.Exists(sourceCurrent) || !File.Exists(sourceManifest)) throw new InvalidOperationException("来源游戏盘上的 current 存档或清单不存在。");

        var targetRoot = Path.Combine(targetDisk.RootPath, "GameSave", game.Id.ToString("N"));
        var targetCurrent = Path.Combine(targetRoot, "current");
        var temporary = Path.Combine(targetRoot, $".current-copy-{Guid.NewGuid():N}");
        var previous = Path.Combine(targetRoot, $".current-previous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetRoot);
        try
        {
            await SourceCopyService.CopyDirectoryAsync(sourceCurrent, temporary, token);
            var manifest = JsonSerializer.Deserialize<SaveSnapshotManifest>(await File.ReadAllTextAsync(sourceManifest, token)) ?? throw new InvalidDataException("来源主存档清单无效。");
            foreach (var file in manifest.Files)
            {
                var copied = CurrentSaveManifestService.GetSafeChildPath(temporary, file.RelativePath);
                if (!File.Exists(copied) || !(await FileFingerprintService.ComputeSha256Async(copied, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("跨游戏盘存档临时副本校验失败。");
            }
            if (Directory.Exists(targetCurrent)) Directory.Move(targetCurrent, previous);
            Directory.Move(temporary, targetCurrent);
            File.Copy(sourceManifest, Path.Combine(targetRoot, "manifest.json"), true);
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            game.CurrentSaveGameDiskId = targetDisk.Id;
        }
        catch
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            if (!Directory.Exists(targetCurrent) && Directory.Exists(previous)) Directory.Move(previous, targetCurrent);
            throw;
        }
    }

    private static string GetGameSaveRoot(AppState state, GameItem game, Guid diskId)
    {
        var disk = state.GameDisks.FirstOrDefault(item => item.Id == diskId && Directory.Exists(item.RootPath)) ?? throw new InvalidOperationException("当前主存档所在游戏盘不可用。");
        return Path.Combine(disk.RootPath, "GameSave", game.Id.ToString("N"));
    }

    private static string GetCurrentDirectory(AppState state, GameItem game, Guid diskId) => Path.Combine(GetGameSaveRoot(state, game, diskId), "current");
}

public sealed record SaveRestoreResult(int GameFileCount, int SystemFileCount, Guid SourceVersionId);

public static class SaveRestoreService
{
    public static async Task<SaveRestoreResult?> RestoreCurrentAsync(AppState state, GameItem game, GameVersionItem targetVersion, string stagedGameRoot, CancellationToken token = default)
    {
        var manifest = await CurrentSaveManifestService.LoadAsync(state, game, token);
        if (manifest is null || manifest.Files.Count == 0) return null;
        if (!await CurrentSaveManifestService.VerifyAsync(state, game, manifest, token)) throw new InvalidDataException("共享存档恢复前校验失败。");
        var current = GameSavePathService.GetCurrentDirectory(state, game);
        var changes = new List<(string Target, string? Backup, bool Existed)>();
        var rollbackRoot = Path.Combine(Path.GetTempPath(), "GameManagement", "restore", Guid.NewGuid().ToString("N"));
        var gameCount = 0;
        var systemCount = 0;
        try
        {
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = CurrentSaveManifestService.GetSafeChildPath(current, file.RelativePath);
                string target;
                if (file.SourceKind == "系统目录")
                {
                    if (string.IsNullOrWhiteSpace(file.OriginalPath) || !Path.IsPathFullyQualified(file.OriginalPath)) throw new InvalidDataException("系统存档缺少有效的原始恢复路径。");
                    target = Path.GetFullPath(file.OriginalPath);
                    systemCount++;
                }
                else
                {
                    var rule = state.SaveFileRules.LastOrDefault(item => item.GameId == game.Id && item.SourceKind == "游戏目录" && (string.IsNullOrWhiteSpace(item.StorageRelativePath) ? item.RelativePath : item.StorageRelativePath).Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
                    var relativePath = rule?.RelativePath ?? file.RelativePath;
                    target = CurrentSaveManifestService.GetSafeChildPath(stagedGameRoot, relativePath);
                    gameCount++;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var existed = File.Exists(target);
                string? backup = null;
                if (existed)
                {
                    backup = Path.Combine(rollbackRoot, changes.Count.ToString("D8"));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                }
                changes.Add((target, backup, existed));
                File.Copy(source, target, true);
                if (!(await FileFingerprintService.ComputeSha256Async(target, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"恢复后的存档文件校验失败：{target}");
            }
            return new SaveRestoreResult(gameCount, systemCount, manifest.GameVersionId);
        }
        catch
        {
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Existed && change.Backup is not null) File.Copy(change.Backup, change.Target, true);
                    else if (File.Exists(change.Target)) File.Delete(change.Target);
                }
                catch (Exception ex) { AppLogger.Error($"存档恢复失败后的回滚失败：{change.Target}", ex); }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(rollbackRoot))
            {
                try { Directory.Delete(rollbackRoot, true); }
                catch (Exception ex) { AppLogger.Error("存档恢复回滚临时目录清理失败", ex); }
            }
        }
    }
}
