using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace CookieLuncher.Core;

/// <summary>浏览器采集失败（Playwright 缺失 / 浏览器启动失败 / CDP 错误）。</summary>
public sealed class CookieCaptureException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public CookieCaptureException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public CookieCaptureException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 浏览器手动登录 Cookie 采集（仅负责采集，不做持久化加密存储）。
/// <para>
/// 与 Python 版 <c>core/cookie_capture.py</c> 一致：使用独立临时 user-data-dir，
/// 浏览器必须可见（供用户手动登录 / 验证码），采集结果不做域名过滤；
/// 结束时无论成功失败都清理进程与临时目录。
/// </para>
/// </summary>
public static class CookieCapture
{
    /// <summary>采集专用临时目录前缀（与浏览流程区分，禁止共用 profile）。</summary>
    public const string TempDirPrefix = "cookie_capture_";

    /// <summary>页面初始加载超时。</summary>
    private const float GotoTimeoutMs = 60_000;

    /// <summary>残留进程查杀等待上限。</summary>
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>临时目录删除重试超时。</summary>
    private static readonly TimeSpan RemoveRetryTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 启动可见浏览器供用户手动登录，采集并返回规范化后的 Cookie 列表。
    /// </summary>
    /// <param name="url">用户输入的登录 URL（无协议时自动补 https://）。</param>
    /// <param name="config">完整配置（browser_backend 决定 thorium / system 后端）。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="loginCompleted">用户点击「完成采集」后完成的信号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>采集到的 Cookie 列表（全部保留）。</returns>
    /// <exception cref="CookieCaptureException">Playwright 不可用、浏览器启动失败或 CDP 错误。</exception>
    public static async Task<List<JsonObject>> CaptureAsync(
        string url,
        AppConfig config,
        Action<string> log,
        Task loginCompleted,
        CancellationToken cancellationToken = default)
    {
        string executable;
        try
        {
            executable = BrowserLauncher.ResolveExecutable(config);
        }
        catch (Exception ex)
        {
            throw new CookieCaptureException($"无法定位浏览器可执行文件: {ex.Message}", ex);
        }

        var normalizedUrl = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
        var userDataDir = Path.Combine(Path.GetTempPath(), TempDirPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDir);

        try
        {
            return await RunCaptureAsync(userDataDir, executable, normalizedUrl, log, loginCompleted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CookieCaptureException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // CDP 协议错误 / 浏览器启动失败 / 上下文已断开等统一归为采集失败
            throw new CookieCaptureException($"浏览器采集失败（CDP 或浏览器错误）: {ex.Message}", ex);
        }
        finally
        {
            ProcessUtil.KillProcessesUsingPath(userDataDir, log, KillWaitTimeout);
            ProcessUtil.RemoveDirectoryWithRetry(userDataDir, RemoveRetryTimeout, log);
            log("采集临时目录与进程已清理");
        }
    }

    private static async Task<List<JsonObject>> RunCaptureAsync(
        string userDataDir,
        string executable,
        string normalizedUrl,
        Action<string> log,
        Task loginCompleted,
        CancellationToken cancellationToken)
    {
        IPlaywright playwright;
        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new CookieCaptureException(
                "Playwright 驱动初始化失败，请确认程序目录未被安全软件隔离，或重新执行 dotnet build。", ex);
        }

        using (playwright)
        {
            IBrowserContext context;
            try
            {
                context = await playwright.Chromium.LaunchPersistentContextAsync(
                    userDataDir,
                    new BrowserTypeLaunchPersistentContextOptions
                    {
                        ExecutablePath = executable,
                        Headless = false, // 必须可见，供用户手动登录 / 验证码
                        Args = BrowserLauncher.FixedLaunchArgs,
                        ViewportSize = ViewportSize.NoViewport,
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new CookieCaptureException($"浏览器采集失败（CDP 或浏览器错误）: {ex.Message}", ex);
            }

            try
            {
                var page = context.Pages.Count > 0
                    ? context.Pages[0]
                    : await context.NewPageAsync().ConfigureAwait(false);

                await page.GotoAsync(
                    normalizedUrl,
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = GotoTimeoutMs,
                    }).ConfigureAwait(false);
                log($"采集浏览器已打开: {normalizedUrl}");

                // 等待界面上的「完成采集」信号
                await WaitForLoginAsync(loginCompleted, cancellationToken).ConfigureAwait(false);

                var playwrightCookies = await context.CookiesAsync().ConfigureAwait(false);
                var (cookies, dropped) = PlaywrightCookieConverter.NormalizeCaptured(playwrightCookies);
                foreach (var message in dropped)
                {
                    log($"采集剔除: {message}");
                }

                log($"采集到 Cookie: {cookies.Count} 条（全部保留），剔除 {dropped.Count} 条");
                return cookies;
            }
            finally
            {
                try
                {
                    await context.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 浏览器可能已被用户关闭
                }
            }
        }
    }

    private static async Task WaitForLoginAsync(Task loginCompleted, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult());

        var finished = await Task.WhenAny(loginCompleted, cancelled.Task).ConfigureAwait(false);
        if (finished != loginCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
