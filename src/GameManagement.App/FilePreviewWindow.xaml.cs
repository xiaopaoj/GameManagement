using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GameManagement;

public partial class FilePreviewWindow : Window
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".json", ".xml", ".ini", ".cfg", ".conf", ".log", ".csv", ".yaml", ".yml", ".html", ".htm", ".md" };
    private readonly string _path;

    public FilePreviewWindow(string path)
    {
        InitializeComponent();
        _path = path;
        PathText.Text = path;
        Title = $"文件预览 - {Path.GetFileName(path)}";
        LoadPreview();
    }

    private void LoadPreview()
    {
        if (!File.Exists(_path)) { TextPreview.Text = "文件已经不存在，无法预览。"; return; }
        var extension = Path.GetExtension(_path);
        try
        {
            if (ImageExtensions.Contains(extension))
            {
                using var stream = File.OpenRead(_path);
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                ImagePreview.Source = image; ImagePreview.Visibility = Visibility.Visible; TextPreview.Visibility = Visibility.Collapsed;
                return;
            }
            if (TextExtensions.Contains(extension) || new FileInfo(_path).Length <= 1024 * 1024)
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[1024 * 1024];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                TextPreview.Text = new string(buffer, 0, length) + (reader.Peek() >= 0 ? "\n\n[文件内容超过 1 MB，仅显示前 1 MB]" : string.Empty);
                return;
            }
            TextPreview.Text = $"该文件不是常见文本或图片格式。\n\n文件大小：{new FileInfo(_path).Length:N0} 字节\n可以点击“使用系统程序打开”查看。";
        }
        catch (Exception ex) { TextPreview.Text = $"预览失败：{ex.Message}"; }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) { MessageBox.Show("文件已经不存在。", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
    }
}
