using System.Windows;

namespace GameManagement;

public partial class HistoryReplayWindow : Window
{
    public bool UseHistory { get; private set; }
    public bool AutoReplayFollowingSteps => AutoReplayCheck.IsChecked == true;

    public HistoryReplayWindow(string stepName, string archivePath, bool allowAutoReplayFollowingSteps)
    {
        InitializeComponent();
        PromptText.Text = $"检测到上次成功使用的{stepName}文件：\n{archivePath}\n\n是否重放历史选择和已保存密码？";
        AutoReplayCheck.Visibility = allowAutoReplayFollowingSteps ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        UseHistory = true;
        DialogResult = true;
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        UseHistory = false;
        DialogResult = false;
    }
}
