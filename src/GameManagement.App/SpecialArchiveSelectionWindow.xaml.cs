using System.IO;
using System.Windows;
using GameManagement.Models;

namespace GameManagement;

public partial class SpecialArchiveSelectionWindow : Window
{
    public IReadOnlyList<SpecialArchiveDifferenceItem> SelectedFiles { get; private set; } = [];

    public SpecialArchiveSelectionWindow(IReadOnlyList<SpecialArchiveDifferenceItem> differences, bool completeBaseline)
    {
        InitializeComponent();
        DifferenceGrid.ItemsSource = differences;
        ModeText.Text = completeBaseline
            ? "已建立完整干净基线。列表显示新增、修改、缺失及默认排除文件；只有新增和修改项可作为存档。请按 Ctrl 或 Shift 多选。"
            : "无完整基线：软件禁止自动判断。下方所有文件都必须由你人工选择，未选择文件不会被归档；在完成有效外部备份前不会删除混乱目录。";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var selected = DifferenceGrid.SelectedItems.Cast<SpecialArchiveDifferenceItem>().ToList();
        if (selected.Count == 0) { MessageBox.Show("请至少选择一个需要归档的存档文件。", "选择提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (selected.Any(item => item.ChangeType == "缺失")) { MessageBox.Show("“缺失”项只用于展示干净基线差异，不能作为存档文件。", "选择无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (selected.Any(item => item.DefaultExcluded)
            && MessageBox.Show("所选文件中包含 100 MB 以上、疑似资源包或无完整基线的默认排除项。是否确认将这些具体文件作为存档？", "默认排除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SelectedFiles = selected;
        DialogResult = true;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (DifferenceGrid.SelectedItem is not SpecialArchiveDifferenceItem file || !File.Exists(file.SourcePath)) return;
        new FilePreviewWindow(file.SourcePath) { Owner = this }.ShowDialog();
    }
}
