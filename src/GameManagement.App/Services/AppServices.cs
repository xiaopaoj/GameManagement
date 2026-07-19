using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GameManagement.Models;

namespace GameManagement.Services;

public static class AppPaths
{
    public static string Root => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static string Data => Path.Combine(Root, "data");
    public static string Logs => Path.Combine(Root, "logs");
    public static string StateFile => Path.Combine(Data, "game-management.json");
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Data); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Path.Combine(Data, "cache"));
        var probe = Path.Combine(Data, $".write-test-{Guid.NewGuid():N}"); File.WriteAllText(probe, "ok"); File.Delete(probe);
    }
}

public static class AppLogger
{
    private static readonly object Sync = new(); private static string _file = string.Empty;
    public static void Initialize() { _file = Path.Combine(AppPaths.Logs, $"application-{DateTime.Now:yyyyMMdd}.log"); Info("软件启动"); }
    public static void Info(string message) => Write("信息", message);
    public static void Error(string message, Exception ex) => Write("错误", $"{message}：{ex}");
    private static void Write(string level, string message) { lock (Sync) { if (!string.IsNullOrWhiteSpace(_file)) File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}"); } }
}

public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public AppState Load()
    {
        if (!File.Exists(AppPaths.StateFile)) return new AppState();
        try
        {
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(AppPaths.StateFile), Options) ?? new AppState();
            foreach (var game in state.Games)
            {
                if (string.IsNullOrWhiteSpace(game.SourceKind) || game.SourceKind == SourceKinds.Unknown) game.SourceKind = SourceKinds.Detect(game.SourcePath);
                foreach (var version in game.Versions)
                    if (string.IsNullOrWhiteSpace(version.SourceKind) || version.SourceKind == SourceKinds.Unknown) version.SourceKind = SourceKinds.Detect(version.SourcePath);
            }
            OperationTaskRecoveryService.MarkInterrupted(state);
            return state;
        }
        catch (Exception ex) { AppLogger.Error("读取数据文件失败", ex); throw new InvalidOperationException("读取软件数据失败，请检查 data 目录中的数据文件。", ex); }
    }
    public void Save(AppState state) { var temp = AppPaths.StateFile + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(state, Options)); File.Move(temp, AppPaths.StateFile, true); }
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
        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }, token);
    private static bool HasSupportedArchiveExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rar", StringComparison.OrdinalIgnoreCase);
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
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var headLength = (int)Math.Min(stream.Length, 4L * 1024 * 1024);
            var head = new byte[headLength];
            stream.ReadExactly(head);
            if (ContainsZipSignature(head)) return "ZIP";
            if (ContainsRarSignature(head)) return "RAR";

            if (stream.Length > headLength)
            {
                var tailLength = (int)Math.Min(stream.Length, 1024L * 1024);
                var tail = new byte[tailLength];
                stream.Position = stream.Length - tailLength;
                stream.ReadExactly(tail);
                if (ContainsZipSignature(tail)) return "ZIP";
                if (ContainsRarSignature(tail)) return "RAR";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public static string EnsureCorrectExtension(string path, string format)
    {
        var expectedExtension = format.Equals("ZIP", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".rar";
        if (Path.GetExtension(path).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase)) return path;
        var target = Path.ChangeExtension(path, expectedExtension);
        if (File.Exists(target)) target = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}{expectedExtension}");
        File.Move(path, target);
        return target;
    }

    public static async Task<string> CreateNormalizedWorkingArchiveAsync(string path, string format, CancellationToken token = default)
    {
        var expectedExtension = format.Equals("ZIP", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".rar";
        var signature = format.Equals("ZIP", StringComparison.OrdinalIgnoreCase)
            ? new byte[] { 0x50, 0x4B, 0x03, 0x04 }
            : new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07 };
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
}

public static class ShellService
{
    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    public static void LaunchGame(GameItem game)
    {
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || string.IsNullOrWhiteSpace(game.ExecutableRelativePath)) throw new InvalidOperationException("该游戏尚未配置可游玩目录或启动文件。");
        var executable = Path.Combine(game.PlayableRootPath, game.ExecutableRelativePath); if (!File.Exists(executable)) throw new FileNotFoundException("游戏启动文件不存在。", executable);
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = true });
    }
}
