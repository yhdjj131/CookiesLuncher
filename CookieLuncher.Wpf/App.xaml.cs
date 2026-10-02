using System.Windows;
using System.Windows.Threading;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;
using CookieLuncher.Views;
using CookieLuncher.Views.Dialogs;

namespace CookieLuncher;

/// <summary>应用程序入口。</summary>
public partial class App : Application
{
    private AppSession? _session;

    /// <summary>
    /// 启动流程：解析数据目录 → 读取配置 → 初始化日志 → 解锁 → 显示主窗口。
    /// </summary>
    /// <param name="e">启动参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppConfig config;
        try
        {
            AppPaths.Initialize(AppPaths.ParseRootArgument(e.Args));
            config = AppConfig.Load();
        }
        catch (ConfigException ex)
        {
            // 此时日志尚未初始化，直接弹窗提示
            MessageDialog.Error(null, "启动失败", ex.Message);
            Shutdown(1);
            return;
        }

        AppLogger.Setup(AppPaths.LogDir, config.LogLevel);
        AppLogger.Info($"程序启动（数据目录: {AppPaths.Root}）");

        try
        {
            AppLogger.CleanupOldLogs(config.LogRetentionDays);
        }
        catch (Exception ex)
        {
            // 清理失败不影响启动
            AppLogger.Warning($"清理过期日志失败: {ex.Message}");
        }

        // 清理上次异常退出遗留的临时目录
        BrowserLauncher.CleanupStaleTempDirs(AppLogger.Debug);

        var auth = new AuthWindow(config);
        if (auth.ShowDialog() != true || auth.Key is null)
        {
            AppLogger.Info("用户未解锁，程序退出");
            AppLogger.Shutdown();
            Shutdown(0);
            return;
        }

        _session = new AppSession(config, auth.Key);

        var main = new MainWindow(_session, new WpfDialogs());
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    /// <summary>退出清理。</summary>
    /// <param name="e">退出参数。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("程序退出");
        AppLogger.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("程序运行出现未处理异常", e.Exception);
        MessageDialog.Error(
            MainWindow,
            "程序异常",
            $"{e.Exception.Message}\n\n详细信息已写入日志：{AppPaths.LogDir}");
        e.Handled = true;
    }
}
