using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GameManagement.Models;

namespace GameManagement.Services;

public static class AppPaths
{
    public static string Root => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string Data => Path.Combine(Root, "data");
    public static string Logs => Path.Combine(Root, "logs");
    public static string StateFile => Path.Combine(Data, "game-management.secure");
    public static string LegacyStateFile => Path.Combine(Data, "game-management.json");
    public static string SecurityConfigFile => Path.Combine(Data, "security.json");
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Data); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Path.Combine(Data, "cache"));
        var probe = Path.Combine(Data, $".write-test-{Guid.NewGuid():N}"); File.WriteAllText(probe, "ok"); File.Delete(probe);
    }
}

public static class AppLogger
{
    private static readonly object Sync = new(); private static string _file = string.Empty; private static readonly List<string> Pending = [];
    public static void Initialize() { _file = Path.Combine(AppPaths.Logs, $"application-{DateTime.Now:yyyyMMdd}.securelog"); Info("软件启动"); }
    public static void FlushPending() { lock (Sync) { foreach (var line in Pending.ToList()) WriteEncrypted(line); Pending.Clear(); } }
    public static void Info(string message) => Write("信息", message);
    public static void Error(string message, Exception ex) => Write("错误", $"{message}：{ex}");
    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(_file)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            try { WriteEncrypted(line); } catch (UnauthorizedAccessException) { Pending.Add(line); }
        }
    }
    private static void WriteEncrypted(string line)
    {
        var key = MasterKeyService.GetOrCreate(AppPaths.SecurityConfigFile);
        var existing = File.Exists(_file) ? EncryptedDataFile.Read(_file, key) : [];
        var addition = Encoding.UTF8.GetBytes(line); var combined = new byte[existing.Length + addition.Length];
        Buffer.BlockCopy(existing, 0, combined, 0, existing.Length); Buffer.BlockCopy(addition, 0, combined, existing.Length, addition.Length);
        EncryptedDataFile.WriteAtomic(_file, combined, key); CryptographicOperations.ZeroMemory(existing); CryptographicOperations.ZeroMemory(combined);
    }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object SaveSync = new();
    private readonly string _stateFile;
    private readonly string _legacyStateFile;
    private readonly string _securityConfigFile;

    public StateStore(string? stateFile = null, string? legacyStateFile = null, string? securityConfigFile = null)
    {
        _stateFile = stateFile ?? AppPaths.StateFile;
        _legacyStateFile = legacyStateFile ?? AppPaths.LegacyStateFile;
        _securityConfigFile = securityConfigFile ?? AppPaths.SecurityConfigFile;
    }

    public AppState Load()
    {
        if (!File.Exists(_stateFile) && !File.Exists(_legacyStateFile)) return new AppState();
        try
        {
            string json;
            if (File.Exists(_stateFile)) json = Encoding.UTF8.GetString(EncryptedDataFile.Read(_stateFile, MasterKeyService.GetOrCreate(_securityConfigFile)));
            else
            {
                json = File.ReadAllText(_legacyStateFile, Encoding.UTF8);
                var migrated = JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();
                Save(migrated);
                _ = Encoding.UTF8.GetString(EncryptedDataFile.Read(_stateFile, MasterKeyService.GetOrCreate(_securityConfigFile)));
            }
            var state = JsonSerializer.Deserialize<AppState>(json, Options) ?? new AppState();
            state.UiSettings ??= new UiSettingsItem();
            if (state.UiSettings.BossKeyModifiers <= 0) state.UiSettings.BossKeyModifiers = 3;
            if (state.UiSettings.BossKeyVirtualKey <= 0) state.UiSettings.BossKeyVirtualKey = 0x42;
            if (state.UiSettings.ExtractionEngine is not (ExtractionEngineNames.Auto or ExtractionEngineNames.WinRar or ExtractionEngineNames.BuiltIn))
                state.UiSettings.ExtractionEngine = ExtractionEngineNames.Auto;
            state.UiSettings.ExecutableIgnoreNames ??= new UiSettingsItem().ExecutableIgnoreNames;
            state.UiSettings.ExecutableIgnoreNames = state.UiSettings.ExecutableIgnoreNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Path.GetFileName(name.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var game in state.Games)
            {
                if (game.AddedAt == default) game.AddedAt = ResolveAddedAt(game);
                if (string.IsNullOrWhiteSpace(game.SourceKind) || game.SourceKind == SourceKinds.Unknown) game.SourceKind = SourceKinds.Detect(game.SourcePath);
                foreach (var version in game.Versions)
                {
                    if (string.IsNullOrWhiteSpace(version.SourceKind) || version.SourceKind == SourceKinds.Unknown) version.SourceKind = SourceKinds.Detect(version.SourcePath);
                    if (version.Id == game.CurrentVersionId && string.IsNullOrWhiteSpace(version.ExecutableRelativePath) && !string.IsNullOrWhiteSpace(game.ExecutableRelativePath))
                        version.ExecutableRelativePath = game.ExecutableRelativePath;
                }
                var currentVersion = game.Versions.FirstOrDefault(version => version.Id == game.CurrentVersionId);
                var cachedIconPath = Path.Combine(AppPaths.Data, "cache", "icons", $"{game.Id:N}.png");
                var encryptedCachedIconPath = Path.Combine(AppPaths.Data, "cache", "icons", $"{game.Id:N}.icon.secure");
                if (string.IsNullOrWhiteSpace(game.IconRelativePath))
                {
                    game.IconRelativePath = !string.IsNullOrWhiteSpace(currentVersion?.IconRelativePath)
                        ? currentVersion.IconRelativePath
                        : File.Exists(encryptedCachedIconPath) ? Path.GetRelativePath(AppPaths.Root, encryptedCachedIconPath)
                        : File.Exists(cachedIconPath) ? Path.GetRelativePath(AppPaths.Root, cachedIconPath) : null;
                }
                game.IconRelativePath = EncryptedIconService.EnsureEncrypted(game.IconRelativePath, game.Id);
                foreach (var iconVersion in game.Versions)
                    if (!string.IsNullOrWhiteSpace(iconVersion.IconRelativePath)) iconVersion.IconRelativePath = EncryptedIconService.EnsureEncrypted(iconVersion.IconRelativePath, game.Id);
                if (currentVersion is not null && string.IsNullOrWhiteSpace(currentVersion.IconRelativePath) && !string.IsNullOrWhiteSpace(game.IconRelativePath))
                    currentVersion.IconRelativePath = game.IconRelativePath;
                foreach (var rule in state.SaveFileRules.Where(item => item.GameId == game.Id && item.SourceKind == "游戏目录"))
                {
                    if (string.IsNullOrWhiteSpace(rule.SourceRootPath)) rule.SourceRootPath = game.PlayableRootPath ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(rule.StorageRelativePath)) rule.StorageRelativePath = rule.RelativePath;
                }
                foreach (var exclusion in state.SaveFileExclusions.Where(item => item.GameId == game.Id && item.SourceKind == "游戏目录" && string.IsNullOrWhiteSpace(item.SourceRootPath))) exclusion.SourceRootPath = game.PlayableRootPath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(game.PlayableRootPath) && Directory.Exists(game.PlayableRootPath) && game.DirectoryCleanupStatus == "目录不存在") game.DirectoryCleanupStatus = "目录存在";
            }
            OperationTaskRecoveryService.MarkInterrupted(state);
            return state;
        }
        catch (Exception ex) { AppLogger.Error("读取加密数据文件失败", ex); throw new InvalidOperationException("读取软件加密数据失败，请检查 data 目录中的安全配置与数据库文件。", ex); }
    }
    public void Save(AppState state)
    {
        lock (SaveSync)
        {
            var key = MasterKeyService.GetOrCreate(_securityConfigFile);
            EncryptedDataFile.WriteAtomic(_stateFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, Options)), key);
        }
    }

    public static DateTime ResolveAddedAt(GameItem game) => game.Versions.Count > 0
        ? game.Versions.Min(version => version.CreatedAt)
        : DateTime.MinValue;
}

public static class OperationTaskRecoveryService
{
    public static int MarkInterrupted(AppState state, DateTime? recoveredAt = null)
    {
        var count = 0;
        foreach (var task in state.OperationTasks.Where(task => task.Status == "运行中"))
        {
            task.Status = "已中断";
            task.Message = "软件上次退出时任务尚未完成，可查看保留的临时目录后重新执行。";
            task.CompletedAt = recoveredAt ?? DateTime.Now;
            count++;
        }
        return count;
    }
}

public static class TemporaryDirectoryService
{
    public static bool IsManagedTaskDirectory(string path, IEnumerable<GameDiskItem> disks)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var disk in disks)
        {
            foreach (var folder in new[] { "GameTemp", "GameSaveTemp" })
            {
                var root = Path.GetFullPath(Path.Combine(disk.RootPath, folder)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (target.Length > root.Length && target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var gamesRoot = Path.GetFullPath(Path.Combine(disk.RootPath, "Games")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(gamesRoot, target.TrimEnd(Path.DirectorySeparatorChar));
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Equals(".prepare", StringComparison.OrdinalIgnoreCase) && !parts.Any(part => part == "..")) return true;
        }
        return false;
    }
}

public sealed class FileScanner
{
    public Task<List<ScanCandidate>> ScanAsync(IEnumerable<ScanPathItem> paths, IReadOnlyCollection<GameItem> games, IProgress<ScanProgressInfo>? progress, CancellationToken token) => Task.Run(() =>
    {
        var existing = games.Select(g => Normalize(g.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<ScanCandidate>();
        var enabledPaths = paths.Where(p => p.Enabled && Directory.Exists(p.Path)).ToList();
        var entries = new List<(ScanPathItem Owner, string Path, bool IsDirectory)>();
        foreach (var item in enabledPaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                entries.AddRange(Directory.EnumerateDirectories(item.Path).Select(path => (item, path, true)));
                entries.AddRange(Directory.EnumerateFiles(item.Path)
                    .Where(HasSupportedArchiveExtension)
                    .Select(path => (item, path, false)));
            }
            catch (UnauthorizedAccessException ex) { AppLogger.Error($"无权扫描目录 {item.Path}", ex); }
            catch (IOException ex) { AppLogger.Error($"扫描目录失败 {item.Path}", ex); }
        }

        progress?.Report(new ScanProgressInfo { Completed = 0, Total = entries.Count, CurrentPath = enabledPaths.FirstOrDefault()?.Path ?? "没有可扫描的路径" });
        var completed = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                var info = new DirectoryInfo(entry.Path);
                result.Add(new ScanCandidate { Name = info.Name, FullPath = info.FullName, Kind = SourceKinds.ArchiveDirectory, ArchiveCount = -1, ModifiedAt = info.LastWriteTime, DriveName = Path.GetPathRoot(info.FullName) ?? string.Empty, Added = existing.Contains(Normalize(entry.Path)) });
            }
            else
            {
                var info = new FileInfo(entry.Path);
                result.Add(new ScanCandidate { Name = info.Name, FullPath = info.FullName, Kind = SourceKinds.ArchiveFile, ArchiveCount = 1, Size = info.Length, ModifiedAt = info.LastWriteTime, DriveName = Path.GetPathRoot(info.FullName) ?? string.Empty, Added = existing.Contains(Normalize(entry.Path)) });
            }
            completed++;
            progress?.Report(new ScanProgressInfo { Completed = completed, Total = entries.Count, CurrentPath = entry.Path });
        }
        progress?.Report(new ScanProgressInfo { Completed = completed, Total = entries.Count, CurrentPath = "扫描完成", IsCompleted = true });
        foreach (var item in enabledPaths) item.LastScannedAt = DateTime.Now;
        return result.OrderByDescending(candidate => candidate.ModifiedAt).ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }, token);
    private static bool HasSupportedArchiveExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".7z", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(Path.GetFileName(path), @"^.+\.7z\.\d+$", RegexOptions.IgnoreCase);
    }
    private static string Normalize(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public static class ArchiveDiscoveryService
{
    private static readonly EnumerationOptions RecursiveOptions = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, ReturnSpecialDirectories = false };

    public static IReadOnlyList<string> Discover(string sourcePath, CancellationToken token = default)
    {
        if (File.Exists(sourcePath)) return IsSupportedArchive(sourcePath) ? [Path.GetFullPath(sourcePath)] : [];
        if (!Directory.Exists(sourcePath)) return [];

        var archives = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", RecursiveOptions))
        {
            token.ThrowIfCancellationRequested();
            if (IsSupportedArchive(file)) archives.Add(Path.GetFullPath(file));
        }
        return archives.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsSupportedArchive(string path)
    {
        return DetectFormat(path) is not null;
    }

    public static string? DetectFormat(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return "ZIP";
        if (extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)) return "RAR";
        if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)) return "7Z";
        if (Regex.IsMatch(Path.GetFileName(path), @"^.+\.7z\.\d+$", RegexOptions.IgnoreCase)) return "7Z";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var headLength = (int)Math.Min(stream.Length, 4L * 1024 * 1024);
            var head = new byte[headLength];
            stream.ReadExactly(head);
            if (ContainsZipSignature(head)) return "ZIP";
            if (ContainsRarSignature(head)) return "RAR";
            if (ContainsSevenZipSignature(head)) return "7Z";

            if (stream.Length > headLength)
            {
                var tailLength = (int)Math.Min(stream.Length, 1024L * 1024);
                var tail = new byte[tailLength];
                stream.Position = stream.Length - tailLength;
                stream.ReadExactly(tail);
                if (ContainsZipSignature(tail)) return "ZIP";
                if (ContainsRarSignature(tail)) return "RAR";
                if (ContainsSevenZipSignature(tail)) return "7Z";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public static string EnsureCorrectExtension(string path, string format)
    {
        var expectedExtension = GetExtensionForFormat(format);
        if (Path.GetExtension(path).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase)) return path;
        var target = Path.ChangeExtension(path, expectedExtension);
        if (File.Exists(target)) target = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}{expectedExtension}");
        File.Move(path, target);
        return target;
    }

    public static async Task<string> CreateNormalizedWorkingArchiveAsync(string path, string format, CancellationToken token = default)
    {
        var expectedExtension = GetExtensionForFormat(format);
        var signature = format.ToUpperInvariant() switch
        {
            "ZIP" => new byte[] { 0x50, 0x4B, 0x03, 0x04 },
            "7Z" => new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C },
            _ => new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 }
        };
        var offset = await FindSignatureOffsetAsync(path, signature, token);
        if (offset <= 0) return EnsureCorrectExtension(path, format);

        var directory = Path.GetDirectoryName(path)!;
        var target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(path)}-normalized{expectedExtension}");
        if (File.Exists(target)) File.Delete(target);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        input.Position = offset;
        await input.CopyToAsync(output, 1024 * 1024, token);
        return target;
    }

    public static async Task<long> FindSignatureOffsetAsync(string path, byte[] signature, CancellationToken token = default)
    {
        const int bufferSize = 8 * 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, true);
        var buffer = new byte[bufferSize + signature.Length - 1];
        var carry = 0;
        long consumed = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(carry, bufferSize), token);
            if (read == 0) return -1;
            var length = carry + read;
            var index = buffer.AsSpan(0, length).IndexOf(signature);
            if (index >= 0) return consumed - carry + index;
            carry = Math.Min(signature.Length - 1, length);
            buffer.AsSpan(length - carry, carry).CopyTo(buffer);
            consumed += read;
        }
    }

    public static string? FindLargestUnrecognizedFile(string directory, CancellationToken token = default)
    {
        if (!Directory.Exists(directory)) return null;
        FileInfo? largest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            token.ThrowIfCancellationRequested();
            if (DetectFormat(path) is not null) continue;
            try
            {
                var info = new FileInfo(path);
                if (largest is null || info.Length > largest.Length) largest = info;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return largest?.FullName;
    }

    private static bool ContainsZipSignature(ReadOnlySpan<byte> data)
    {
        return data.IndexOf(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) >= 0 ||
               data.IndexOf(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) >= 0 ||
               data.IndexOf(new byte[] { 0x50, 0x4B, 0x07, 0x08 }) >= 0;
    }

    private static bool ContainsRarSignature(ReadOnlySpan<byte> data)
    {
        return data.IndexOf(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }) >= 0 ||
               data.IndexOf(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }) >= 0;
    }

    private static bool ContainsSevenZipSignature(ReadOnlySpan<byte> data) =>
        data.IndexOf(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }) >= 0;

    private static string GetExtensionForFormat(string format) => format.ToUpperInvariant() switch
    {
        "ZIP" => ".zip",
        "7Z" => ".7z",
        _ => ".rar"
    };
}

public static partial class ArchiveVolumeService
{
    public static IReadOnlyList<ArchiveVolumeGroup> DiscoverGroups(string sourcePath, CancellationToken token = default)
    {
        var archives = ArchiveDiscoveryService.Discover(sourcePath, token);
        var groups = new Dictionary<string, ArchiveVolumeGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            token.ThrowIfCancellationRequested();
            var group = BuildGroup(archive);
            groups.TryAdd(group.GroupKey, group);
        }
        return groups.Values.OrderBy(group => Path.GetFileName(group.EntryPath), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static ArchiveVolumeGroup BuildGroup(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var fileName = Path.GetFileName(fullPath);

        var partMatch = PartRarRegex().Match(fileName);
        if (partMatch.Success) return BuildNumberedGroup(directory, partMatch.Groups["base"].Value, "part-rar", ".part", ".rar", 1, PartRarRegex(), "RAR");

        var zipPartMatch = ZipPartRegex().Match(fileName);
        if (zipPartMatch.Success) return BuildNumberedGroup(directory, zipPartMatch.Groups["base"].Value, "zip-parts", ".zip.", string.Empty, 1, ZipPartRegex(), "ZIP");

        var sevenZipPartMatch = SevenZipPartRegex().Match(fileName);
        if (sevenZipPartMatch.Success) return BuildNumberedGroup(directory, sevenZipPartMatch.Groups["base"].Value, "7z-parts", ".7z.", string.Empty, 1, SevenZipPartRegex(), "7Z");

        var oldRarMatch = OldRarRegex().Match(fileName);
        if (oldRarMatch.Success) return BuildOldRarGroup(directory, oldRarMatch.Groups["base"].Value);

        var oldZipMatch = OldZipRegex().Match(fileName);
        if (oldZipMatch.Success) return BuildOldZipGroup(directory, oldZipMatch.Groups["base"].Value);

        if (Path.GetExtension(fileName).Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (Directory.EnumerateFiles(directory, $"{baseName}.r*").Any(path => OldRarRegex().IsMatch(Path.GetFileName(path)))) return BuildOldRarGroup(directory, baseName);
        }
        if (Path.GetExtension(fileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (Directory.EnumerateFiles(directory, fileName + ".*").Any(path => ZipPartRegex().IsMatch(Path.GetFileName(path)))) return BuildNumberedGroup(directory, baseName, "zip-parts", ".zip.", string.Empty, 1, ZipPartRegex(), "ZIP");
            if (Directory.EnumerateFiles(directory, $"{baseName}.z*").Any(path => OldZipRegex().IsMatch(Path.GetFileName(path)))) return BuildOldZipGroup(directory, baseName);
        }

        return new ArchiveVolumeGroup
        {
            GroupKey = fullPath,
            VolumeKind = "single",
            EntryPath = fullPath,
            Format = ArchiveDiscoveryService.DetectFormat(fullPath) ?? Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(),
            Files = [fullPath]
        };
    }

    private static ArchiveVolumeGroup BuildNumberedGroup(string directory, string baseName, string kind, string middle, string suffix, int expectedStart, Regex regex, string format)
    {
        var matches = Directory.EnumerateFiles(directory)
            .Select(path => (Path: path, Match: regex.Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success && item.Match.Groups["base"].Value.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Path, Number: int.Parse(item.Match.Groups["number"].Value), Width: item.Match.Groups["number"].Value.Length))
            .OrderBy(item => item.Number).ToList();
        var max = matches.Count == 0 ? expectedStart : matches.Max(item => item.Number);
        var width = matches.Count == 0 ? 2 : matches.Max(item => item.Width);
        var existing = matches.Select(item => item.Number).ToHashSet();
        var expectedMax = max;
        if (kind.Equals("7z-parts", StringComparison.OrdinalIgnoreCase))
        {
            var firstPart = matches.FirstOrDefault(item => item.Number == expectedStart).Path;
            if (firstPart is not null && TryReadSevenZipExpectedLength(firstPart, out var expectedLength))
            {
                var firstPartLength = new FileInfo(firstPart).Length;
                if (firstPartLength > 0 && expectedLength > firstPartLength)
                {
                    var expectedPartCount = checked((int)Math.Min(int.MaxValue, (expectedLength + firstPartLength - 1) / firstPartLength));
                    expectedMax = Math.Max(expectedMax, expectedStart + expectedPartCount - 1);
                }
            }
        }
        var missing = Enumerable.Range(expectedStart, expectedMax - expectedStart + 1).Where(number => !existing.Contains(number))
            .Select(number => Path.Combine(directory, $"{baseName}{middle}{number.ToString($"D{width}")}{suffix}")).ToList();
        var entry = matches.FirstOrDefault(item => item.Number == expectedStart).Path ?? matches.FirstOrDefault().Path ?? Path.Combine(directory, $"{baseName}{middle}{expectedStart.ToString($"D{width}")}{suffix}");
        return new ArchiveVolumeGroup { GroupKey = Path.Combine(directory, $"{kind}:{baseName}"), VolumeKind = kind, EntryPath = entry, Format = format, Files = matches.Select(item => Path.GetFullPath(item.Path)).ToList(), MissingFiles = missing };
    }

    private static bool TryReadSevenZipExpectedLength(string firstPartPath, out long expectedLength)
    {
        expectedLength = 0;
        try
        {
            Span<byte> header = stackalloc byte[32];
            using var stream = new FileStream(firstPartPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Read(header) != header.Length || !header[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return false;
            var nextHeaderOffset = BitConverter.ToUInt64(header[12..20]);
            var nextHeaderSize = BitConverter.ToUInt64(header[20..28]);
            var total = checked(32UL + nextHeaderOffset + nextHeaderSize);
            if (total > long.MaxValue) return false;
            expectedLength = (long)total;
            return expectedLength >= 32;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { return false; }
    }

    private static ArchiveVolumeGroup BuildOldRarGroup(string directory, string baseName)
    {
        var first = Path.Combine(directory, baseName + ".rar");
        var numbered = Directory.EnumerateFiles(directory, $"{baseName}.r*")
            .Select(path => (Path: path, Match: OldRarRegex().Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success && item.Match.Groups["base"].Value.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Path, Number: int.Parse(item.Match.Groups["number"].Value), Width: item.Match.Groups["number"].Value.Length)).OrderBy(item => item.Number).ToList();
        var files = new List<string>(); if (File.Exists(first)) files.Add(first); files.AddRange(numbered.Select(item => item.Path));
        var missing = new List<string>(); if (!File.Exists(first)) missing.Add(first);
        if (numbered.Count > 0)
        {
            var max = numbered.Max(item => item.Number); var width = numbered.Max(item => item.Width); var existing = numbered.Select(item => item.Number).ToHashSet();
            missing.AddRange(Enumerable.Range(0, max + 1).Where(number => !existing.Contains(number)).Select(number => Path.Combine(directory, $"{baseName}.r{number.ToString($"D{width}")}")));
        }
        return new ArchiveVolumeGroup { GroupKey = Path.Combine(directory, $"old-rar:{baseName}"), VolumeKind = "old-rar", EntryPath = first, Format = "RAR", Files = files.Select(Path.GetFullPath).ToList(), MissingFiles = missing };
    }

    private static ArchiveVolumeGroup BuildOldZipGroup(string directory, string baseName)
    {
        var finalZip = Path.Combine(directory, baseName + ".zip");
        var numbered = Directory.EnumerateFiles(directory, $"{baseName}.z*")
            .Select(path => (Path: path, Match: OldZipRegex().Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success && item.Match.Groups["base"].Value.Equals(baseName, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Path, Number: int.Parse(item.Match.Groups["number"].Value), Width: item.Match.Groups["number"].Value.Length)).OrderBy(item => item.Number).ToList();
        var files = numbered.Select(item => item.Path).ToList(); if (File.Exists(finalZip)) files.Add(finalZip);
        var missing = new List<string>(); if (!File.Exists(finalZip)) missing.Add(finalZip);
        if (numbered.Count > 0)
        {
            var max = numbered.Max(item => item.Number); var width = numbered.Max(item => item.Width); var existing = numbered.Select(item => item.Number).ToHashSet();
            missing.AddRange(Enumerable.Range(1, max).Where(number => !existing.Contains(number)).Select(number => Path.Combine(directory, $"{baseName}.z{number.ToString($"D{width}")}")));
        }
        return new ArchiveVolumeGroup { GroupKey = Path.Combine(directory, $"old-zip:{baseName}"), VolumeKind = "old-zip", EntryPath = finalZip, Format = "ZIP", Files = files.Select(Path.GetFullPath).ToList(), MissingFiles = missing };
    }

    [GeneratedRegex(@"^(?<base>.+)\.part(?<number>\d+)\.rar$", RegexOptions.IgnoreCase)] private static partial Regex PartRarRegex();
    [GeneratedRegex(@"^(?<base>.+)\.zip\.(?<number>\d+)$", RegexOptions.IgnoreCase)] private static partial Regex ZipPartRegex();
    [GeneratedRegex(@"^(?<base>.+)\.7z\.(?<number>\d+)$", RegexOptions.IgnoreCase)] private static partial Regex SevenZipPartRegex();
    [GeneratedRegex(@"^(?<base>.+)\.r(?<number>\d{2,3})$", RegexOptions.IgnoreCase)] private static partial Regex OldRarRegex();
    [GeneratedRegex(@"^(?<base>.+)\.z(?<number>\d{2,3})$", RegexOptions.IgnoreCase)] private static partial Regex OldZipRegex();
}

public static class ShellService
{
    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    public static Process LaunchGame(GameItem game)
    {
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || string.IsNullOrWhiteSpace(game.ExecutableRelativePath)) throw new InvalidOperationException("该游戏尚未配置可游玩目录或启动文件。");
        var executable = Path.Combine(game.PlayableRootPath, game.ExecutableRelativePath); if (!File.Exists(executable)) throw new FileNotFoundException("游戏启动文件不存在。", executable);
        return Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = true })
            ?? throw new InvalidOperationException("系统没有返回可监听的启动进程，游戏可能未成功启动。");
    }
}

public static class GameRuntimeStateService
{
    public static void MarkStarted(GameItem game, int processId, DateTime startedAt)
    {
        game.Status = "运行中";
        if (game.ArchiveStatus == "已归档")
        {
            game.ArchiveStatus = "未归档";
            game.DirectoryCleanupStatus = "目录存在";
            game.ArchiveMessage = "已归档目录重新启动，重新进入游玩和存档监控状态。";
        }
        game.RunningProcessId = processId;
        game.CurrentPlayStartedAt = startedAt;
        game.LastPlayedAt = startedAt;
        game.LastExitCode = null;
    }

    public static void MarkExited(GameItem game, int? exitCode, DateTime exitedAt)
    {
        var startedAt = game.CurrentPlayStartedAt;
        game.Status = string.IsNullOrWhiteSpace(game.PlayableRootPath) || !Directory.Exists(game.PlayableRootPath) ? "未准备" : "可游玩";
        game.RunningProcessId = null;
        game.CurrentPlayStartedAt = null;
        game.LastExitedAt = exitedAt;
        game.LastExitCode = exitCode;
        game.LastRunDurationSeconds = startedAt.HasValue ? Math.Max(0, (long)(exitedAt - startedAt.Value).TotalSeconds) : null;
    }
}

public static class GameProcessMonitorService
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Guid, Process> RunningProcesses = [];

    public static void Launch(GameItem game, Action<GameItem, string> stateChanged)
    {
        lock (Sync)
        {
            if (IsTrackedProcessRunning(game.Id)) throw new InvalidOperationException("该游戏的主程序已经在运行。");
        }

        var process = ShellService.LaunchGame(game);
        var startedAt = DateTime.Now;
        GameRuntimeStateService.MarkStarted(game, process.Id, startedAt);
        stateChanged(game, $"游戏已启动，正在监听主程序进程 {process.Id}。");
        Track(game, process, stateChanged);
    }

    public static void Restore(IEnumerable<GameItem> games, Action<GameItem, string> stateChanged)
    {
        foreach (var game in games.Where(game => game.RunningProcessId.HasValue || game.Status == "运行中"))
        {
            if (game.RunningProcessId is not int processId)
            {
                GameRuntimeStateService.MarkExited(game, null, DateTime.Now);
                stateChanged(game, $"主游戏程序已退出；已修正游戏“{game.DisplayName}”的过期运行状态。");
                continue;
            }

            try
            {
                var process = Process.GetProcessById(processId);
                if (process.HasExited || !MatchesRecordedProcess(game, process))
                {
                    process.Dispose();
                    GameRuntimeStateService.MarkExited(game, null, DateTime.Now);
                    stateChanged(game, $"主游戏程序已退出；检测到游戏“{game.DisplayName}”的主程序已经结束。");
                    continue;
                }
                Track(game, process, stateChanged);
            }
            catch (ArgumentException)
            {
                GameRuntimeStateService.MarkExited(game, null, DateTime.Now);
                stateChanged(game, $"主游戏程序已退出；检测到游戏“{game.DisplayName}”的主程序已经结束。");
            }
            catch (InvalidOperationException)
            {
                GameRuntimeStateService.MarkExited(game, null, DateTime.Now);
                stateChanged(game, $"主游戏程序已退出；无法恢复游戏“{game.DisplayName}”的进程监听，已清理过期运行状态。");
            }
        }
    }

    public static bool HasRunningGame(IEnumerable<GameItem> games)
    {
        foreach (var game in games.Where(game => game.RunningProcessId.HasValue || game.Status == "运行中"))
        {
            if (game.RunningProcessId is not int processId) continue;
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited && MatchesRecordedProcess(game, process)) return true;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return false;
    }

    private static void Track(GameItem game, Process process, Action<GameItem, string> stateChanged)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => HandleExited(game, process, stateChanged);
        lock (Sync)
        {
            if (RunningProcesses.Remove(game.Id, out var previous)) previous.Dispose();
            RunningProcesses[game.Id] = process;
        }
        try { if (process.HasExited) HandleExited(game, process, stateChanged); }
        catch (InvalidOperationException) { }
    }

    private static void HandleExited(GameItem game, Process process, Action<GameItem, string> stateChanged)
    {
        lock (Sync)
        {
            if (!RunningProcesses.TryGetValue(game.Id, out var tracked) || tracked.Id != process.Id) return;
            RunningProcesses.Remove(game.Id);
        }
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { }
        GameRuntimeStateService.MarkExited(game, exitCode, DateTime.Now);
        process.Dispose();
        stateChanged(game, exitCode is null ? "主游戏程序已退出。" : $"主游戏程序已退出，退出代码：{exitCode}。");
    }

    private static bool IsTrackedProcessRunning(Guid gameId)
    {
        if (!RunningProcesses.TryGetValue(gameId, out var process)) return false;
        try { return !process.HasExited; }
        catch { return false; }
    }

    private static bool MatchesRecordedProcess(GameItem game, Process process)
    {
        if (game.CurrentPlayStartedAt is DateTime recordedStart)
        {
            try
            {
                if (Math.Abs((process.StartTime - recordedStart).TotalSeconds) > 10) return false;
            }
            catch { return false; }
        }
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || string.IsNullOrWhiteSpace(game.ExecutableRelativePath)) return false;
        var expected = Path.GetFullPath(Path.Combine(game.PlayableRootPath, game.ExecutableRelativePath));
        try { return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty), expected, StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }
}

public static class IconExtractionService
{
    public static string? ExtractToCache(string executablePath, Guid gameId)
    {
        if (!File.Exists(executablePath) || !Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var iconDirectory = Path.Combine(AppPaths.Data, "cache", "icons");
        Directory.CreateDirectory(iconDirectory);
        var outputPath = Path.Combine(iconDirectory, $"{gameId:N}.icon.secure");
        var extracted = ExtractIconEx(executablePath, 0, out var largeIcon, out var smallIcon, 1);
        var icon = largeIcon != IntPtr.Zero ? largeIcon : smallIcon;
        if (extracted == 0 || icon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            EncryptedDataFile.WriteAtomic(outputPath, stream.ToArray(), MasterKeyService.GetOrCreate(AppPaths.SecurityConfigFile));
            return Path.GetRelativePath(AppPaths.Root, outputPath);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"提取游戏图标失败：{executablePath}", ex);
            return null;
        }
        finally
        {
            if (largeIcon != IntPtr.Zero) DestroyIcon(largeIcon);
            if (smallIcon != IntPtr.Zero && smallIcon != largeIcon) DestroyIcon(smallIcon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string fileName, int iconIndex, out IntPtr largeIcon, out IntPtr smallIcon, uint iconCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}

public static class EncryptedIconService
{
    private static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Load(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var fullPath = Path.Combine(AppPaths.Root, relativePath);
        if (!File.Exists(fullPath)) return null;
        if (Path.GetExtension(fullPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) return new BitmapImage(new Uri(fullPath));
        lock (Cache)
        {
            if (Cache.TryGetValue(fullPath, out var cached)) return cached;
            var bytes = EncryptedDataFile.Read(fullPath, MasterKeyService.GetOrCreate(AppPaths.SecurityConfigFile));
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            CryptographicOperations.ZeroMemory(bytes); Cache[fullPath] = image; return image;
        }
    }

    public static string? EnsureEncrypted(string? relativePath, Guid gameId)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var fullPath = Path.Combine(AppPaths.Root, relativePath);
        if (!File.Exists(fullPath) || fullPath.EndsWith(".icon.secure", StringComparison.OrdinalIgnoreCase)) return relativePath;
        var output = Path.Combine(AppPaths.Data, "cache", "icons", $"{gameId:N}.icon.secure");
        var plaintext = File.ReadAllBytes(fullPath);
        var key = MasterKeyService.GetOrCreate(AppPaths.SecurityConfigFile);
        EncryptedDataFile.WriteAtomic(output, plaintext, key);
        var verified = EncryptedDataFile.Read(output, key);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(plaintext, verified))
                throw new CryptographicException("旧版明文图标迁移后的加密回读校验失败。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(verified);
        }
        return Path.GetRelativePath(AppPaths.Root, output);
    }

    public static void ClearMemoryCache() { lock (Cache) Cache.Clear(); }
}
