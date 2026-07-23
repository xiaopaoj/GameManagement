using System.ComponentModel;
using System.Windows;

namespace GameManagement;

public partial class PreparationProgressWindow : Window
{
    private Action? _cancel;
    private bool _allowClose;
    private bool _runInBackground;
    public PreparationProgressWindow(string title = "正在准备游戏") { InitializeComponent(); Title = title; TitleText.Text = title; }
    public void UpdateStatus(string message, int? percentage = null)
    {
        StatusText.Text = message;
        Progress.IsIndeterminate = !percentage.HasValue;
        if (percentage.HasValue) Progress.Value = Math.Clamp(percentage.Value, 0, 100);
    }
    public void EnableCancellation(Action cancel) { _cancel = cancel; CancelButton.Visibility = Visibility.Visible; }
    public void CloseSafely() { _allowClose = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancel is null) return;
        CancelButton.IsEnabled = false; StatusText.Text = "正在取消任务，请稍候…"; _cancel();
    }
    private void Background_Click(object sender, RoutedEventArgs e)
    {
        _runInBackground = true;
        Hide();
    }
    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_runInBackground && IsVisible) Dispatcher.BeginInvoke(Hide);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _runInBackground = true;
        Hide();
    }
}
