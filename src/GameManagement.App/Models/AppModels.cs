using System.Text.Json.Serialization;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameManagement.Services;

namespace GameManagement.Models;

public sealed class AppState
{
    public List<ScanPathItem> ScanPaths { get; set; } = [];
    public List<GameDiskItem> GameDisks { get; set; } = [];
    public List<GameItem> Games { get; set; } = [];
    public List<ArchiveCredentialItem> Credentials { get; set; } = [];
    public List<FileBaselineItem> FileBaselines { get; set; } = [];
    public List<OperationTaskItem> OperationTasks { get; set; } = [];
}

public sealed class ScanPathItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Path { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime? LastScannedAt { get; set; }
    [JsonIgnore] public string PathStatus => Directory.Exists(Path) ? "有效" : "失效";
    [JsonIgnore] public string DriveName => System.IO.Path.GetPathRoot(Path) ?? string.Empty;
    [JsonIgnore] public string FreeSpaceText { get { try { var root = System.IO.Path.GetPathRoot(Path); return root is null ? "未知" : SizeFormatter.Format(new DriveInfo(root).AvailableFreeSpace); } catch { return "不可用"; } } }
}

public sealed class GameDiskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public long MinimumFreeSpaceBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    [JsonIgnore] public string FreeSpaceText { get { try { var root = System.IO.Path.GetPathRoot(RootPath); return root is null ? "未知" : SizeFormatter.Format(new DriveInfo(root).AvailableFreeSpace); } catch { return "不可用"; } } }
    [JsonIgnore] public string TotalSpaceText { get { try { var root = System.IO.Path.GetPathRoot(RootPath); return root is null ? "未知" : SizeFormatter.Format(new DriveInfo(root).TotalSize); } catch { return "不可用"; } } }
    [JsonIgnore] public string DiskTypeText { get { try { var root = System.IO.Path.GetPathRoot(RootPath); return root is null ? "未知" : TranslateDriveType(new DriveInfo(root).DriveType); } catch { return "不可用"; } } }
    [JsonIgnore] public string PathStatus => Directory.Exists(RootPath) ? "有效" : "失效";
    [JsonIgnore] public string MinimumFreeSpaceText => SizeFormatter.Format(MinimumFreeSpaceBytes);
    private static string TranslateDriveType(DriveType type) => type switch { DriveType.Fixed => "本地磁盘", DriveType.Removable => "可移动磁盘", DriveType.Network => "网络磁盘", DriveType.CDRom => "光驱", DriveType.Ram => "内存盘", _ => "未知" };
}

public sealed class GameItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceKind { get; set; } = SourceKinds.Unknown;
    public string Status { get; set; } = "未准备";
    public Guid? CurrentVersionId { get; set; }
    public string CurrentVersionName { get; set; } = string.Empty;
    public string? PlayableRootPath { get; set; }
    public string? ExecutableRelativePath { get; set; }
    public Guid? CurrentGameDiskId { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public List<GameVersionItem> Versions { get; set; } = [];
    [JsonIgnore] public string SourcePathStatus => File.Exists(SourcePath) || Directory.Exists(SourcePath) ? "有效" : "失效";
}

public sealed class GameVersionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string VersionName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceKind { get; set; } = SourceKinds.Unknown;
    public Guid? GameDiskId { get; set; }
    public string? FirstArchiveRelativePath { get; set; }
    public string? SecondArchiveRelativePath { get; set; }
    public int SourceFileCount { get; set; }
    public long SourceSize { get; set; }
    public DateTime? SourceModifiedAt { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonIgnore] public string SourcePathStatus => File.Exists(SourcePath) || Directory.Exists(SourcePath) ? "有效" : "失效";
    [JsonIgnore] public string SourceSizeText => SizeFormatter.Format(SourceSize);
}

public sealed class ArchiveCredentialItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameVersionId { get; set; }
    public string ArchiveFingerprint { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class FileBaselineItem
{
    public Guid GameVersionId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ChoiceItem
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [JsonIgnore] public object? Value { get; set; }
}

public sealed class ScanCandidate
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool Added { get; set; }
    public int ArchiveCount { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string DriveName { get; set; } = string.Empty;
    public bool PathValid => File.Exists(FullPath) || Directory.Exists(FullPath);
    public string DisplaySize => SizeFormatter.Format(Size);
    public string ArchiveSummary => ArchiveCount switch { < 0 => "准备时扫描", 0 => "未发现", 1 => "1 个", _ => $"{ArchiveCount} 个" };
}

public sealed record SourceMetadataSnapshot(int FileCount, long TotalSize, DateTime? ModifiedAt, string Fingerprint);

public sealed record SourceMetadataProgress(int Completed, int Total, string CurrentPath)
{
    public int Percentage => Total <= 0 ? 100 : (int)Math.Clamp(Completed * 100L / Total, 0, 100);
}

public sealed class ScanProgressInfo
{
    public int Completed { get; set; }
    public int Total { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int Percentage => Total <= 0
        ? IsCompleted ? 100 : 0
        : (int)Math.Clamp(Completed * 100L / Total, 0, 100);
}

public static class SourceKinds
{
    public const string ArchiveFile = "压缩文件";
    public const string ArchiveDirectory = "压缩文件目录";
    public const string Unknown = "未知";

    public static string Detect(string path)
    {
        if (File.Exists(path) && ArchiveDiscoveryService.IsSupportedArchive(path)) return ArchiveFile;
        if (Directory.Exists(path)) return ArchiveDirectory;
        return Unknown;
    }
}

public sealed class OperationTaskItem : INotifyPropertyChanged
{
    private string _status = "等待";
    private int _progress;
    private string _message = string.Empty;
    private string _currentPath = string.Empty;
    private string _errorMessage = string.Empty;
    private string _workingDirectory = string.Empty;
    private DateTime? _completedAt;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public Guid? GameId { get; set; }
    public Guid? GameVersionId { get; set; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int Progress { get => _progress; set { if (Set(ref _progress, value)) Raise(nameof(ProgressText)); } }
    [JsonIgnore] public string ProgressText => $"{Progress}%";
    public string Message { get => _message; set => Set(ref _message, value); }
    public string CurrentPath { get => _currentPath; set => Set(ref _currentPath, value); }
    public string ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Set(ref _workingDirectory, value); }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get => _completedAt; set => Set(ref _completedAt, value); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
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
