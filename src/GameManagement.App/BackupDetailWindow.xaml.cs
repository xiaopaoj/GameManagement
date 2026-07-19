using System.Windows;
using GameManagement.Models;
using GameManagement.Services;

namespace GameManagement;

public partial class BackupDetailWindow : Window
{
    public BackupDetailWindow(ExternalBackupItem backup)
    {
        InitializeComponent();
        var manifest = ExternalBackupService.ReadManifest(backup.FilePath);
        FileGrid.ItemsSource = manifest.Files.OrderBy(item => item.ZipPath, StringComparer.CurrentCultureIgnoreCase).ToList();
        SummaryText.Text = $"类型：{manifest.BackupKind}｜游戏：{manifest.GameName}｜版本：{manifest.GameVersionName}｜创建：{manifest.CreatedAt:yyyy-MM-dd HH:mm:ss}｜文件：{manifest.Files.Count}";
        InstructionsText.Text = "人工恢复步骤：\n\n1. 关闭游戏及可能读写存档的插件、启动器。\n2. 先备份目标位置当前已有的文件。\n3. 使用系统解压工具打开此无密码 ZIP。\n4. 根据上表“原始恢复路径”，将 ZIP 中对应文件复制到游戏目录或 Windows 用户目录。\n5. 遇到同名文件时由你人工决定是否覆盖。\n6. 跨游戏版本恢复可能不兼容，恢复后请先保留原存档并验证游戏能正常读取。\n\n软件只提供查看与路径提示，不会自动恢复外部 ZIP。";
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (FileGrid.SelectedItem is not ExternalBackupFileItem file || string.IsNullOrWhiteSpace(file.OriginalRestorePath)) return;
        System.Windows.Clipboard.SetText(file.OriginalRestorePath);
        MessageBox.Show("恢复路径已复制。", "复制完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
