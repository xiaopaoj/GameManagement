using System.Text.Json.Serialization;
using System.IO;

namespace GameManagement.Models;

public sealed class AppState
{
    public List<ScanPathItem> ScanPaths { get; set; } = [];
    public List<GameDiskItem> GameDisks { get; set; } = [];
    public List<GameItem> Games { get; set; } = [];
}

public sealed class ScanPathItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime? LastScannedAt { get; set; }
}

public sealed class GameDiskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public long MinimumFreeSpaceBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    [JsonIgnore] public string FreeSpaceText { get { try { var root = System.IO.Path.GetPathRoot(RootPath); return root is null ? "未知" : SizeFormatter.Format(new DriveInfo(root).AvailableFreeSpace); } catch { return "不可用"; } } }
}

public sealed class GameItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Status { get; set; } = "未准备";
    public Guid? CurrentVersionId { get; set; }
    public string CurrentVersionName { get; set; } = string.Empty;
    public string? PlayableRootPath { get; set; }
    public string? ExecutableRelativePath { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public List<GameVersionItem> Versions { get; set; } = [];
}

public sealed class GameVersionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VersionName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class ScanCandidate
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool Added { get; set; }
    public string DisplaySize => SizeFormatter.Format(Size);
}

public sealed class OperationTaskItem
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "等待";
    public int Progress { get; set; }
    public string ProgressText => $"{Progress}%";
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
}

public static class SizeFormatter
{
    public static string Format(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size; var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.##} {units[index]}";
    }
}
