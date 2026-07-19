using System.Windows;
using GameManagement.Services;

namespace GameManagement;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            AppLogger.Initialize();
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"软件初始化失败：{ex.Message}\n请确认程序所在目录具有写入权限。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
