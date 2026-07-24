using System.IO;
using System.Windows;
using System.Windows.Threading;
using GameManagement.Services;
using GameManagement.Models;

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
            var passwordRequired = MasterKeyService.IsPasswordRequired(AppPaths.SecurityConfigFile);
            if (e.Args.Any(argument => argument.Equals("--scheduled-backup", StringComparison.OrdinalIgnoreCase)) && passwordRequired)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                await BackupNotificationService.ShowAsync("游戏管理软件每日备份", "安全模式尚未解锁，本次计划备份已跳过；下次解锁后可手动补执行。", System.Windows.Forms.ToolTipIcon.Warning);
                Shutdown(0);
                return;
            }
            if (passwordRequired && new SecurityWrapperWindow().ShowDialog() != true) { Shutdown(0); return; }
            var startupStore = new StateStore();
            var startupState = startupStore.Load();
            ThemeService.Apply(startupState.UiSettings.ThemeName);
            if (e.Args.Any(argument => argument.Equals("--scheduled-backup", StringComparison.OrdinalIgnoreCase)))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var store = startupStore;
                var state = startupState;
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
            PromptLegacyDatabaseCleanup();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"软件初始化失败：{ex.Message}\n请确认程序所在目录具有写入权限。", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MasterKeyService.ClearSession();
        base.OnExit(e);
    }

    private static void PromptLegacyDatabaseCleanup()
    {
        if (!File.Exists(AppPaths.LegacyStateFile) || !File.Exists(AppPaths.StateFile)) return;
        var result = MessageBox.Show("现有明文数据库已经完成加密迁移并通过回读校验。\n\n是否永久删除旧的明文数据库？\n\n选择“否”会保留旧文件，但旧文件中的游戏名称、路径等信息仍可被直接查看，软件下次启动会继续提示。", "清理未加密历史数据", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(AppPaths.LegacyStateFile);
            AppLogger.Info("用户确认后已永久删除迁移完成的旧明文数据库");
        }
        catch (Exception ex)
        {
            AppLogger.Error("旧明文数据库删除失败", ex);
            MessageBox.Show($"旧明文数据库未能删除：{ex.Message}", "清理失败", MessageBoxButton.OK, MessageBoxImage.Error);
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
