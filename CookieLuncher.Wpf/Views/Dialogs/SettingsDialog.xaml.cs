using System.Globalization;
using System.Windows;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Dialogs;

/// <summary>程序设置对话框（写入 config.json，与 Python 版共用）。</summary>
public partial class SettingsDialog : Window
{
    private readonly AppSession _session;

    /// <summary>初始化对话框。</summary>
    /// <param name="session">运行期上下文。</param>
    public SettingsDialog(AppSession session)
    {
        InitializeComponent();

        _session = session;
        var config = session.Config;

        ThoriumRadio.IsChecked = config.BrowserBackend != AppConfig.BackendSystem;
        SystemRadio.IsChecked = config.BrowserBackend == AppConfig.BackendSystem;
        HeadlessInput.IsChecked = config.Headless;
        WindowSizeInput.Text = config.WindowSize;
        RetentionInput.Text = config.LogRetentionDays.ToString(CultureInfo.InvariantCulture);

        LogLevelInput.ItemsSource = AppConfig.ValidLogLevels;
        LogLevelInput.SelectedItem = AppConfig.ValidLogLevels.Contains(config.LogLevel)
            ? config.LogLevel
            : "INFO";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RetentionInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention)
            || retention <= 0)
        {
            ShowError("日志保留天数必须是大于 0 的整数。");
            return;
        }

        var windowSize = WindowSizeInput.Text.Trim();
        if (windowSize.Length == 0)
        {
            ShowError("窗口尺寸不能为空，可填写 maximized 或 1280x720。");
            return;
        }

        var config = _session.Config;
        config.BrowserBackend = SystemRadio.IsChecked == true ? AppConfig.BackendSystem : AppConfig.BackendThorium;
        config.Headless = HeadlessInput.IsChecked == true;
        config.WindowSize = windowSize;
        config.LogLevel = LogLevelInput.SelectedItem as string ?? "INFO";
        config.LogRetentionDays = retention;

        try
        {
            config.Save();
        }
        catch (ConfigException ex)
        {
            ShowError(ex.Message);
            return;
        }

        AppLogger.Setup(AppPaths.LogDir, config.LogLevel);
        AppLogger.Info($"设置已保存（后端={config.BrowserBackend}，无头={config.Headless}，窗口={config.WindowSize}）");
        _session.NotifyConfigChanged();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
