using System.Windows;
using System.Windows.Threading;

namespace GameManagement.Services;

/// <summary>
/// 统一恢复阻塞式任务的调用窗口，避免进度窗口关闭后窗口组退到后台或意外最小化。
/// </summary>
public static class WindowInteractionService
{
    public static void RestoreBeforeDialog(Window owner, Window? progressWindow = null)
    {
        if (progressWindow?.IsVisible == true) progressWindow.Hide();
        Restore(owner);
    }

    public static void CompleteProgress(Window owner, PreparationProgressWindow progressWindow)
    {
        owner.IsEnabled = true;
        progressWindow.CloseSafely();
        _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Restore(owner)));
    }

    public static void Restore(Window owner)
    {
        if (!owner.IsVisible) return;
        owner.IsEnabled = true;
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate();
        owner.Focus();
    }
}
