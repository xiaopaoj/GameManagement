using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using GameManagement.Models;

namespace GameManagement.Services;

public static class GameSavePathService
{
    public static GameDiskItem GetCurrentDisk(AppState state, GameItem game)
    {
        var diskIds = new[] { game.CurrentGameDiskId, game.CurrentSaveGameDiskId }.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var disk = diskIds.Select(id => state.GameDisks.FirstOrDefault(item => item.Id == id && Directory.Exists(item.RootPath))).FirstOrDefault(item => item is not null);
        return disk ?? throw new InvalidOperationException("当前游戏没有可用的游戏盘，无法保存本地存档。");
    }

    public static string GetGameSaveRoot(AppState state, GameItem game) => Path.Combine(GetCurrentDisk(state, game).RootPath, "GameSave", game.Id.ToString("N"));
    public static string GetCurrentDirectory(AppState state, GameItem game) => Path.Combine(GetGameSaveRoot(state, game), "current");
}

public static class SaveChangeDetectionService
{
    public const long LargeFileThreshold = 100L * 1024 * 1024;
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pak", ".assets", ".bundle", ".vpk", ".cpk", ".arc", ".big", ".obb", ".ress", ".resource", ".resources", ".pck"
    };

    public static async Task<List<SaveCandidateItem>> DetectAsync(AppState state, GameItem game, string snapshotKind, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || !Directory.Exists(game.PlayableRootPath)) throw new InvalidOperationException("可游玩目录不存在，无法扫描游戏目录变化。");
        var versionId = game.CurrentVersionId ?? throw new InvalidOperationException("当前游戏没有有效版本。");
        var baselines = state.FileBaselines.Where(item => item.GameVersionId == versionId)
            .ToDictionary(item => NormalizeRelativePath(item.RelativePath), StringComparer.OrdinalIgnoreCase);
        if (baselines.Count == 0) throw new InvalidOperationException("当前版本没有文件基线，禁止自动判断存档文件。");

        var confirmed = state.SaveFileRules.Where(item => item.GameId == game.Id && item.SourceKind == "游戏目录")
            .Select(item => NormalizeRelativePath(item.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = state.SaveFileExclusions.Where(item => item.GameId == game.Id && item.SourceKind == "游戏目录")
            .Select(item => NormalizeRelativePath(item.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingBaselines = new HashSet<string>(baselines.Keys, StringComparer.OrdinalIgnoreCase);
        var result = new List<SaveCandidateItem>();

        foreach (var file in Directory.EnumerateFiles(game.PlayableRootPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }))
        {
            token.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(game.PlayableRootPath, file));
            var info = new FileInfo(file);
            baselines.TryGetValue(relativePath, out var baseline);
            if (baseline is not null)
            {
                remainingBaselines.Remove(relativePath);
                if (baseline.FileSize == info.Length && Math.Abs((baseline.ModifiedAt - info.LastWriteTime).TotalSeconds) < 1) continue;
                var hash = await FileFingerprintService.ComputeSha256Async(file, token);
                if (hash.Equals(baseline.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(CreateCandidate(game, versionId, snapshotKind, file, relativePath, "修改", info, hash, true, confirmed, excluded));
            }
            else
            {
                var hash = await FileFingerprintService.ComputeSha256Async(file, token);
                result.Add(CreateCandidate(game, versionId, snapshotKind, file, relativePath, "新增", info, hash, false, confirmed, excluded));
            }
        }

        foreach (var relativePath in remainingBaselines.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var baseline = baselines[relativePath];
            var expectedPath = Path.Combine(game.PlayableRootPath, relativePath);
            result.Add(CreateCandidate(game, versionId, snapshotKind, expectedPath, relativePath, "删除", null, string.Empty, true, confirmed, excluded, baseline.FileSize, baseline.ModifiedAt));
        }
        return result.OrderBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void ReplaceDetectedCandidates(AppState state, GameItem game, IEnumerable<SaveCandidateItem> candidates)
    {
        state.SaveCandidates.RemoveAll(item => item.GameId == game.Id && item.SourceKind == "游戏目录");
        state.SaveCandidates.AddRange(candidates);
    }

    private static SaveCandidateItem CreateCandidate(GameItem game, Guid versionId, string snapshotKind, string path, string relativePath, string changeType, FileInfo? info, string hash, bool existedInBaseline, HashSet<string> confirmed, HashSet<string> excluded, long deletedSize = 0, DateTime? deletedModifiedAt = null)
    {
        var reasons = new List<string>();
        var normalized = NormalizeRelativePath(relativePath);
        var isConfirmed = confirmed.Contains(normalized);
        var isExplicitlyExcluded = excluded.Contains(normalized);
        var size = info?.Length ?? deletedSize;
        if (!isConfirmed)
        {
            if (size >= LargeFileThreshold) reasons.Add("文件大于或等于 100 MB");
            if (existedInBaseline) reasons.Add("首次基线中已经存在");
            if (ResourceExtensions.Contains(Path.GetExtension(path))) reasons.Add("疑似游戏资源包");
        }
        if (isExplicitlyExcluded) reasons.Insert(0, "用户已按具体文件排除");
        return new SaveCandidateItem
        {
            GameId = game.Id,
            GameVersionId = versionId,
            GameName = game.DisplayName,
            SourcePath = path,
            SourceRootPath = game.PlayableRootPath ?? string.Empty,
            RelativePath = normalized,
            StorageRelativePath = normalized,
            ChangeType = changeType,
            FileSize = size,
            ModifiedAt = info?.LastWriteTime ?? deletedModifiedAt,
            Sha256 = hash,
            DefaultExcluded = reasons.Count > 0,
            PreviouslyConfirmed = isConfirmed,
            ExclusionReason = string.Join("；", reasons),
            Decision = isExplicitlyExcluded ? SaveCandidateDecisions.Excluded : SaveCandidateDecisions.Pending,
            SnapshotKind = snapshotKind
        };
    }

    private static string NormalizeRelativePath(string path) => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}

public sealed record SaveSnapshotCreationResult(SaveSnapshotItem? Snapshot, bool ContentChanged, int AppliedCount);

public static class SaveSnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<SaveSnapshotCreationResult> ApplyAndCreateAsync(AppState state, GameItem game, IReadOnlyCollection<Guid> candidateIds, CancellationToken token = default)
    {
        var candidates = state.SaveCandidates.Where(item => candidateIds.Contains(item.Id) && item.GameId == game.Id && item.Decision == SaveCandidateDecisions.Pending).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("没有选择可确认的存档候选。");
        if (candidates.Select(item => item.SnapshotKind).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw new InvalidOperationException("一次只能处理同一种正常或异常快照候选。");
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || !Directory.Exists(game.PlayableRootPath)) throw new InvalidOperationException("可游玩目录不存在，无法收集存档文件。");

        var saveRoot = GameSavePathService.GetGameSaveRoot(state, game);
        var currentDirectory = Path.Combine(saveRoot, "current");
        var workingDirectory = Path.Combine(saveRoot, $".current-temp-{Guid.NewGuid():N}");
        var previousDirectory = Path.Combine(saveRoot, $".current-previous-{Guid.NewGuid():N}");
        string? temporarySnapshotDirectory = null;
        Directory.CreateDirectory(saveRoot);
        try
        {
            if (Directory.Exists(currentDirectory)) await SourceCopyService.CopyDirectoryAsync(currentDirectory, workingDirectory, token);
            else Directory.CreateDirectory(workingDirectory);
            var before = await CaptureManifestAsync(state, game, candidates[0].SnapshotKind, currentDirectory, Guid.Empty, token);

            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                var storageRelativePath = string.IsNullOrWhiteSpace(candidate.StorageRelativePath) ? candidate.RelativePath : candidate.StorageRelativePath;
                var target = GetSafeChildPath(workingDirectory, storageRelativePath);
                if (candidate.ChangeType == "删除")
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                else
                {
                    var sourceRoot = candidate.SourceKind == "系统目录" ? candidate.SourceRootPath : game.PlayableRootPath;
                    if (string.IsNullOrWhiteSpace(sourceRoot)) throw new InvalidOperationException("候选文件缺少有效来源根目录。");
                    var source = GetSafeChildPath(sourceRoot, candidate.RelativePath);
                    if (!File.Exists(source)) throw new FileNotFoundException("待确认的存档候选已经不存在。", source);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, true);
                }
            }

            DeleteEmptyDirectories(workingDirectory);
            var snapshotId = Guid.NewGuid();
            var after = await CaptureManifestAsync(state, game, candidates[0].SnapshotKind, workingDirectory, snapshotId, token, candidates);
            if (before.ContentFingerprint.Equals(after.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(workingDirectory, true);
                after.SnapshotId = Guid.Empty;
                await File.WriteAllTextAsync(Path.Combine(saveRoot, "manifest.json"), JsonSerializer.Serialize(after, JsonOptions), Encoding.UTF8, token);
                ApplyCandidateDecisions(state, game, candidates);
                game.HasLocalSave = after.Files.Count > 0;
                game.CurrentSaveGameDiskId = GameSavePathService.GetCurrentDisk(state, game).Id;
                return new SaveSnapshotCreationResult(null, false, candidates.Count);
            }

            var snapshotFolderName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{snapshotId:N}";
            var snapshotParent = candidates[0].SnapshotKind == SaveSnapshotKinds.Abnormal ? "abnormal-snapshots" : "normal-snapshots";
            var snapshotDirectory = Path.Combine(saveRoot, snapshotParent, snapshotFolderName);
            temporarySnapshotDirectory = snapshotDirectory + ".partial";
            Directory.CreateDirectory(temporarySnapshotDirectory);
            foreach (var file in after.Files)
            {
                token.ThrowIfCancellationRequested();
                var source = GetSafeChildPath(workingDirectory, file.RelativePath);
                var target = GetSafeChildPath(temporarySnapshotDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            var temporaryManifestPath = Path.Combine(temporarySnapshotDirectory, "manifest.json");
            await File.WriteAllTextAsync(temporaryManifestPath, JsonSerializer.Serialize(after, JsonOptions), Encoding.UTF8, token);
            if (!await VerifySnapshotAsync(temporarySnapshotDirectory, after, token))
            {
                Directory.Delete(temporarySnapshotDirectory, true);
                Directory.Delete(workingDirectory, true);
                throw new InvalidDataException("本地存档快照 Hash 校验失败，未替换 current 存档。");
            }

            var currentMoved = false;
            var workingMoved = false;
            var snapshotMoved = false;
            try
            {
                if (Directory.Exists(currentDirectory)) { Directory.Move(currentDirectory, previousDirectory); currentMoved = true; }
                Directory.Move(workingDirectory, currentDirectory); workingMoved = true;
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotDirectory)!);
                Directory.Move(temporarySnapshotDirectory, snapshotDirectory); snapshotMoved = true;
                await File.WriteAllTextAsync(Path.Combine(saveRoot, "manifest.json"), JsonSerializer.Serialize(after, JsonOptions), Encoding.UTF8, token);
            }
            catch
            {
                if (snapshotMoved && Directory.Exists(snapshotDirectory)) Directory.Delete(snapshotDirectory, true);
                else if (Directory.Exists(temporarySnapshotDirectory)) Directory.Delete(temporarySnapshotDirectory, true);
                if (workingMoved && Directory.Exists(currentDirectory)) Directory.Delete(currentDirectory, true);
                else if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, true);
                if (currentMoved && Directory.Exists(previousDirectory)) Directory.Move(previousDirectory, currentDirectory);
                throw;
            }

            if (Directory.Exists(previousDirectory))
            {
                try { Directory.Delete(previousDirectory, true); }
                catch (Exception ex) { AppLogger.Error("旧 current 临时目录自动清理失败", ex); }
            }
            ApplyCandidateDecisions(state, game, candidates);
            game.HasLocalSave = after.Files.Count > 0;
            game.CurrentSaveGameDiskId = GameSavePathService.GetCurrentDisk(state, game).Id;
            var manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
            var snapshot = new SaveSnapshotItem
            {
                Id = snapshotId,
                GameId = game.Id,
                GameVersionId = game.CurrentVersionId ?? Guid.Empty,
                GameName = game.DisplayName,
                SnapshotKind = candidates[0].SnapshotKind,
                DirectoryPath = snapshotDirectory,
                ManifestPath = manifestPath,
                FileCount = after.Files.Count,
                TotalSize = after.Files.Sum(item => item.FileSize),
                ContentFingerprint = after.ContentFingerprint,
                ExitCode = game.LastExitCode,
                Verified = true
            };
            state.SaveSnapshots.Add(snapshot);
            UpdateCleanupSuggestions(state, game.Id, snapshot.SnapshotKind);
            return new SaveSnapshotCreationResult(snapshot, true, candidates.Count);
        }
        finally
        {
            TryDeleteTemporaryDirectory(workingDirectory);
            if (!string.IsNullOrWhiteSpace(temporarySnapshotDirectory)) TryDeleteTemporaryDirectory(temporarySnapshotDirectory);
        }
    }

    private static void ApplyCandidateDecisions(AppState state, GameItem game, IEnumerable<SaveCandidateItem> candidates)
    {
        foreach (var candidate in candidates)
        {
            candidate.Decision = SaveCandidateDecisions.Confirmed;
            if (!state.SaveFileRules.Any(item => item.GameId == game.Id && item.SourceKind == candidate.SourceKind && item.SourceRootPath.Equals(candidate.SourceRootPath, StringComparison.OrdinalIgnoreCase) && item.RelativePath.Equals(candidate.RelativePath, StringComparison.OrdinalIgnoreCase)))
                state.SaveFileRules.Add(new SaveFileRuleItem
                {
                    GameId = game.Id,
                    RelativePath = candidate.RelativePath,
                    SourceKind = candidate.SourceKind,
                    SourceRootPath = candidate.SourceRootPath,
                    StorageRelativePath = string.IsNullOrWhiteSpace(candidate.StorageRelativePath) ? candidate.RelativePath : candidate.StorageRelativePath
                });
            if (candidate.SourceKind == "系统目录" && Directory.Exists(candidate.SourceRootPath)) SystemSaveMonitoringService.EnsureDirectoryRule(state, game, candidate.SourceRootPath, true);
            state.SaveFileExclusions.RemoveAll(item => item.GameId == game.Id
                && item.SourceKind == candidate.SourceKind
                && item.SourceRootPath.Equals(candidate.SourceRootPath, StringComparison.OrdinalIgnoreCase)
                && item.RelativePath.Equals(candidate.RelativePath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static void ExcludeCandidates(AppState state, GameItem game, IEnumerable<Guid> candidateIds)
    {
        foreach (var candidate in state.SaveCandidates.Where(item => candidateIds.Contains(item.Id) && item.GameId == game.Id))
        {
            candidate.Decision = SaveCandidateDecisions.Excluded;
            if (!state.SaveFileExclusions.Any(item => item.GameId == game.Id && item.SourceKind == candidate.SourceKind && item.SourceRootPath.Equals(candidate.SourceRootPath, StringComparison.OrdinalIgnoreCase) && item.RelativePath.Equals(candidate.RelativePath, StringComparison.OrdinalIgnoreCase)))
                state.SaveFileExclusions.Add(new SaveFileExclusionItem { GameId = game.Id, RelativePath = candidate.RelativePath, SourceKind = candidate.SourceKind, SourceRootPath = candidate.SourceRootPath });
        }
    }

    public static void RemoveSnapshot(AppState state, SaveSnapshotItem snapshot)
    {
        if (Directory.Exists(snapshot.DirectoryPath)) Directory.Delete(snapshot.DirectoryPath, true);
        state.SaveSnapshots.RemoveAll(item => item.Id == snapshot.Id);
        UpdateCleanupSuggestions(state, snapshot.GameId, snapshot.SnapshotKind);
    }

    private static async Task<SaveSnapshotManifest> CaptureManifestAsync(AppState state, GameItem game, string snapshotKind, string directory, Guid snapshotId, CancellationToken token, IReadOnlyCollection<SaveCandidateItem>? pendingCandidates = null)
    {
        var files = new List<SaveSnapshotFileItem>();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(path);
                var relativePath = Path.GetRelativePath(directory, path);
                var rule = state.SaveFileRules.LastOrDefault(item => item.GameId == game.Id && (string.IsNullOrWhiteSpace(item.StorageRelativePath) ? item.RelativePath : item.StorageRelativePath).Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                var pending = pendingCandidates?.LastOrDefault(item => (string.IsNullOrWhiteSpace(item.StorageRelativePath) ? item.RelativePath : item.StorageRelativePath).Equals(relativePath, StringComparison.OrdinalIgnoreCase));
                var sourceKind = rule?.SourceKind ?? pending?.SourceKind ?? "游戏目录";
                var originalPath = rule is not null ? Path.Combine(rule.SourceRootPath, rule.RelativePath) : pending is not null ? Path.Combine(pending.SourceRootPath, pending.RelativePath) : Path.Combine(game.PlayableRootPath ?? string.Empty, relativePath);
                files.Add(new SaveSnapshotFileItem { RelativePath = relativePath, SourceKind = sourceKind, OriginalPath = originalPath, FileSize = info.Length, Sha256 = await FileFingerprintService.ComputeSha256Async(path, token) });
            }
        }
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.RelativePath.Replace('\\', '/').ToLowerInvariant()));
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.OriginalPath.ToLowerInvariant()));
            aggregate.AppendData(BitConverter.GetBytes(file.FileSize));
            aggregate.AppendData(Convert.FromHexString(file.Sha256));
        }
        return new SaveSnapshotManifest
        {
            SnapshotId = snapshotId,
            GameId = game.Id,
            GameVersionId = game.CurrentVersionId ?? Guid.Empty,
            SnapshotKind = snapshotKind,
            ExitCode = game.LastExitCode,
            CreatedAt = DateTime.Now,
            ContentFingerprint = Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            Files = files
        };
    }

    private static async Task<bool> VerifySnapshotAsync(string snapshotDirectory, SaveSnapshotManifest manifest, CancellationToken token)
    {
        foreach (var file in manifest.Files)
        {
            var path = GetSafeChildPath(snapshotDirectory, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.FileSize) return false;
            if (!(await FileFingerprintService.ComputeSha256Async(path, token)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static void UpdateCleanupSuggestions(AppState state, Guid gameId, string snapshotKind)
    {
        var snapshots = state.SaveSnapshots.Where(item => item.GameId == gameId && item.SnapshotKind == snapshotKind).OrderByDescending(item => item.CreatedAt).ToList();
        for (var index = 0; index < snapshots.Count; index++) snapshots[index].CleanupSuggested = index >= 3;
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("存档相对路径超出允许目录范围。");
        return target;
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (Exception ex) { AppLogger.Error($"存档临时目录自动清理失败：{path}", ex); }
    }
}
