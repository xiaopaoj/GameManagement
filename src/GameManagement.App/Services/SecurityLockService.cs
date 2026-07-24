using System.Diagnostics;
using System.Windows;

namespace GameManagement.Services;

public static class SecurityLockService
{
    public static void RestartIntoLockedMode()
    {
        if (!MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile)) return;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序文件。");
        EncryptedIconService.ClearMemoryCache();
        MasterKeyService.ClearSession();
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
