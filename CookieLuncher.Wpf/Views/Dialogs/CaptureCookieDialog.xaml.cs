using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using CookieLuncher.Core;
using CookieLuncher.ViewModels;

namespace CookieLuncher.Views.Dialogs;

/// <summary>
/// 浏览器手动登录采集对话框：负责打开浏览器、等待用户点击「完成采集」、取回 Cookie。
/// </summary>
public partial class CaptureCookieDialog : Window
{
    private readonly AppSession _session;

    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _loginCompleted;
    private Task<List<JsonObject>>? _captureTask;
    private bool _closing;
    private bool _succeeded;

    /// <summary>初始化对话框。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <param name="suggestedSite">预填域名。</param>
    public CaptureCookieDialog(AppSession session, string? suggestedSite = null)
    {
        InitializeComponent();

        _session = session;
        if (!string.IsNullOrWhiteSpace(suggestedSite))
        {
            UrlInput.Text = suggestedSite;
            SiteInput.Text = suggestedSite;
        }

        Loaded += (_, _) => UrlInput.Focus();
    }

    /// <summary>采集结果。</summary>
    public CaptureCookieOutput Result { get; private set; }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_succeeded && _captureTask is { IsCompleted: false })
        {
            // 采集进行中：先取消并等待清理完成，再关闭窗口
            e.Cancel = true;
            _closing = true;
            _cancellation?.Cancel();
            _ = CloseWhenCaptureDoneAsync();
            return;
        }

        base.OnClosing(e);
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        var rawUrl = UrlInput.Text.Trim();
        if (rawUrl.Length == 0)
        {
            ShowError("请输入登录 URL。");
            return;
        }

        string site;
        try
        {
            var rawSite = SiteInput.Text.Trim();
            site = CookieParser.NormalizeSite(rawSite.Length > 0 ? rawSite : rawUrl);
        }
        catch (CookieParseException ex)
        {
            ShowError(ex.Message);
            return;
        }

        SiteInput.Text = site;

        _cancellation = new CancellationTokenSource();
        _loginCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SetCapturing(true);
        StatusText.Text = "正在打开浏览器，请稍候…";

        var config = _session.Config;
        var cancellationToken = _cancellation.Token;
        var loginTask = _loginCompleted.Task;

        _captureTask = Task.Run(
            () => CookieCapture.CaptureAsync(
                rawUrl,
                config,
                message => RunOnUi(() => StatusText.Text = message),
                loginTask,
                cancellationToken),
            cancellationToken);

        try
        {
            var cookies = await _captureTask;
            if (_closing)
            {
                return;
            }

            if (cookies.Count == 0)
            {
                ShowError("未采集到任何有效 Cookie，已拒绝保存。");
                AppLogger.Warning("采集返回空 Cookie 列表，拒绝保存");
                SetCapturing(false);
                return;
            }

            AppLogger.Info($"采集到 {cookies.Count} 条 Cookie（{CookieParser.Summarize(cookies)}）");
            Result = new CaptureCookieOutput(site, cookies);
            _succeeded = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("用户取消采集");
            SetCapturing(false);
        }
        catch (CookieCaptureException ex)
        {
            ShowError(ex.Message);
            AppLogger.Error($"采集 Cookie 失败: {ex.Message}");
            SetCapturing(false);
        }
        catch (Exception ex)
        {
            ShowError($"采集过程出现异常: {ex.Message}");
            AppLogger.Error("采集 Cookie 异常", ex);
            SetCapturing(false);
        }
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        FinishButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在读取 Cookie 并清理浏览器…";
        _loginCompleted?.TrySetResult();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_captureTask is { IsCompleted: false })
        {
            StatusText.Text = "正在取消并清理浏览器…";
            _cancellation?.Cancel();
            Close();
            return;
        }

        DialogResult = false;
    }

    private async Task CloseWhenCaptureDoneAsync()
    {
        try
        {
            if (_captureTask is not null)
            {
                await _captureTask;
            }
        }
        catch (Exception)
        {
            // 关闭流程中的异常无需上报
        }

        _closing = false;
        Close();
    }

    private void SetCapturing(bool capturing)
    {
        UrlInput.IsEnabled = !capturing;
        SiteInput.IsEnabled = !capturing;
        StartButton.Visibility = capturing ? Visibility.Collapsed : Visibility.Visible;
        FinishButton.Visibility = capturing ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        CancelButton.Content = capturing ? "取消采集" : "关闭";

        if (capturing)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            StatusText.Text = "浏览器已打开，请在浏览器中完成登录，然后点击【完成采集】。";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }
}
