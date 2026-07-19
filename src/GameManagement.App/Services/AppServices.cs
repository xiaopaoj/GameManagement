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
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(AppPaths.StateFile), Options) ?? new AppState(); }
        catch (Exception ex) { AppLogger.Error("读取数据文件失败", ex); throw new InvalidOperationException("读取软件数据失败，请检查 data 目录中的数据文件。", ex); }
    }
    public void Save(AppState state) { var temp = AppPaths.StateFile + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(state, Options)); File.Move(temp, AppPaths.StateFile, true); }
}

public sealed class FileScanner
{
    public Task<List<ScanCandidate>> ScanAsync(IEnumerable<ScanPathItem> paths, IReadOnlyCollection<GameItem> games, CancellationToken token) => Task.Run(() =>
    {
        var existing = games.Select(g => Normalize(g.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase); var result = new List<ScanCandidate>();
        foreach (var item in paths.Where(p => p.Enabled && Directory.Exists(p.Path)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(item.Path)) { token.ThrowIfCancellationRequested(); result.Add(new ScanCandidate { Name = Path.GetFileName(directory), FullPath = directory, Kind = "文件夹", Added = existing.Contains(Normalize(directory)) }); }
                foreach (var file in Directory.EnumerateFiles(item.Path))
                {
                    token.ThrowIfCancellationRequested(); var extension = Path.GetExtension(file);
                    if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file); result.Add(new ScanCandidate { Name = info.Name, FullPath = info.FullName, Kind = extension.TrimStart('.').ToUpperInvariant(), Size = info.Length, Added = existing.Contains(Normalize(file)) });
                }
            }
            catch (UnauthorizedAccessException ex) { AppLogger.Error($"无权扫描目录 {item.Path}", ex); }
            item.LastScannedAt = DateTime.Now;
        }
        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }, token);
    private static string Normalize(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public static class ShellService
{
    public static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    public static void LaunchGame(GameItem game)
    {
        if (string.IsNullOrWhiteSpace(game.PlayableRootPath) || string.IsNullOrWhiteSpace(game.ExecutableRelativePath)) throw new InvalidOperationException("该游戏尚未配置可游玩目录或主游戏 EXE。");
        var executable = Path.Combine(game.PlayableRootPath, game.ExecutableRelativePath); if (!File.Exists(executable)) throw new FileNotFoundException("主游戏 EXE 不存在。", executable);
        Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = true });
    }
}
