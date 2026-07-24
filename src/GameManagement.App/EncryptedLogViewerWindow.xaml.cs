using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GameManagement.Services;

namespace GameManagement;

public partial class EncryptedLogViewerWindow : Window
{
    public EncryptedLogViewerWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = Directory.Exists(AppPaths.Logs) ? Directory.EnumerateFiles(AppPaths.Logs, "*.securelog").Select(path => new FileInfo(path)).OrderByDescending(file => file.Name).ToList() : [];
        LogList.SelectedIndex = LogList.Items.Count > 0 ? 0 : -1;
    }
    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not FileInfo file) { LogContent.Clear(); return; }
        try { LogContent.Text = Encoding.UTF8.GetString(EncryptedDataFile.Read(file.FullName, MasterKeyService.GetOrCreate(AppPaths.SecurityConfigFile))); }
        catch (Exception ex) { LogContent.Text = $"日志解密或完整性校验失败：{ex.Message}"; }
    }
}
