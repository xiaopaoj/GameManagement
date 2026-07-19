using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameManagement.Models;

namespace GameManagement.Services;

public sealed record SystemMonitorProgress(int FileCount, string CurrentPath);

public static class SystemSaveMonitoringService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static IReadOnlyList<string> GetCommonDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(userProfile, "Saved Games")
        }.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<SystemMonitorSessionItem?> BeginSessionAsync(AppState state, GameItem game, IProgress<SystemMonitorProgress>? progress = null, CancellationToken token = default)
    {
        var isInitial = !game.SystemSaveInitialScanCompleted;
        var directories = isInitial
            ? GetCommonDirectories().ToList()
            : state.SystemSaveDirectories.Where(item => item.GameId == game.Id && item.Enabled && Directory.Exists(item.Path)).Select(item => Path.GetFullPath(item.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (directories.Count == 0) return null;

        foreach (var stale in state.SystemMonitorSessions.Where(item => item.GameId == game.Id && item.Status == "监控中").ToList())
        {
            stale.Status = "已替换"; stale.CompletedAt = DateTime.Now;
            TryDeleteSnapshotFile(stale.SnapshotFilePath);
        }
        var session = new SystemMonitorSessionItem
        {
            GameId = game.Id,
            IsInitialCommonScan = isInitial,
            DirectoryPaths = directories,
            SnapshotFilePath = Path.Combine(AppPaths.Data, "cache", "system-monitor", $"{game.Id:N}-{Guid.NewGuid():N}.json.gz")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(session.SnapshotFilePath)!);
        var snapshot = await CaptureAsync(directories, progress, token);
        await WriteSnapshotAsync(session.SnapshotFilePath, snapshot, token);
        state.SystemMonitorSessions.Add(session);
        return session;
    }

    public static async Task<List<SaveCandidateItem>> CompleteSessionAsync(AppState state, GameItem game, string snapshotKind, IProgress<SystemMonitorProgress>? progress = null, CancellationToken token = default)
    {
        var session = state.SystemMonitorSessions.Where(item => item.GameId == game.Id && item.Status == "监控中").OrderByDescending(item => item.StartedAt).FirstOrDefault();
        if (session is null || !File.Exists(session.SnapshotFilePath)) return [];
        var before = await ReadSnapshotAsync(session.SnapshotFilePath, token);
        var after = await CaptureAsync(session.DirectoryPaths.Where(Directory.Exists).ToList(), progress, token);
        var result = await CompareAsync(state, game, snapshotKind, session.IsInitialCommonScan, before, after, token);
        session.Status = "已完成"; session.CompletedAt = DateTime.Now;
        foreach (var rule in state.SystemSaveDirectories.Where(item => item.GameId == game.Id && session.DirectoryPaths.Contains(item.Path, StringComparer.OrdinalIgnoreCase))) rule.LastScannedAt = DateTime.Now;
        TryDeleteSnapshotFile(session.SnapshotFilePath);
        return result;
    }

    public static void ReplaceDetectedCandidates(AppState state, GameItem game, IEnumerable<SaveCandidateItem> candidates)
    {
        state.SaveCandidates.RemoveAll(item => item.GameId == game.Id && item.SourceKind == "系统目录");
        state.SaveCandidates.AddRange(candidates);
    }

    public static void MarkInitialScanCompleted(GameItem game)
    {
        game.SystemSaveInitialScanCompleted = true;
        game.SystemSaveInitialScanCompletedAt = DateTime.Now;
    }

    public static void CancelLatestSession(AppState state, GameItem game, string reason)
    {
        var session = state.SystemMonitorSessions.Where(item => item.GameId == game.Id && item.Status == "监控中").OrderByDescending(item => item.StartedAt).FirstOrDefault();
        if (session is null) return;
        session.Status = reason; session.CompletedAt = DateTime.Now; TryDeleteSnapshotFile(session.SnapshotFilePath);
    }

    public static void EnsureDirectoryRule(AppState state, GameItem game, string path, bool autoDiscovered)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (state.SystemSaveDirectories.Any(item => item.GameId == game.Id && PathsEqual(item.Path, normalized))) return;
        state.SystemSaveDirectories.Add(new SystemSaveDirectoryRuleItem
        {
            GameId = game.Id,
            Path = normalized,
            DisplayName = new DirectoryInfo(normalized).Name,
            AutoDiscovered = autoDiscovered
        });
    }

    public static bool IsSharedDirectory(AppState state, string path) => state.SystemSaveDirectories
        .Where(item => item.Enabled && PathsEqual(item.Path, path)).Select(item => item.GameId).Distinct().Count() > 1;

    private static Task<List<SystemFileSnapshotItem>> CaptureAsync(IReadOnlyCollection<string> roots, IProgress<SystemMonitorProgress>? progress, CancellationToken token) => Task.Run(() =>
    {
        var result = new List<SystemFileSnapshotItem>();
        var count = 0;
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLogger.Error($"系统存档目录扫描失败：{root}", ex); continue; }
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    result.Add(new SystemFileSnapshotItem { FullPath = info.FullName, RootPath = root, FileSize = info.Length, ModifiedAt = info.LastWriteTime });
                    count++;
                    if (count % 100 == 0) progress?.Report(new SystemMonitorProgress(count, file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        progress?.Report(new SystemMonitorProgress(count, "扫描完成"));
        return result;
    }, token);

    internal static async Task<List<SaveCandidateItem>> CompareAsync(AppState state, GameItem game, string snapshotKind, bool initialScan, IReadOnlyCollection<SystemFileSnapshotItem> before, IReadOnlyCollection<SystemFileSnapshotItem> after, CancellationToken token)
    {
        var normalizedBefore = before.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.RootPath.Length).First()).ToList();
        var normalizedAfter = after.GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.RootPath.Length).First()).ToList();
        var beforeMap = normalizedBefore.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
        var afterMap = normalizedAfter.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
        var changed = new List<(SystemFileSnapshotItem Item, string ChangeType)>();
        foreach (var item in normalizedAfter)
        {
            token.ThrowIfCancellationRequested();
            if (!beforeMap.TryGetValue(item.FullPath, out var original)) changed.Add((item, "新增"));
            else if (original.FileSize != item.FileSize || Math.Abs((original.ModifiedAt - item.ModifiedAt).TotalSeconds) >= 1) changed.Add((item, "修改"));
        }
        foreach (var item in normalizedBefore.Where(item => !afterMap.ContainsKey(item.FullPath))) changed.Add((item, "删除"));

        var result = new List<SaveCandidateItem>();
        foreach (var change in changed.OrderBy(item => item.Item.FullPath, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var fullPath = change.Item.FullPath;
            var sourceRoot = initialScan ? Path.GetDirectoryName(fullPath)! : change.Item.RootPath;
            var relativePath = initialScan ? Path.GetFileName(fullPath) : Path.GetRelativePath(sourceRoot, fullPath);
            var storageRelativePath = Path.Combine("system", GetPathKey(sourceRoot), relativePath);
            var confirmed = state.SaveFileRules.Any(item => item.GameId == game.Id && item.SourceKind == "系统目录" && PathsEqual(item.SourceRootPath, sourceRoot) && item.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            var excluded = state.SaveFileExclusions.Any(item => item.GameId == game.Id && item.SourceKind == "系统目录" && PathsEqual(item.SourceRootPath, sourceRoot) && item.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
            var shared = IsSharedDirectory(state, sourceRoot);
            var reasons = new List<string>();
            if (!confirmed && change.Item.FileSize >= SaveChangeDetectionService.LargeFileThreshold) reasons.Add("文件大于或等于 100 MB");
            if (shared) reasons.Add("共享目录中的文件需要再次确认归属");
            if (excluded) reasons.Insert(0, "用户已按具体文件排除");
            var hash = change.ChangeType == "删除" || !File.Exists(fullPath) ? string.Empty : await FileFingerprintService.ComputeSha256Async(fullPath, token);
            result.Add(new SaveCandidateItem
            {
                GameId = game.Id,
                GameVersionId = game.CurrentVersionId ?? Guid.Empty,
                GameName = game.DisplayName,
                SourceKind = "系统目录",
                SourcePath = fullPath,
                SourceRootPath = sourceRoot,
                RelativePath = relativePath,
                StorageRelativePath = storageRelativePath,
                ChangeType = change.ChangeType,
                FileSize = change.Item.FileSize,
                ModifiedAt = change.Item.ModifiedAt,
                Sha256 = hash,
                DefaultExcluded = reasons.Count > 0,
                ExclusionReason = string.Join("；", reasons),
                Decision = excluded ? SaveCandidateDecisions.Excluded : SaveCandidateDecisions.Pending,
                SnapshotKind = snapshotKind,
                PreviouslyConfirmed = confirmed,
                SharedDirectory = shared
            });
        }
        return result;
    }

    private static async Task WriteSnapshotAsync(string path, List<SystemFileSnapshotItem> snapshot, CancellationToken token)
    {
        await using var file = File.Create(path);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await JsonSerializer.SerializeAsync(gzip, snapshot, JsonOptions, token);
    }

    private static async Task<List<SystemFileSnapshotItem>> ReadSnapshotAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<List<SystemFileSnapshotItem>>(gzip, JsonOptions, token) ?? [];
    }

    private static string GetPathKey(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteSnapshotFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { AppLogger.Error($"系统存档扫描缓存自动清理失败：{path}", ex); }
    }
}
