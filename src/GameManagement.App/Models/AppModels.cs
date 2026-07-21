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
    public List<PasswordHistoryItem> PasswordHistory { get; set; } = [];
    public List<ExtractionTemplateItem> ExtractionTemplates { get; set; } = [];
    public List<FileBaselineItem> FileBaselines { get; set; } = [];
    public List<SaveFileRuleItem> SaveFileRules { get; set; } = [];
    public List<SaveFileExclusionItem> SaveFileExclusions { get; set; } = [];
    public List<SaveCandidateItem> SaveCandidates { get; set; } = [];
    public List<SaveSnapshotItem> SaveSnapshots { get; set; } = [];
    public List<SystemSaveDirectoryRuleItem> SystemSaveDirectories { get; set; } = [];
    public List<SystemMonitorSessionItem> SystemMonitorSessions { get; set; } = [];
    public List<ExternalBackupItem> ExternalBackups { get; set; } = [];
    public BackupSettingsItem BackupSettings { get; set; } = new();
    public List<DeletionHistoryItem> DeletionHistory { get; set; } = [];
    public List<OperationTaskItem> OperationTasks { get; set; } = [];
    public UiSettingsItem UiSettings { get; set; } = new();
}

public sealed class UiSettingsItem
{
    public string ThemeName { get; set; } = ThemeNames.Classic;
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
    public DateTime AddedAt { get; set; }
    public Guid? ExtractionTemplateId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string SourceKind { get; set; } = SourceKinds.Unknown;
    public string Status { get; set; } = "未准备";
    public Guid? CurrentVersionId { get; set; }
    public string CurrentVersionName { get; set; } = string.Empty;
    public string? PlayableRootPath { get; set; }
    public string? ExecutableRelativePath { get; set; }
    public string? IconRelativePath { get; set; }
    public Guid? CurrentGameDiskId { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public DateTime? CurrentPlayStartedAt { get; set; }
    public DateTime? LastExitedAt { get; set; }
    public int? RunningProcessId { get; set; }
    public int? LastExitCode { get; set; }
    public long? LastRunDurationSeconds { get; set; }
    public bool HasLocalSave { get; set; }
    public bool HasSystemSave { get; set; } = true;
    public Guid? CurrentSaveGameDiskId { get; set; }
    public bool SystemSaveInitialScanCompleted { get; set; }
    public DateTime? SystemSaveInitialScanCompletedAt { get; set; }
    public string ArchiveStatus { get; set; } = "未归档";
    public string DirectoryCleanupStatus { get; set; } = "目录不存在";
    public DateTime? ArchivedAt { get; set; }
    public Guid? ArchivedVersionId { get; set; }
    public Guid? ArchivedSnapshotId { get; set; }
    public string ArchivedContentFingerprint { get; set; } = string.Empty;
    public string ArchiveMessage { get; set; } = string.Empty;
    public string SpecialArchiveBaselineStatus { get; set; } = string.Empty;
    public List<string> RetainedSourcePaths { get; set; } = [];
    public List<GameVersionItem> Versions { get; set; } = [];
    [JsonIgnore] public string SourcePathStatus => File.Exists(SourcePath) || Directory.Exists(SourcePath) ? "有效" : "失效";
    [JsonIgnore] public string? IconFullPath
    {
        get
        {
            var savedPath = !string.IsNullOrWhiteSpace(IconRelativePath)
                ? IconRelativePath
                : Versions.FirstOrDefault(version => version.Id == CurrentVersionId)?.IconRelativePath;
            return string.IsNullOrWhiteSpace(savedPath) ? null : System.IO.Path.Combine(AppPaths.Root, savedPath);
        }
    }
    [JsonIgnore] public string LastRunDurationText => LastRunDurationSeconds is long seconds ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : "暂无记录";
    [JsonIgnore] public string LocalSaveStatus => HasLocalSave ? "有" : "无";
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
    public string? ExecutableRelativePath { get; set; }
    public string? IconRelativePath { get; set; }
    public string FirstArchiveFormat { get; set; } = string.Empty;
    public string SecondArchiveFormat { get; set; } = string.Empty;
    public bool SecondArchiveUsedFallback { get; set; }
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
    public int StepOrder { get; set; }
    public string ArchiveDisplayName { get; set; } = string.Empty;
    public string ArchiveRelativePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? VerifiedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class PasswordHistoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EncryptedPassword { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
}

public sealed class ExtractionTemplateItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string EncryptedFirstPassword { get; set; } = string.Empty;
    public string EncryptedSecondPassword { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class ArchiveVolumeGroup
{
    public string GroupKey { get; set; } = string.Empty;
    public string VolumeKind { get; set; } = "single";
    public string EntryPath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public List<string> Files { get; set; } = [];
    public List<string> MissingFiles { get; set; } = [];
    public bool IsMultiVolume => !VolumeKind.Equals("single", StringComparison.OrdinalIgnoreCase);
    public long TotalSize => Files.Sum(path => { try { return new FileInfo(path).Length; } catch { return 0L; } });
    public string VolumeSummary => IsMultiVolume ? $"{Files.Count} 个分卷" : "单文件";
}

public sealed class FileBaselineItem
{
    public Guid GameId { get; set; }
    public Guid GameVersionId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string FileCategory { get; set; } = "游戏文件";
}

public sealed class SaveFileRuleItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "游戏目录";
    public string SourceRootPath { get; set; } = string.Empty;
    public string StorageRelativePath { get; set; } = string.Empty;
    public DateTime ConfirmedAt { get; set; } = DateTime.Now;
}

public sealed class SaveFileExclusionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "游戏目录";
    public string SourceRootPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class SaveCandidateItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public Guid GameVersionId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "游戏目录";
    public string SourcePath { get; set; } = string.Empty;
    public string SourceRootPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string StorageRelativePath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool DefaultExcluded { get; set; }
    public bool PreviouslyConfirmed { get; set; }
    public bool SharedDirectory { get; set; }
    public string ExclusionReason { get; set; } = string.Empty;
    public string Decision { get; set; } = SaveCandidateDecisions.Pending;
    public string SnapshotKind { get; set; } = SaveSnapshotKinds.Normal;
    public DateTime DetectedAt { get; set; } = DateTime.Now;
    [JsonIgnore] public string FileSizeText => SizeFormatter.Format(FileSize);
    [JsonIgnore] public string DefaultExcludedText => DefaultExcluded ? ExclusionReason : "否";
    [JsonIgnore] public string PreviouslyConfirmedText => PreviouslyConfirmed ? "是" : "否";
    [JsonIgnore] public string SharedDirectoryText => SharedDirectory ? "是，需再次确认" : "否";
    [JsonIgnore] public bool SourceExists => File.Exists(SourcePath);
}

public sealed class SaveSnapshotItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public Guid GameVersionId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string SnapshotKind { get; set; } = SaveSnapshotKinds.Normal;
    public string DirectoryPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public string ContentFingerprint { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public bool Verified { get; set; }
    public bool CleanupSuggested { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonIgnore] public string TotalSizeText => SizeFormatter.Format(TotalSize);
    [JsonIgnore] public string VerifiedText => Verified ? "已校验" : "校验失败";
    [JsonIgnore] public string CleanupSuggestionText => CleanupSuggested ? "建议清理" : "保留";
}

public sealed class SaveSnapshotManifest
{
    public Guid SnapshotId { get; set; }
    public Guid GameId { get; set; }
    public Guid GameVersionId { get; set; }
    public string SnapshotKind { get; set; } = SaveSnapshotKinds.Normal;
    public int? ExitCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ContentFingerprint { get; set; } = string.Empty;
    public List<SaveSnapshotFileItem> Files { get; set; } = [];
}

public sealed class SaveSnapshotFileItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string SourceKind { get; set; } = "游戏目录";
    public string OriginalPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ExternalBackupItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? GameId { get; set; }
    public Guid? GameVersionId { get; set; }
    public Guid? SaveSnapshotId { get; set; }
    public string BackupKind { get; set; } = "单游戏手动备份";
    public string FilePath { get; set; } = string.Empty;
    public string ContentFingerprint { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public bool CleanupSuggested { get; set; }
    public string Status { get; set; } = "已完成";
    public string ErrorMessage { get; set; } = string.Empty;
    public List<Guid> SourceGameDiskIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? VerifiedAt { get; set; }
    [JsonIgnore] public string FileSizeText => SizeFormatter.Format(FileSize);
    [JsonIgnore] public string VerifiedText => Verified ? "已校验" : "校验失败";
    [JsonIgnore] public string CleanupSuggestionText => CleanupSuggested ? "建议清理" : "保留";
}

public sealed class BackupSettingsItem
{
    public string BackupDirectory { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public uint VolumeSerialNumber { get; set; }
    public string RelativeDirectory { get; set; } = string.Empty;
    public bool DailyEnabled { get; set; }
    public string DailyTime { get; set; } = "02:00";
    public bool PendingScheduledBackup { get; set; }
    public DateTime? PendingSince { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string LastStatus { get; set; } = "尚未执行";
    public string LastError { get; set; } = string.Empty;
    public string LastContentFingerprint { get; set; } = string.Empty;
}

public sealed class ExternalBackupManifest
{
    public string BackupKind { get; set; } = string.Empty;
    public Guid? GameId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public Guid? GameVersionId { get; set; }
    public string GameVersionName { get; set; } = string.Empty;
    public Guid? SaveSnapshotId { get; set; }
    public string ContentFingerprint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<Guid> SourceGameDiskIds { get; set; } = [];
    public List<ExternalBackupFileItem> Files { get; set; } = [];
}

public sealed class ExternalBackupFileItem
{
    public string ZipPath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string OriginalRestorePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class SpecialArchiveDifferenceItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool DefaultExcluded { get; set; }
    public string ExclusionReason { get; set; } = string.Empty;
    [JsonIgnore] public string FileSizeText => SizeFormatter.Format(FileSize);
    [JsonIgnore] public string SelectableText => ChangeType is "新增" or "修改" or "人工选择" ? "可选择" : "仅查看";
}

public sealed class DeletionHistoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? GameId { get; set; }
    public Guid? GameVersionId { get; set; }
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectPath { get; set; } = string.Empty;
    public string DeleteMethod { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public sealed class SystemSaveDirectoryRuleItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AutoDiscovered { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastScannedAt { get; set; }
    [JsonIgnore] public string PathStatus => Directory.Exists(Path) ? "有效" : "失效";
}

public sealed class SystemMonitorSessionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GameId { get; set; }
    public string SnapshotFilePath { get; set; } = string.Empty;
    public List<string> DirectoryPaths { get; set; } = [];
    public bool IsInitialCommonScan { get; set; }
    public string Status { get; set; } = "监控中";
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}

public sealed class SystemFileSnapshotItem
{
    public string FullPath { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime ModifiedAt { get; set; }
}

public static class SaveCandidateDecisions
{
    public const string Pending = "待确认";
    public const string Confirmed = "已确认";
    public const string Excluded = "已排除";
}

public static class SaveSnapshotKinds
{
    public const string Normal = "正常";
    public const string Abnormal = "异常";
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
