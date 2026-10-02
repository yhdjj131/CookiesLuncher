using System.Windows;
using System.Windows.Controls;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;
using CookieLuncher.Views.Pages;

namespace CookieLuncher.Views;

/// <summary>主窗口：左侧导航 + 内容区 + 底部状态栏。</summary>
public partial class MainWindow : Window
{
    private readonly AppSession _session;
    private readonly IUserDialogs _dialogs;
    private readonly LaunchViewModel _launch;
    private readonly ManageViewModel _manage;
    private readonly ExportViewModel _export;
    private readonly UserControl[] _pages;

    /// <summary>初始化主窗口。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <param name="dialogs">交互对话框。</param>
    public MainWindow(AppSession session, IUserDialogs dialogs)
    {
        InitializeComponent();

        _session = session;
        _dialogs = dialogs;

        _launch = new LaunchViewModel(session, dialogs);
        _manage = new ManageViewModel(session, dialogs);
        _export = new ExportViewModel(session, dialogs);

        _pages =
        [
            new LaunchPage(_launch),
            new ManagePage(_manage),
            new ExportPage(_export),
        ];

        // 「启动浏览器」页为空时引导到「Cookie 管理」
        _launch.ManageRequested += () => SelectPage(1);

        DataDirText.Text = AppPaths.Root;
        UpdateBackendText();
        AppLogger.WarningOrAbove += OnLoggerWarning;

        Loaded += (_, _) => SelectPage(0);
        Closed += (_, _) => AppLogger.WarningOrAbove -= OnLoggerWarning;
    }

    /// <summary>显示指定页面（供外部调用，例如错误提示后跳转）。</summary>
    /// <param name="index">页面索引。</param>
    public void SelectPage(int index)
    {
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        NavList.SelectedIndex = index;
        if (PageHost.Content != _pages[index])
        {
            PageHost.Content = _pages[index];
            RefreshPage(index);
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = NavList.SelectedIndex;
        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        PageHost.Content = _pages[index];
        RefreshPage(index);
    }

    private void RefreshPage(int index)
    {
        switch (index)
        {
            case 0:
                _launch.Reload();
                break;
            case 1:
                _manage.Reload();
                break;
            case 2:
                _export.Reload();
                break;
        }
    }

    private void UpdateBackendText()
    {
        var backend = _session.Config.BrowserBackend;
        BackendText.Text = backend == AppConfig.BackendSystem ? "后端：本机 Edge / Chrome" : "后端：内置 Thorium";
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_dialogs.PromptSettings(_session))
        {
            return;
        }

        UpdateBackendText();
        _launch.Reload();
        StatusMessage.Text = "设置已保存。";
    }

    private void OnOpenDataDirClick(object sender, RoutedEventArgs e)
        => _dialogs.OpenDirectory(AppPaths.Root);

    private void OnLoggerWarning(string line)
    {
        if (Dispatcher.CheckAccess())
        {
            StatusMessage.Text = line;
        }
        else
        {
            Dispatcher.Invoke(() => StatusMessage.Text = line);
        }
    }
}
