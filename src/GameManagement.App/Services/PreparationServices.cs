using System.Security.Cryptography;
using System.Text;
using System.IO;
using GameManagement.Models;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GameManagement.Services;

public static class CredentialService
{
    public static string Encrypt(string password, Guid versionId)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;
        var data = Encoding.UTF8.GetBytes(password);
        var entropy = versionId.ToByteArray();
        return Convert.ToBase64String(ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser));
    }

    public static string Decrypt(string encryptedPassword, Guid versionId)
    {
        if (string.IsNullOrEmpty(encryptedPassword)) return string.Empty;
        var data = Convert.FromBase64String(encryptedPassword);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, versionId.ToByteArray(), DataProtectionScope.CurrentUser));
    }

    public static string? FindPassword(AppState state, Guid versionId, string fingerprint)
    {
        var item = state.Credentials.FirstOrDefault(x => x.GameVersionId == versionId && x.ArchiveFingerprint == fingerprint);
        return item is null ? null : Decrypt(item.EncryptedPassword, versionId);
    }

    public static void SavePassword(AppState state, Guid versionId, string fingerprint, string password, int stepOrder = 0, string? archiveDisplayName = null, string? archiveRelativePath = null, DateTime? verifiedAt = null)
    {
        var item = state.Credentials.FirstOrDefault(x => x.GameVersionId == versionId && x.ArchiveFingerprint == fingerprint);
        if (item is null)
        {
            item = new ArchiveCredentialItem { GameVersionId = versionId, ArchiveFingerprint = fingerprint };
            state.Credentials.Add(item);
        }
        item.EncryptedPassword = Encrypt(password, versionId);
        if (stepOrder > 0) item.StepOrder = stepOrder;
        if (archiveDisplayName is not null) item.ArchiveDisplayName = archiveDisplayName;
        if (archiveRelativePath is not null) item.ArchiveRelativePath = archiveRelativePath;
        item.VerifiedAt = verifiedAt;
        item.UpdatedAt = DateTime.Now;
    }

    public static void DeletePassword(AppState state, Guid credentialId) => state.Credentials.RemoveAll(item => item.Id == credentialId);
}

public static class FileFingerprintService
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class SourceMetadataService
{
    public static async Task<SourceMetadataSnapshot> CaptureAsync(string sourcePath, IProgress<SourceMetadataProgress>? progress = null, CancellationToken token = default)
    {
        if (File.Exists(sourcePath))
        {
            var volumeGroup = ArchiveVolumeService.BuildGroup(sourcePath);
            if (volumeGroup.IsMultiVolume && volumeGroup.Files.Count > 1)
                return await CaptureFileSetAsync(Path.GetDirectoryName(sourcePath)!, volumeGroup.Files, progress, token);
            var info = new FileInfo(sourcePath);
            progress?.Report(new SourceMetadataProgress(0, 1, sourcePath));
            var fingerprint = await FileFingerprintService.ComputeSha256Async(sourcePath, token);
            progress?.Report(new SourceMetadataProgress(1, 1, sourcePath));
            return new SourceMetadataSnapshot(1, info.Length, info.LastWriteTime, fingerprint);
        }
        if (!Directory.Exists(sourcePath)) throw new FileNotFoundException("原始文件或目录不存在。", sourcePath);

        var files = Directory.EnumerateFiles(sourcePath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false })
            .OrderBy(path => Path.GetRelativePath(sourcePath, path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        return await CaptureFileSetAsync(sourcePath, files, progress, token);
    }

    private static async Task<SourceMetadataSnapshot> CaptureFileSetAsync(string rootPath, IReadOnlyList<string> files, IProgress<SourceMetadataProgress>? progress, CancellationToken token)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalSize = 0;
        DateTime? modifiedAt = Directory.Exists(rootPath) ? new DirectoryInfo(rootPath).LastWriteTime : null;
        for (var index = 0; index < files.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[index];
            progress?.Report(new SourceMetadataProgress(index, files.Count, file));
            var info = new FileInfo(file);
            totalSize += info.Length;
            if (modifiedAt is null || info.LastWriteTime > modifiedAt) modifiedAt = info.LastWriteTime;
            var relativePath = Path.GetRelativePath(rootPath, file).Replace(Path.DirectorySeparatorChar, '/');
            var fileHash = await FileFingerprintService.ComputeSha256Async(file, token);
            var manifestLine = $"{relativePath}\0{info.Length}\0{fileHash}\n";
            aggregate.AppendData(Encoding.UTF8.GetBytes(manifestLine));
            progress?.Report(new SourceMetadataProgress(index + 1, files.Count, file));
        }
        return new SourceMetadataSnapshot(files.Count, totalSize, modifiedAt, Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant());
    }

    public static void Apply(GameVersionItem version, SourceMetadataSnapshot metadata)
    {
        version.SourceFileCount = metadata.FileCount;
        version.SourceSize = metadata.TotalSize;
        version.SourceModifiedAt = metadata.ModifiedAt;
        version.SourceFingerprint = metadata.Fingerprint;
    }
}

public static class SourceCopyService
{
    public static async Task<string> CopyToWorkDirectoryAsync(string sourcePath, string destinationRoot, CancellationToken token = default)
    {
        Directory.CreateDirectory(destinationRoot);
        if (File.Exists(sourcePath))
        {
            var group = ArchiveVolumeService.BuildGroup(sourcePath);
            foreach (var file in group.Files)
            {
                token.ThrowIfCancellationRequested();
                await CopyFileAsync(file, Path.Combine(destinationRoot, Path.GetFileName(file)), token);
            }
            var entryTarget = Path.Combine(destinationRoot, Path.GetFileName(group.EntryPath));
            if (File.Exists(entryTarget)) return entryTarget;
            var selectedTarget = Path.Combine(destinationRoot, Path.GetFileName(sourcePath));
            if (!File.Exists(selectedTarget)) await CopyFileAsync(sourcePath, selectedTarget, token);
            return selectedTarget;
        }
        if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException($"原始文件或目录不存在：{sourcePath}");
        var targetDirectory = Path.Combine(destinationRoot, new DirectoryInfo(sourcePath).Name);
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourcePath, directory)));
        }
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(targetDirectory, Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAsync(file, target, token);
        }
        return targetDirectory;
    }

    public static async Task CopyDirectoryAsync(string sourceDirectory, string targetDirectory, CancellationToken token = default)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAsync(file, target, token);
        }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await input.CopyToAsync(output, 1024 * 1024, token);
    }
}

public static class SpaceEstimationService
{
    public static long GetSourceSize(string sourcePath)
    {
        if (File.Exists(sourcePath)) return new FileInfo(sourcePath).Length;
        if (!Directory.Exists(sourcePath)) return 0;
        return Directory.EnumerateFiles(sourcePath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
    }

    public static long EstimateRequiredSpace(long sourceSize)
    {
        if (sourceSize <= 0) return 5L * 1024 * 1024 * 1024;
        try { return checked(sourceSize * 4); }
        catch (OverflowException) { return long.MaxValue; }
    }

    public static long GetAvailableSpace(string rootPath)
    {
        var driveRoot = Path.GetPathRoot(rootPath) ?? throw new InvalidOperationException("无法确定游戏盘所属磁盘。");
        return new DriveInfo(driveRoot).AvailableFreeSpace;
    }
}

public static class ArchiveExtractionService
{
    public static Task ExtractAsync(string archivePath, string outputDirectory, string password, CancellationToken token = default) => Task.Run(() =>
    {
        Directory.CreateDirectory(outputDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password });
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            token.ThrowIfCancellationRequested();
            var entryKey = entry.Key ?? throw new InvalidDataException("压缩包中存在无效文件名。");
            var destination = Path.GetFullPath(Path.Combine(outputDirectory, entryKey));
            if (!destination.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"压缩包包含越界路径：{entryKey}");
            entry.WriteToDirectory(outputDirectory, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
        }
    }, token);

    public static Task ValidatePasswordAsync(string archivePath, string password, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password });
        var entry = archive.Entries.FirstOrDefault(item => !item.IsDirectory);
        if (entry is null) return;
        using var stream = entry.OpenEntryStream();
        Span<byte> buffer = stackalloc byte[1];
        _ = stream.Read(buffer);
    }, token);
}

public static class ExecutableDiscoveryService
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityCrashHandler.exe", "UnityCrashHandler64.exe", "CrashReportClient.exe", "UE4PrereqSetup_x64.exe",
        "UEPrereqSetup_x64.exe", "unins000.exe", "uninstall.exe", "setup.exe", "vc_redist.x64.exe", "DXSETUP.exe"
    };

    public static GameLaunchDiscoveryResult? Discover(string rootDirectory, CancellationToken token = default)
    {
        if (!Directory.Exists(rootDirectory)) return null;
        var pending = new Queue<string>();
        pending.Enqueue(rootDirectory);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            IReadOnlyList<string> directExecutables;
            try
            {
                directExecutables = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            if (directExecutables.Any(path => !ExcludedNames.Contains(Path.GetFileName(path))))
            {
                var launchFiles = directExecutables
                    .Concat(EnumerateIndexFiles(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var candidates = ScoreCandidates(directory, launchFiles);
                return new GameLaunchDiscoveryResult(directory, candidates);
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory).OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)) pending.Enqueue(child);
            }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IReadOnlyList<GameLaunchCandidate> ScoreCandidates(string gameRoot, IEnumerable<string> launchFiles)
    {
        var directoryName = NormalizeName(new DirectoryInfo(gameRoot).Name);
        var files = launchFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var largestExeSize = files.Where(IsExecutable).Select(GetFileSize).DefaultIfEmpty(0).Max();
        var candidates = new List<GameLaunchCandidate>();
        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var baseName = NormalizeName(Path.GetFileNameWithoutExtension(path));
            var reasons = new List<string>();
            var score = 0;
            var excluded = IsExecutable(path) && ExcludedNames.Contains(fileName);
            if (IsExecutable(path)) { score += 100; reasons.Add("EXE 启动文件"); }
            else { score += 10; reasons.Add("网页入口"); }
            if (!string.IsNullOrWhiteSpace(directoryName) && baseName.Equals(directoryName, StringComparison.OrdinalIgnoreCase)) { score += 80; reasons.Add("名称与游戏目录一致"); }
            if (IsExecutable(path) && GetFileSize(path) == largestExeSize && largestExeSize > 0) { score += 25; reasons.Add("目录内体积最大的 EXE"); }
            if (baseName.Contains("game", StringComparison.OrdinalIgnoreCase) || baseName.Contains("launch", StringComparison.OrdinalIgnoreCase) || baseName.Contains("start", StringComparison.OrdinalIgnoreCase)) { score += 20; reasons.Add("文件名包含常见启动关键词"); }
            if (TryGetVersionDescription(path, out var description) && !string.IsNullOrWhiteSpace(description))
            {
                var normalizedDescription = NormalizeName(description);
                if (!string.IsNullOrWhiteSpace(directoryName) && normalizedDescription.Contains(directoryName, StringComparison.OrdinalIgnoreCase)) { score += 30; reasons.Add("版本信息与目录名称相关"); }
            }
            if (excluded) { score -= 300; reasons.Add("属于辅助程序排除名单"); }
            candidates.Add(new GameLaunchCandidate(path, score, reasons, excluded));
        }
        return candidates.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => Path.GetExtension(candidate.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => Path.GetFileName(candidate.Path), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsExcludedExecutable(string path) => ExcludedNames.Contains(Path.GetFileName(path));

    private static IEnumerable<string> EnumerateIndexFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Equals("index.html", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    private static bool IsExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    private static long GetFileSize(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    private static string NormalizeName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool TryGetVersionDescription(string path, out string description)
    {
        description = string.Empty;
        if (!IsExecutable(path)) return false;
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            description = info.ProductName ?? info.FileDescription ?? string.Empty;
            return true;
        }
        catch { return false; }
    }
}

public sealed record GameLaunchCandidate(string Path, int Score, IReadOnlyList<string> Reasons, bool IsExcluded);

public sealed record GameLaunchDiscoveryResult(string GameRoot, IReadOnlyList<GameLaunchCandidate> Candidates)
{
    public IReadOnlyList<string> LaunchFiles => Candidates.Select(candidate => candidate.Path).ToList();
}

public sealed record GameLaunchSelection(string GameRoot, string LaunchFile);

public static class BaselineService
{
    public static Task<List<FileBaselineItem>> BuildAsync(Guid versionId, string rootDirectory, CancellationToken token = default) => BuildAsync(Guid.Empty, versionId, rootDirectory, token);

    public static async Task<List<FileBaselineItem>> BuildAsync(Guid gameId, Guid versionId, string rootDirectory, CancellationToken token = default)
    {
        var result = new List<FileBaselineItem>();
        foreach (var file in Directory.EnumerateFiles(rootDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            result.Add(new FileBaselineItem
            {
                GameId = gameId,
                GameVersionId = versionId,
                RelativePath = Path.GetRelativePath(rootDirectory, file),
                FileSize = info.Length,
                ModifiedAt = info.LastWriteTime,
                Sha256 = await FileFingerprintService.ComputeSha256Async(file, token),
                FileCategory = "游戏文件"
            });
        }
        return result;
    }
}
