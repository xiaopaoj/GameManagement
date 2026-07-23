using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Diagnostics;
using GameManagement.Models;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace GameManagement.Services;

public static class CredentialService
{
    private static readonly Guid PasswordHistoryEntropy = new("7ad75725-f7a0-4fa3-b39a-c82c62fb9383");

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

    public static IReadOnlyList<string> GetPasswordHistory(AppState state)
    {
        var passwords = new List<string>();
        foreach (var item in state.PasswordHistory.OrderByDescending(item => item.LastUsedAt))
        {
            try
            {
                var password = Decrypt(item.EncryptedPassword, PasswordHistoryEntropy);
                if (!string.IsNullOrEmpty(password) && !passwords.Contains(password, StringComparer.Ordinal)) passwords.Add(password);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // 无法由当前 Windows 用户解密的历史记录不显示，也不影响解压流程。
            }
        }
        return passwords;
    }

    public static void AddPasswordHistory(AppState state, string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        var existing = state.PasswordHistory.FirstOrDefault(item =>
        {
            try { return Decrypt(item.EncryptedPassword, PasswordHistoryEntropy) == password; }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException) { return false; }
        });
        if (existing is not null)
        {
            existing.LastUsedAt = DateTime.Now;
            return;
        }
        state.PasswordHistory.Add(new PasswordHistoryItem
        {
            EncryptedPassword = Encrypt(password, PasswordHistoryEntropy),
            LastUsedAt = DateTime.Now
        });
    }
}

public static class ExtractionTemplateService
{
    public static void SetPasswords(ExtractionTemplateItem template, string firstPassword, string secondPassword)
    {
        template.EncryptedFirstPassword = CredentialService.Encrypt(firstPassword, template.Id);
        template.EncryptedSecondPassword = CredentialService.Encrypt(secondPassword, template.Id);
        template.UpdatedAt = DateTime.Now;
    }

    public static string? GetPassword(AppState state, GameItem game, int stepOrder)
    {
        if (game.ExtractionTemplateId is not Guid templateId) return null;
        var template = state.ExtractionTemplates.FirstOrDefault(item => item.Id == templateId);
        if (template is null) return null;
        var encrypted = stepOrder == 1 ? template.EncryptedFirstPassword : template.EncryptedSecondPassword;
        return string.IsNullOrEmpty(encrypted) ? string.Empty : CredentialService.Decrypt(encrypted, template.Id);
    }
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
    public sealed record EstimateResult(long SourceCopyBytes, long FirstExtractionBytes, long SecondExtractionAndFinalReserveBytes, long SafetyReserveBytes, long TotalBytes, bool ContentMetadataAvailable);

    public static long GetSourceSize(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            var group = ArchiveVolumeService.BuildGroup(sourcePath);
            return group.Files.Where(File.Exists).Sum(path => new FileInfo(path).Length);
        }
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

    public static async Task<EstimateResult> EstimateForSourceAsync(string sourcePath, CancellationToken token = default)
    {
        var sourceSize = await Task.Run(() => GetSourceSize(sourcePath), token);
        long firstExtraction = 0;
        var metadataAvailable = false;
        try
        {
            var groups = await Task.Run(() => ArchiveVolumeService.DiscoverGroups(sourcePath, token), token);
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var archive = ArchiveFactory.Open(group.EntryPath);
                    var expanded = archive.Entries.Where(entry => !entry.IsDirectory).Sum(entry => Math.Max(0L, entry.Size));
                    if (expanded > 0) { metadataAvailable = true; firstExtraction = Math.Max(firstExtraction, expanded); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException or OverflowException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException) { }

        if (!metadataAvailable)
        {
            var fallback = EstimateRequiredSpace(sourceSize);
            return new EstimateResult(sourceSize, 0, Math.Max(0, fallback - sourceSize), 0, fallback, false);
        }
        try
        {
            var secondAndFinal = checked(Math.Max(firstExtraction * 2, sourceSize * 2));
            var subtotal = checked(sourceSize + firstExtraction + secondAndFinal);
            var safety = checked(Math.Max(1024L * 1024 * 1024, subtotal / 10));
            return new EstimateResult(sourceSize, firstExtraction, secondAndFinal, safety, checked(subtotal + safety), true);
        }
        catch (OverflowException) { return new EstimateResult(sourceSize, firstExtraction, long.MaxValue, 0, long.MaxValue, true); }
    }

    public static long GetAvailableSpace(string rootPath)
    {
        var driveRoot = Path.GetPathRoot(rootPath) ?? throw new InvalidOperationException("无法确定游戏盘所属磁盘。");
        return new DriveInfo(driveRoot).AvailableFreeSpace;
    }
}

public static class ArchiveExtractionService
{
    public static bool RequiresNormalizedWorkingArchive(UiSettingsItem? settings) => !WinRarExtractionService.ShouldUse(settings, out _);

    public static async Task ExtractAsync(string archivePath, string outputDirectory, string password, CancellationToken token = default, UiSettingsItem? settings = null)
    {
        if (WinRarExtractionService.ShouldUse(settings, out var executablePath))
        {
            await WinRarExtractionService.ExtractAsync(executablePath, archivePath, outputDirectory, password, token);
            return;
        }
        await Task.Run(() =>
    {
        var readableArchivePath = PrepareReadableArchive(archivePath, token, out var temporaryArchivePath);
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var outputRoot = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var readableStream = temporaryArchivePath is null ? null : new FileStream(readableArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = readableStream is null
                ? ArchiveFactory.Open(readableArchivePath, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password })
                : ArchiveFactory.Open(readableStream, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password });
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                token.ThrowIfCancellationRequested();
                var entryKey = entry.Key ?? throw new InvalidDataException("压缩包中存在无效文件名。");
                var destination = Path.GetFullPath(Path.Combine(outputDirectory, entryKey));
                if (!destination.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"压缩包包含越界路径：{entryKey}");
                entry.WriteToDirectory(outputDirectory, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }
        }
        finally { DeleteTemporaryArchive(temporaryArchivePath); }
    }, token);
    }

    public static async Task ValidatePasswordAsync(string archivePath, string password, CancellationToken token = default, UiSettingsItem? settings = null)
    {
        if (WinRarExtractionService.ShouldUse(settings, out var executablePath))
        {
            await WinRarExtractionService.TestAsync(executablePath, archivePath, password, token);
            return;
        }
        await Task.Run(() =>
    {
        var readableArchivePath = PrepareReadableArchive(archivePath, token, out var temporaryArchivePath);
        try
        {
            token.ThrowIfCancellationRequested();
            using var readableStream = temporaryArchivePath is null ? null : new FileStream(readableArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = readableStream is null
                ? ArchiveFactory.Open(readableArchivePath, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password })
                : ArchiveFactory.Open(readableStream, new ReaderOptions { Password = string.IsNullOrEmpty(password) ? null : password });
            var entry = archive.Entries.FirstOrDefault(item => !item.IsDirectory);
            if (entry is null) return;
            using var stream = entry.OpenEntryStream();
            Span<byte> buffer = stackalloc byte[1];
            _ = stream.Read(buffer);
        }
        finally { DeleteTemporaryArchive(temporaryArchivePath); }
    }, token);
    }

    private static string PrepareReadableArchive(string archivePath, CancellationToken token, out string? temporaryArchivePath)
    {
        temporaryArchivePath = null;
        var group = ArchiveVolumeService.BuildGroup(archivePath);
        if (!group.VolumeKind.Equals("7z-parts", StringComparison.OrdinalIgnoreCase)) return archivePath;
        temporaryArchivePath = Path.Combine(Path.GetDirectoryName(archivePath)!, $".combined-{Guid.NewGuid():N}.7z");
        try
        {
            using var output = new FileStream(temporaryArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
            foreach (var volume in group.Files)
            {
                token.ThrowIfCancellationRequested();
                using var input = new FileStream(volume, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
                input.CopyTo(output, 1024 * 1024);
            }
            return temporaryArchivePath;
        }
        catch
        {
            DeleteTemporaryArchive(temporaryArchivePath);
            throw;
        }
    }

    private static void DeleteTemporaryArchive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) { AppLogger.Error($"清理 7z 分卷临时合并文件失败：{path}", ex); }
    }
}

public static class WinRarExtractionService
{
    public static bool ShouldUse(UiSettingsItem? settings, out string executablePath)
    {
        executablePath = string.Empty;
        var engine = settings?.ExtractionEngine ?? ExtractionEngineNames.BuiltIn;
        if (engine == ExtractionEngineNames.BuiltIn) return false;
        if (TryResolveExecutable(out executablePath)) return true;
        if (engine == ExtractionEngineNames.WinRar) throw new FileNotFoundException("设置要求使用 WinRAR，但未检测到 WinRAR.exe。请安装 WinRAR，或在设置中改为“自动选择/内置解压”。");
        return false;
    }

    public static bool TryResolveExecutable(out string path)
    {
        var candidates = new List<string?>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try { candidates.Add(hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe")?.GetValue(null) as string); }
            catch { }
        }
        candidates.AddRange(
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "WinRAR.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinRAR", "WinRAR.exe")
        ]);
        path = candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim().Trim('"'))
            .FirstOrDefault(File.Exists) ?? string.Empty;
        return path.Length > 0;
    }

    public static Task ExtractAsync(string executablePath, string archivePath, string outputDirectory, string password, CancellationToken token)
    {
        Directory.CreateDirectory(outputDirectory);
        return RunAsync(executablePath, "x", archivePath, outputDirectory, password, token);
    }

    public static Task TestAsync(string executablePath, string archivePath, string password, CancellationToken token) =>
        RunAsync(executablePath, "t", archivePath, null, password, token);

    private static async Task RunAsync(string executablePath, string command, string archivePath, string? outputDirectory, string password, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("-ibck");
        startInfo.ArgumentList.Add("-inul");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-o+");
        startInfo.ArgumentList.Add(string.IsNullOrEmpty(password) ? "-p-" : $"-p{password}");
        startInfo.ArgumentList.Add(archivePath);
        if (!string.IsNullOrWhiteSpace(outputDirectory)) startInfo.ArgumentList.Add(Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("无法启动 WinRAR 后台解压进程。");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        var output = string.Join(Environment.NewLine, new[] { await stdout, await stderr }.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        if (process.ExitCode != 0) throw new InvalidDataException(BuildFailureMessage(process.ExitCode, output));
    }

    private static string BuildFailureMessage(int exitCode, string output)
    {
        var reason = exitCode switch
        {
            1 => "WinRAR 报告警告",
            2 => "WinRAR 发生致命错误",
            3 => "压缩包 CRC 校验失败，可能密码错误或文件损坏",
            4 => "压缩包被锁定",
            5 => "写入目标目录失败",
            6 => "无法打开压缩文件",
            10 => "未找到匹配的压缩内容",
            11 => "密码错误",
            _ => $"WinRAR 返回错误代码 {exitCode}"
        };
        return string.IsNullOrWhiteSpace(output) ? reason : $"{reason}：{output}";
    }
}

public static class ExecutableDiscoveryService
{
    public static GameLaunchDiscoveryResult? Discover(string rootDirectory, CancellationToken token = default, IEnumerable<string>? ignoredFileNames = null)
    {
        if (!Directory.Exists(rootDirectory)) return null;
        var excludedNames = NormalizeIgnoreNames(ignoredFileNames);
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

            if (directExecutables.Any(path => !excludedNames.Contains(Path.GetFileName(path))))
            {
                var launchFiles = directExecutables
                    .Where(path => !excludedNames.Contains(Path.GetFileName(path)))
                    .Concat(EnumerateIndexFiles(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var candidates = ScoreCandidates(directory, launchFiles, excludedNames);
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

    public static IReadOnlyList<GameLaunchCandidate> ScoreCandidates(string gameRoot, IEnumerable<string> launchFiles, IEnumerable<string>? ignoredFileNames = null)
    {
        var excludedNames = NormalizeIgnoreNames(ignoredFileNames);
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
            var excluded = IsExecutable(path) && excludedNames.Contains(fileName);
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

    public static bool IsExcludedExecutable(string path, IEnumerable<string>? ignoredFileNames = null) => NormalizeIgnoreNames(ignoredFileNames).Contains(Path.GetFileName(path));

    private static HashSet<string> NormalizeIgnoreNames(IEnumerable<string>? ignoredFileNames) =>
        new((ignoredFileNames ?? new UiSettingsItem().ExecutableIgnoreNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileName(name.Trim())), StringComparer.OrdinalIgnoreCase);

    public static GameLaunchSelection? ResolveRecordedSelection(string searchRoot, string? executableRelativePath, string? preferredGameRoot = null, IEnumerable<string>? ignoredFileNames = null)
    {
        if (string.IsNullOrWhiteSpace(executableRelativePath) || Path.IsPathFullyQualified(executableRelativePath)) return null;
        var normalizedRelativePath = executableRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedRelativePath.Split(Path.DirectorySeparatorChar).Any(part => part is ".." or ".")) return null;

        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredGameRoot) && Directory.Exists(preferredGameRoot)) directories.Add(Path.GetFullPath(preferredGameRoot));
        if (Directory.Exists(searchRoot))
        {
            directories.Add(Path.GetFullPath(searchRoot));
            try { directories.AddRange(Directory.EnumerateDirectories(searchRoot, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        foreach (var gameRoot in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) continue;
            var valid = Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(candidate).Equals("index.html", StringComparison.OrdinalIgnoreCase);
            if (valid && !IsExcludedExecutable(candidate, ignoredFileNames)) return new GameLaunchSelection(root, candidate);
        }
        return null;
    }

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
