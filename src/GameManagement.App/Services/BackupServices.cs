using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameManagement.Models;
using Forms = System.Windows.Forms;

namespace GameManagement.Services;

public sealed record BackupExecutionResult(ExternalBackupItem? Backup, string Status, string Message, bool WaitingForTarget = false, bool SkippedUnchanged = false);

public static class BackupTargetService
{
    public static void Configure(BackupSettingsItem settings, string directory)
    {
        var fullPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException("无法识别备份目录所在磁盘。");
        settings.BackupDirectory = fullPath;
        settings.RelativeDirectory = Path.GetRelativePath(root, fullPath);
        settings.VolumeLabel = GetVolumeLabel(root);
        settings.VolumeSerialNumber = GetVolumeSerialNumber(root);
    }

    public static string? Resolve(BackupSettingsItem settings, bool createWhenOnline)
    {
        if (!string.IsNullOrWhiteSpace(settings.BackupDirectory))
        {
            var root = Path.GetPathRoot(settings.BackupDirectory);
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                if (createWhenOnline) Directory.CreateDirectory(settings.BackupDirectory);
                if (Directory.Exists(settings.BackupDirectory)) return settings.BackupDirectory;
            }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(item => item.IsReady))
        {
            try
            {
                var serial = GetVolumeSerialNumber(drive.RootDirectory.FullName);
                var labelMatches = !string.IsNullOrWhiteSpace(settings.VolumeLabel) && drive.VolumeLabel.Equals(settings.VolumeLabel, StringComparison.OrdinalIgnoreCase);
                if ((settings.VolumeSerialNumber != 0 && serial == settings.VolumeSerialNumber) || (settings.VolumeSerialNumber == 0 && labelMatches))
                {
                    var resolved = Path.GetFullPath(Path.Combine(drive.RootDirectory.FullName, settings.RelativeDirectory));
                    if (createWhenOnline) Directory.CreateDirectory(resolved);
                    settings.BackupDirectory = resolved;
                    return Directory.Exists(resolved) ? resolved : null;
                }
            }
            catch { }
        }
        return null;
    }

    private static string GetVolumeLabel(string root)
    {
        try { return new DriveInfo(root).IsReady ? new DriveInfo(root).VolumeLabel : string.Empty; }
        catch { return string.Empty; }
    }

    private static uint GetVolumeSerialNumber(string root)
    {
        var volumeName = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        return GetVolumeInformation(root, volumeName, volumeName.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity) ? serial : 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}

public static class ExternalBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<BackupExecutionResult> CreateManualGameBackupAsync(AppState state, GameItem game, CancellationToken token = default)
    {
        var backupDirectory = BackupTargetService.Resolve(state.BackupSettings, true) ?? throw new InvalidOperationException("外部备份目录不可用，请先在每日计划备份设置中配置在线目录。");
        ValidateBackupDirectory(state, backupDirectory);
        var saveRoot = GameSavePathService.GetGameSaveRoot(state, game);
        var current = Path.Combine(saveRoot, "current");
        var currentManifestPath = Path.Combine(saveRoot, "manifest.json");
        if (!Directory.Exists(current) || !File.Exists(currentManifestPath)) throw new InvalidOperationException("该游戏尚未生成可备份的 current 存档和清单。");
        var saveManifest = await CurrentSaveManifestService.LoadAsync(state, game, token) ?? throw new InvalidDataException("当前存档清单无效。");
        if (!await CurrentSaveManifestService.VerifyAsync(state, game, saveManifest, token)) throw new InvalidDataException("当前存档 Hash 校验失败，禁止创建外部备份。");

        var manifest = new ExternalBackupManifest
        {
            BackupKind = "单游戏手动备份",
            GameId = game.Id,
            GameName = game.DisplayName,
            GameVersionId = game.CurrentVersionId,
            GameVersionName = game.CurrentVersionName,
            SaveSnapshotId = saveManifest.SnapshotId == Guid.Empty ? null : saveManifest.SnapshotId,
            ContentFingerprint = saveManifest.ContentFingerprint,
            SourceGameDiskIds = [GameSavePathService.GetCurrentDisk(state, game).Id]
        };
        var gameFolder = $"GameSave/{game.Id:N}";
        foreach (var file in saveManifest.Files)
        {
            var source = CurrentSaveManifestService.GetSafeChildPath(current, file.RelativePath);
            manifest.Files.Add(new ExternalBackupFileItem
            {
                ZipPath = NormalizeZipPath($"{gameFolder}/current/{file.RelativePath}"),
                SourcePath = source,
                OriginalRestorePath = file.OriginalPath,
                FileSize = file.FileSize,
                Sha256 = file.Sha256
            });
        }
        manifest.Files.Add(await CreateFileItemAsync(currentManifestPath, $"{gameFolder}/manifest.json", currentManifestPath, token));
        var fileName = $"GameSave_{SanitizeFileName(game.DisplayName)}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.zip";
        return await CreateAndRegisterAsync(state, manifest, Path.Combine(backupDirectory, fileName), token);
    }

    public static async Task<BackupExecutionResult> CreateScheduledFullBackupAsync(AppState state, CancellationToken token = default)
    {
        var settings = state.BackupSettings;
        settings.LastRunAt = DateTime.Now;
        var backupDirectory = BackupTargetService.Resolve(settings, true);
        if (backupDirectory is null)
        {
            settings.PendingScheduledBackup = true;
            settings.PendingSince ??= DateTime.Now;
            settings.LastStatus = "等待目标磁盘";
            settings.LastError = "外部备份磁盘当前离线，将在重新上线后补执行。";
            return new BackupExecutionResult(null, settings.LastStatus, settings.LastError, true);
        }
        ValidateBackupDirectory(state, backupDirectory);

        var manifest = new ExternalBackupManifest { BackupKind = "完整 GameSave 每日备份" };
        var failures = new List<string>();
        foreach (var disk in state.GameDisks.Where(item => Directory.Exists(item.RootPath)).OrderBy(item => item.Id))
        {
            token.ThrowIfCancellationRequested();
            var gameSave = Path.Combine(disk.RootPath, "GameSave");
            if (!Directory.Exists(gameSave)) continue;
            try
            {
                manifest.SourceGameDiskIds.Add(disk.Id);
                foreach (var source in Directory.EnumerateFiles(gameSave, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false }).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(gameSave, source);
                    manifest.Files.Add(await CreateFileItemAsync(source, $"game-disk-{disk.Id:N}/GameSave/{relative}", source, token));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add($"{disk.DisplayName}：{ex.Message}"); }
        }
        manifest.ContentFingerprint = ComputeContentFingerprint(manifest.Files);
        var latest = state.ExternalBackups.Where(item => item.BackupKind == manifest.BackupKind && item.Verified && File.Exists(item.FilePath)).OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        if (latest is not null && latest.ContentFingerprint.Equals(manifest.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            settings.PendingScheduledBackup = false;
            settings.PendingSince = null;
            settings.LastStatus = "内容未变化，未重复备份";
            settings.LastError = string.Empty;
            settings.LastContentFingerprint = manifest.ContentFingerprint;
            return new BackupExecutionResult(null, settings.LastStatus, settings.LastStatus, false, true);
        }
        var output = Path.Combine(backupDirectory, $"GameSaveBackup_{DateTime.Now:yyyyMMdd_HHmmss_fff}.zip");
        var completed = await CreateAndRegisterAsync(state, manifest, output, token);
        var result = failures.Count == 0 ? completed : new BackupExecutionResult(completed.Backup, "部分失败", $"备份已创建并校验，但部分游戏盘读取失败：{string.Join("；", failures)}");
        settings.PendingScheduledBackup = false;
        settings.PendingSince = null;
        settings.LastStatus = result.Status;
        settings.LastError = string.Empty;
        settings.LastContentFingerprint = manifest.ContentFingerprint;
        UpdateScheduledCleanupSuggestions(state);
        return result;
    }

    public static async Task<bool> VerifyAsync(ExternalBackupItem backup, CancellationToken token = default)
    {
        if (!File.Exists(backup.FilePath)) return false;
        try
        {
            using var archive = ZipFile.OpenRead(backup.FilePath);
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null) return false;
            ExternalBackupManifest? manifest;
            await using (var stream = manifestEntry.Open()) manifest = await JsonSerializer.DeserializeAsync<ExternalBackupManifest>(stream, JsonOptions, token);
            if (manifest is null) return false;
            var entries = archive.Entries.ToDictionary(item => item.FullName, StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!entries.TryGetValue(NormalizeZipPath(file.ZipPath), out var entry) || entry.Length != file.FileSize) return false;
                await using var stream = entry.Open();
                using var sha = SHA256.Create();
                var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, token)).ToLowerInvariant();
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return false; }
    }

    public static ExternalBackupManifest ReadManifest(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("备份 ZIP 中缺少 manifest.json。");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<ExternalBackupManifest>(stream, JsonOptions) ?? throw new InvalidDataException("备份清单格式无效。");
    }

    public static IReadOnlyList<string> ListEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Select(item => item.FullName).OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void Remove(AppState state, ExternalBackupItem backup)
    {
        if (File.Exists(backup.FilePath)) File.Delete(backup.FilePath);
        state.ExternalBackups.RemoveAll(item => item.Id == backup.Id);
        state.DeletionHistory.Add(new DeletionHistoryItem { GameId = backup.GameId, GameVersionId = backup.GameVersionId, ObjectType = "外部 ZIP 备份", ObjectPath = backup.FilePath, DeleteMethod = "永久删除", Status = "成功" });
        UpdateScheduledCleanupSuggestions(state);
    }

    private static async Task<BackupExecutionResult> CreateAndRegisterAsync(AppState state, ExternalBackupManifest manifest, string output, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".partial";
        if (File.Exists(temporary)) File.Delete(temporary);
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create, Encoding.UTF8))
            {
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(NormalizeZipPath(file.ZipPath), CompressionLevel.Optimal);
                    await using var input = File.OpenRead(file.SourcePath);
                    await using var outputStream = entry.Open();
                    await input.CopyToAsync(outputStream, token);
                }
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, token);
            }
            var item = new ExternalBackupItem
            {
                GameId = manifest.GameId,
                GameVersionId = manifest.GameVersionId,
                SaveSnapshotId = manifest.SaveSnapshotId,
                BackupKind = manifest.BackupKind,
                FilePath = output,
                ContentFingerprint = manifest.ContentFingerprint,
                FileSize = new FileInfo(temporary).Length,
                SourceGameDiskIds = manifest.SourceGameDiskIds,
                CreatedAt = manifest.CreatedAt
            };
            File.Move(temporary, output, true);
            item.Verified = await VerifyAsync(item, token);
            if (!item.Verified) { File.Delete(output); throw new InvalidDataException("外部备份 ZIP 内容或文件 Hash 校验失败。"); }
            item.Sha256 = await FileFingerprintService.ComputeSha256Async(output, token);
            item.VerifiedAt = DateTime.Now;
            state.ExternalBackups.Add(item);
            return new BackupExecutionResult(item, "已完成", $"外部备份已创建并校验：{output}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { AppLogger.Error($"外部备份临时文件清理失败：{temporary}", ex); }
            }
        }
    }

    private static async Task<ExternalBackupFileItem> CreateFileItemAsync(string source, string zipPath, string restorePath, CancellationToken token)
    {
        var info = new FileInfo(source);
        return new ExternalBackupFileItem { ZipPath = NormalizeZipPath(zipPath), SourcePath = source, OriginalRestorePath = restorePath, FileSize = info.Length, Sha256 = await FileFingerprintService.ComputeSha256Async(source, token) };
    }

    private static string ComputeContentFingerprint(IEnumerable<ExternalBackupFileItem> files)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(item => item.ZipPath, StringComparer.OrdinalIgnoreCase))
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(file.ZipPath.ToLowerInvariant()));
            aggregate.AppendData(BitConverter.GetBytes(file.FileSize));
            aggregate.AppendData(Convert.FromHexString(file.Sha256));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static void UpdateScheduledCleanupSuggestions(AppState state)
    {
        var backups = state.ExternalBackups.Where(item => item.BackupKind == "完整 GameSave 每日备份").OrderByDescending(item => item.CreatedAt).ToList();
        for (var index = 0; index < backups.Count; index++) backups[index].CleanupSuggested = index >= 3;
    }

    private static string NormalizeZipPath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static void ValidateBackupDirectory(AppState state, string backupDirectory)
    {
        var target = Path.GetFullPath(backupDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var disk in state.GameDisks)
        {
            var gameSave = Path.GetFullPath(Path.Combine(disk.RootPath, "GameSave")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (target.StartsWith(gameSave, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("外部备份目录不能位于任何游戏盘的 GameSave 目录内部。");
        }
    }
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Game" : result;
    }
}

public static class BackupScheduleService
{
    public const string TaskName = "GameManagement-DailyBackup";

    public static void RegisterDaily(string time)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", out _)) throw new InvalidOperationException("每日备份时间必须使用 HH:mm 格式。");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取当前 EXE 路径。");
        RunSchtasks($"/Create /F /SC DAILY /ST {time} /TN \"{TaskName}\" /TR \"\\\"{executable}\\\" --scheduled-backup\"");
    }

    public static void RemoveDaily()
    {
        var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        process?.WaitForExit();
    }

    private static void RunSchtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new InvalidOperationException("无法启动 Windows 任务计划程序命令。");
        process.WaitForExit();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0) throw new InvalidOperationException($"注册 Windows 每日备份任务失败：{error}");
    }
}

public static class BackupNotificationService
{
    public static async Task ShowAsync(string title, string message, Forms.ToolTipIcon icon)
    {
        if (!Environment.UserInteractive) return;
        using var notification = new Forms.NotifyIcon { Visible = true, Icon = System.Drawing.SystemIcons.Application, Text = "游戏管理软件" };
        notification.ShowBalloonTip(5000, title, message, icon);
        await Task.Delay(5000);
    }
}
