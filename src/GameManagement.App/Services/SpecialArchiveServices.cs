using System.IO;
using GameManagement.Models;

namespace GameManagement.Services;

public static class SpecialArchiveComparisonService
{
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pak", ".assets", ".bundle", ".vpk", ".cpk", ".arc", ".big", ".obb", ".ress", ".resource", ".resources", ".pck", ".rpa"
    };

    public static async Task<List<SpecialArchiveDifferenceItem>> CompareAsync(string cleanRoot, string mixedRoot, CancellationToken token = default)
    {
        if (!Directory.Exists(cleanRoot) || !Directory.Exists(mixedRoot)) throw new InvalidOperationException("干净基准目录或混乱游戏目录不存在。");
        var clean = new Dictionary<string, (long Size, DateTime ModifiedAt, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(cleanRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            clean[Normalize(Path.GetRelativePath(cleanRoot, path))] = (info.Length, info.LastWriteTime, await FileFingerprintService.ComputeSha256Async(path, token));
        }

        var remaining = clean.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<SpecialArchiveDifferenceItem>();
        foreach (var path in Directory.EnumerateFiles(mixedRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }))
        {
            token.ThrowIfCancellationRequested();
            var relative = Normalize(Path.GetRelativePath(mixedRoot, path));
            var info = new FileInfo(path);
            if (!clean.TryGetValue(relative, out var baseline))
            {
                var hash = await FileFingerprintService.ComputeSha256Async(path, token);
                result.Add(Create(path, relative, "新增", info.Length, info.LastWriteTime, hash));
                continue;
            }
            remaining.Remove(relative);
            if (baseline.Size == info.Length && Math.Abs((baseline.ModifiedAt - info.LastWriteTime).TotalSeconds) < 1) continue;
            var currentHash = await FileFingerprintService.ComputeSha256Async(path, token);
            if (!currentHash.Equals(baseline.Hash, StringComparison.OrdinalIgnoreCase)) result.Add(Create(path, relative, "修改", info.Length, info.LastWriteTime, currentHash));
        }
        foreach (var relative in remaining.OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase))
        {
            var baseline = clean[relative];
            result.Add(new SpecialArchiveDifferenceItem { RelativePath = relative, SourcePath = Path.Combine(mixedRoot, relative), ChangeType = "缺失", FileSize = baseline.Size, ModifiedAt = baseline.ModifiedAt, Sha256 = baseline.Hash, DefaultExcluded = true, ExclusionReason = "混乱目录中缺少干净基准文件，仅供查看" });
        }
        return result.OrderBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task<List<SpecialArchiveDifferenceItem>> BuildManualSelectionAsync(string mixedRoot, CancellationToken token = default)
    {
        if (!Directory.Exists(mixedRoot)) throw new InvalidOperationException("混乱游戏目录不存在。");
        var result = new List<SpecialArchiveDifferenceItem>();
        foreach (var path in Directory.EnumerateFiles(mixedRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            result.Add(new SpecialArchiveDifferenceItem
            {
                RelativePath = Normalize(Path.GetRelativePath(mixedRoot, path)), SourcePath = path, ChangeType = "人工选择",
                FileSize = info.Length, ModifiedAt = info.LastWriteTime, Sha256 = await FileFingerprintService.ComputeSha256Async(path, token),
                DefaultExcluded = true, ExclusionReason = "无完整基线，必须由用户人工判断"
            });
        }
        return result;
    }

    public static List<SaveCandidateItem> CreateSaveCandidates(GameItem game, GameVersionItem version, string mixedRoot, IEnumerable<SpecialArchiveDifferenceItem> selected)
    {
        return selected.Where(item => item.ChangeType is "新增" or "修改" or "人工选择").Select(item => new SaveCandidateItem
        {
            GameId = game.Id, GameVersionId = version.Id, GameName = game.DisplayName, SourceKind = "游戏目录", SourcePath = item.SourcePath,
            SourceRootPath = mixedRoot, RelativePath = item.RelativePath, StorageRelativePath = item.RelativePath, ChangeType = "新增",
            FileSize = item.FileSize, ModifiedAt = item.ModifiedAt, Sha256 = item.Sha256, DefaultExcluded = item.DefaultExcluded,
            ExclusionReason = item.ExclusionReason, SnapshotKind = SaveSnapshotKinds.Normal
        }).ToList();
    }

    private static SpecialArchiveDifferenceItem Create(string path, string relative, string changeType, long size, DateTime modifiedAt, string hash)
    {
        var reasons = new List<string>();
        if (size >= SaveChangeDetectionService.LargeFileThreshold) reasons.Add("文件大于或等于 100 MB");
        if (ResourceExtensions.Contains(Path.GetExtension(path))) reasons.Add("疑似游戏资源包");
        return new SpecialArchiveDifferenceItem { SourcePath = path, RelativePath = relative, ChangeType = changeType, FileSize = size, ModifiedAt = modifiedAt, Sha256 = hash, DefaultExcluded = reasons.Count > 0, ExclusionReason = string.Join("；", reasons) };
    }

    private static string Normalize(string path) => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}

public static class SourceDeletionService
{
    public static IReadOnlyList<string> ResolveSourceFiles(GameVersionItem version)
    {
        if (File.Exists(version.SourcePath)) return ArchiveVolumeService.BuildGroup(version.SourcePath).Files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!Directory.Exists(version.SourcePath)) return [];
        if (string.IsNullOrWhiteSpace(version.FirstArchiveRelativePath)) return [];
        var archive = Path.GetFullPath(Path.Combine(version.SourcePath, version.FirstArchiveRelativePath));
        var normalizedRoot = Path.GetFullPath(version.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!archive.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(archive)) return [];
        return ArchiveVolumeService.BuildGroup(archive).Files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void MoveSourcesToRecycleBin(AppState state, GameItem game, GameVersionItem version, IReadOnlyCollection<string> files)
    {
        if (files.Count == 0) throw new InvalidOperationException("无法定位该版本的主压缩文件及相关分卷，禁止删除。");
        foreach (var path in files)
        {
            try
            {
                RecycleBinService.MoveFile(path);
                state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, GameVersionId = version.Id, ObjectType = "原始压缩包或分卷", ObjectPath = path, DeleteMethod = "Windows 回收站", Status = "成功" });
            }
            catch (Exception ex)
            {
                state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, GameVersionId = version.Id, ObjectType = "原始压缩包或分卷", ObjectPath = path, DeleteMethod = "Windows 回收站", Status = "失败", Message = ex.Message });
                throw;
            }
        }
    }
}

public static class GameRecordDeletionService
{
    public static bool HasLocalSaveData(AppState state, GameItem game) =>
        game.HasLocalSave || state.SaveSnapshots.Any(item => item.GameId == game.Id) || state.SaveFileRules.Any(item => item.GameId == game.Id) ||
        state.SaveFileExclusions.Any(item => item.GameId == game.Id) || state.SaveCandidates.Any(item => item.GameId == game.Id) ||
        state.GameDisks.Any(disk => Directory.Exists(Path.Combine(disk.RootPath, "GameSave", game.Id.ToString("N"))));

    public static bool HasSystemSaveConfiguration(AppState state, GameItem game) =>
        state.SystemSaveDirectories.Any(item => item.GameId == game.Id) || state.SystemMonitorSessions.Any(item => item.GameId == game.Id);

    public static bool HasTemporaryDirectories(AppState state, GameItem game) => state.GameDisks.Any(disk =>
        Directory.Exists(Path.Combine(disk.RootPath, "GameTemp", game.Id.ToString())) ||
        Directory.Exists(Path.Combine(disk.RootPath, "GameSaveTemp", game.Id.ToString())));

    public static IReadOnlyList<string> GetBlockers(AppState state, GameItem game)
    {
        var blockers = new List<string>();
        if (game.Versions.Count > 0) blockers.Add("仍存在游戏版本记录");
        if (!string.IsNullOrWhiteSpace(game.PlayableRootPath) && Directory.Exists(game.PlayableRootPath)) blockers.Add("仍存在可游玩目录");
        if (HasLocalSaveData(state, game)) blockers.Add("仍存在本地存档、快照、候选或规则");
        if (HasSystemSaveConfiguration(state, game)) blockers.Add("仍存在系统存档目录配置或监控记录");
        if (state.ExternalBackups.Any(item => item.GameId == game.Id)) blockers.Add("仍存在单游戏外部备份记录");
        if (game.RetainedSourcePaths.Any(path => File.Exists(path) || Directory.Exists(path))) blockers.Add("仍存在已保留的原始文件或目录");
        if (state.OperationTasks.Any(item => item.GameId == game.Id && item.Status is "运行中" or "等待")) blockers.Add("仍存在未完成任务");
        if (state.SystemMonitorSessions.Any(item => item.GameId == game.Id && item.Status == "监控中")) blockers.Add("仍存在监控会话");
        if (HasTemporaryDirectories(state, game)) blockers.Add("仍存在任务临时目录");
        return blockers;
    }

    public static void RemoveWithRelatedData(AppState state, GameItem game, GameRecordDeletionOptions options)
    {
        if (game.Status == "运行中") throw new InvalidOperationException("主游戏程序运行期间禁止删除游戏记录。");
        if (state.OperationTasks.Any(item => item.GameId == game.Id && item.Status is "运行中" or "等待")) throw new InvalidOperationException("仍存在未完成任务，禁止删除游戏记录。");
        if (state.SystemMonitorSessions.Any(item => item.GameId == game.Id && item.Status == "监控中")) throw new InvalidOperationException("仍存在活动监控会话，禁止删除游戏记录。");

        var uncheckedItems = new List<string>();
        if (game.Versions.Count > 0 && !options.DeleteVersions) uncheckedItems.Add("游戏版本记录");
        if (!string.IsNullOrWhiteSpace(game.PlayableRootPath) && Directory.Exists(game.PlayableRootPath) && !options.DeletePlayableDirectory) uncheckedItems.Add("可游玩目录");
        if (HasLocalSaveData(state, game) && !options.DeleteLocalSave) uncheckedItems.Add("本地存档、快照、候选或规则");
        if (HasSystemSaveConfiguration(state, game) && !options.DeleteSystemSaveConfiguration) uncheckedItems.Add("系统存档配置或监控记录");
        if (state.ExternalBackups.Any(item => item.GameId == game.Id) && !options.DeleteExternalBackups) uncheckedItems.Add("外部 ZIP 备份");
        if (HasTemporaryDirectories(state, game) && !options.DeleteTemporaryDirectories) uncheckedItems.Add("任务临时目录");
        if (game.RetainedSourcePaths.Any(path => File.Exists(path) || Directory.Exists(path)) && !options.DetachRetainedSources) uncheckedItems.Add("已保留原始文件关联");
        if (uncheckedItems.Count > 0) throw new InvalidOperationException($"以下关联数据仍存在且未勾选同步处理：\n\n- {string.Join("\n- ", uncheckedItems)}");

        if (options.DeletePlayableDirectory && !string.IsNullOrWhiteSpace(game.PlayableRootPath) && Directory.Exists(game.PlayableRootPath))
        {
            var path = game.PlayableRootPath;
            RecycleBinService.MoveDirectory(path);
            state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, ObjectType = "游戏可游玩目录", ObjectPath = path, DeleteMethod = "Windows 回收站", Status = "成功" });
            game.PlayableRootPath = string.Empty;
        }
        if (options.DeleteLocalSave)
        {
            foreach (var disk in state.GameDisks)
            {
                var root = Path.Combine(disk.RootPath, "GameSave", game.Id.ToString("N"));
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            state.SaveSnapshots.RemoveAll(item => item.GameId == game.Id);
            state.SaveCandidates.RemoveAll(item => item.GameId == game.Id);
            state.SaveFileRules.RemoveAll(item => item.GameId == game.Id);
            state.SaveFileExclusions.RemoveAll(item => item.GameId == game.Id);
            game.HasLocalSave = false;
            game.CurrentSaveGameDiskId = null;
            state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, ObjectType = "游戏全部本地存档", ObjectPath = game.DisplayName, DeleteMethod = "永久删除", Status = "成功" });
        }
        if (options.DeleteExternalBackups)
        {
            foreach (var backup in state.ExternalBackups.Where(item => item.GameId == game.Id).ToList()) ExternalBackupService.Remove(state, backup);
        }
        if (options.DeleteTemporaryDirectories)
        {
            foreach (var disk in state.GameDisks)
            {
                foreach (var root in new[] { Path.Combine(disk.RootPath, "GameTemp", game.Id.ToString()), Path.Combine(disk.RootPath, "GameSaveTemp", game.Id.ToString()) })
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
            state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, ObjectType = "游戏任务临时目录", ObjectPath = game.DisplayName, DeleteMethod = "永久删除", Status = "成功" });
        }
        if (options.DeleteSystemSaveConfiguration)
        {
            state.SystemSaveDirectories.RemoveAll(item => item.GameId == game.Id);
            state.SystemMonitorSessions.RemoveAll(item => item.GameId == game.Id);
        }
        if (options.DetachRetainedSources) game.RetainedSourcePaths.Clear();
        if (options.DeleteVersions)
        {
            foreach (var version in game.Versions.ToList())
            {
                state.Credentials.RemoveAll(item => item.GameVersionId == version.Id);
                state.FileBaselines.RemoveAll(item => item.GameVersionId == version.Id);
                state.SaveCandidates.RemoveAll(item => item.GameVersionId == version.Id);
                state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, GameVersionId = version.Id, ObjectType = "游戏版本记录", ObjectPath = version.VersionName, DeleteMethod = "数据库删除", Status = "成功", Message = "原始文件已保留" });
            }
            game.Versions.Clear();
        }
        Remove(state, game);
    }

    public static void Remove(AppState state, GameItem game)
    {
        if (GetBlockers(state, game).Count > 0) throw new InvalidOperationException("游戏主记录仍有关联数据，禁止删除。");
        state.SaveCandidates.RemoveAll(item => item.GameId == game.Id);
        state.SaveFileExclusions.RemoveAll(item => item.GameId == game.Id);
        state.SystemSaveDirectories.RemoveAll(item => item.GameId == game.Id);
        state.SystemMonitorSessions.RemoveAll(item => item.GameId == game.Id);
        state.Games.RemoveAll(item => item.Id == game.Id);
        state.DeletionHistory.Add(new DeletionHistoryItem { GameId = game.Id, ObjectType = "游戏主记录", ObjectPath = game.DisplayName, DeleteMethod = "数据库删除", Status = "成功" });
    }
}

public sealed class GameRecordDeletionOptions
{
    public bool DeleteVersions { get; set; }
    public bool DeletePlayableDirectory { get; set; }
    public bool DeleteLocalSave { get; set; }
    public bool DeleteSystemSaveConfiguration { get; set; }
    public bool DeleteExternalBackups { get; set; }
    public bool DeleteTemporaryDirectories { get; set; }
    public bool DetachRetainedSources { get; set; }
}
