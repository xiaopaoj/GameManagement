using System.Windows;
using System.Windows.Threading;
using GameManagement.Services;

namespace GameManagement;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            AppPaths.EnsureDirectories();
            AppLogger.Initialize();
            base.OnStartup(e);
            if (e.Args.Any(argument => argument.Equals("--scheduled-backup", StringComparison.OrdinalIgnoreCase)))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var store = new StateStore();
                var state = store.Load();
                BackupExecutionResult result;
                try
                {
                    result = await ExternalBackupService.CreateScheduledFullBackupAsync(state);
                    MergeAndSaveBackupState(store, state);
                }
                catch (Exception ex)
                {
                    state.BackupSettings.LastRunAt = DateTime.Now;
                    state.BackupSettings.LastStatus = "失败";
                    state.BackupSettings.LastError = ex.Message;
                    MergeAndSaveBackupState(store, state);
                    AppLogger.Error("每日计划备份执行失败", ex);
                    result = new BackupExecutionResult(null, "失败", ex.Message);
                }
                var icon = result.WaitingForTarget || result.Status == "失败" ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info;
                await BackupNotificationService.ShowAsync("游戏管理软件每日备份", result.Message, icon);
                Shutdown(result.Status == "失败" ? 1 : 0);
                return;
            }
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"软件初始化失败：{ex.Message}\n请确认程序所在目录具有写入权限。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLogUnhandledException("界面线程发生未处理异常", e.Exception);
        e.Handled = true;
        MessageBox.Show($"软件发生未处理错误并将安全退出：{e.Exception.Message}\n详细信息已写入运行日志。", "未处理错误", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) TryLogUnhandledException("后台线程发生未处理异常", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryLogUnhandledException("异步任务发生未观察异常", e.Exception);
        e.SetObserved();
    }

    private static void TryLogUnhandledException(string message, Exception exception)
    {
        try { AppLogger.Error(message, exception); }
        catch { }
    }

    private static void MergeAndSaveBackupState(StateStore store, Models.AppState backupState)
    {
        var latest = store.Load();
        var merged = latest.ExternalBackups.ToDictionary(item => item.Id);
        foreach (var item in backupState.ExternalBackups) merged[item.Id] = item;
        latest.ExternalBackups = merged.Values.OrderBy(item => item.CreatedAt).ToList();
        latest.BackupSettings = backupState.BackupSettings;
        store.Save(latest);
    }
}
