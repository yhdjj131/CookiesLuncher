using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Microsoft.Playwright;

namespace CookieLuncher.Core;

/// <summary>浏览器启动或操作失败。</summary>
public sealed class BrowserException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public BrowserException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public BrowserException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 双后端（thorium / system）浏览器启动、Cookie 注入与痕迹清理，
/// 与 Python 版 <c>core/browser.py</c> 的方案 C 保持一致：
/// <list type="number">
///   <item>Playwright 以 <c>launch_persistent_context</c> 绑定临时 user-data-dir 并注入 Cookie，
///         由 Chromium 自身加密落盘（登录态可靠）；</item>
///   <item>Page 加载完成后退出 Playwright 上下文，改用 <see cref="Process"/> 以同一 profile
///         直接启动浏览器独立运行；</item>
///   <item>用户关闭窗口后，等待进程树退出并删除临时目录。</item>
/// </list>
/// </summary>
public static class BrowserLauncher
{
    /// <summary>thorium 后端。</summary>
    public const string BackendThorium = "thorium";

    /// <summary>system 后端。</summary>
    public const string BackendSystem = "system";

    /// <summary>临时用户数据目录前缀。</summary>
    public const string TempDirPrefix = "cookie_launcher_";

    /// <summary>无头模式下的等待时间（秒）。</summary>
    public const double HeadlessGraceSeconds = 5.0;

    /// <summary>所有后端共用的固定启动参数（thorium / system 完全一致）。</summary>
    public static readonly string[] FixedLaunchArgs =
    [
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-quic",
        "--disable-background-networking",
        "--disable-sync",
        "--enable-gpu-rasterization",
        "--ignore-gpu-blocklist",
        "--disable-blink-features=AutomationControlled",
        // B 站播放兼容：关闭站点隔离/分区 Cookie/弹窗拦截/沙箱限制，允许自动播放
        "--disable-features=IsolateOrigins,site-per-process,PartitionedCookies",
        "--disable-popup-blocking",
        "--disable-site-isolation-trials",
        "--no-sandbox",
        "--disable-dev-shm-usage",
        "--autoplay-policy=no-user-gesture-required",
    ];

    /// <summary>system 后端探测的本机浏览器常见路径。</summary>
    private static readonly string[] SystemBrowserCandidates =
    [
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ];

    /// <summary>注册表 App Paths 探测键（按优先级排列）。</summary>
    private static readonly (RegistryKey Hive, string Path)[] SystemRegistryKeys =
    [
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
    ];

    /// <summary>Playwright 注入 Cookie 后页面加载完成的稳定等待。</summary>
    private static readonly TimeSpan SeedLoadStable = TimeSpan.FromSeconds(1.5);

    /// <summary>Playwright 打开页面（含浏览器冷启动）的超时。</summary>
    private const float SeedGotoTimeoutMs = 60_000;

    /// <summary>等待进程树退出的总超时。</summary>
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(45);

    /// <summary>删除临时目录的重试超时。</summary>
    private static readonly TimeSpan RemoveDirTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 程序启动阶段调用：清理上次异常退出遗留的临时目录。
    /// </summary>
    /// <param name="log">日志回调。</param>
    /// <returns>清理的目录数量。</returns>
    public static int CleanupStaleTempDirs(Action<string>? log = null)
        => ProcessUtil.CleanupStaleTempDirs(TempDirPrefix, log)
            + ProcessUtil.CleanupStaleTempDirs(CookieCapture.TempDirPrefix, log);

    /// <summary>根据 <c>browser_backend</c> 返回浏览器可执行文件路径。</summary>
    /// <param name="config">完整配置。</param>
    /// <returns>可执行文件路径。</returns>
    /// <exception cref="BrowserException">thorium 文件缺失，或 system 未探测到本机浏览器。</exception>
    public static string ResolveExecutable(AppConfig config)
    {
        if (config.BrowserBackend == BackendSystem)
        {
            var found = ProbeSystemBrowser();
            if (found is null)
            {
                throw new BrowserException(
                    "未检测到本机 Edge/Chrome（system 后端依赖本机浏览器，不是离线自包含）。"
                    + "请安装 Microsoft Edge 或 Google Chrome 后重试，或把 browser_backend 改回 thorium。");
            }

            return found;
        }

        var thorium = AppPaths.ThoriumExePath;
        if (!File.Exists(thorium))
        {
            throw new BrowserException(
                $"未找到内置 Thorium 浏览器（期望路径：{thorium}，"
                + "请把 Thorium 解压内容放到数据目录下的 external_browsers\\thorium-win64\\ 中）。");
        }

        return thorium;
    }

    /// <summary>探测本机 Edge / Chrome 的可执行文件。</summary>
    /// <returns>找到的可执行文件路径；未找到返回 null。</returns>
    public static string? ProbeSystemBrowser()
    {
        foreach (var (hive, path) in SystemRegistryKeys)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetValue(string.Empty) is string value && File.Exists(value))
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // 无权读取该注册表项，继续下一个
            }
        }

        foreach (var candidate in SystemBrowserCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 启动浏览器（Playwright 注入 Cookie → 浏览器独立运行），关闭后清理全部痕迹。
    /// </summary>
    /// <param name="site">网站域名。</param>
    /// <param name="cookies">该网站的 Cookie 列表。</param>
    /// <param name="config">完整配置。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="notify">面向界面的提示回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="BrowserException">浏览器启动或 Cookie 注入失败。</exception>
    public static async Task RunSessionAsync(
        string site,
        IReadOnlyList<JsonObject> cookies,
        AppConfig config,
        Action<string> log,
        Action<string>? notify = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSite = CookieParser.NormalizeSite(site);
        var valid = cookies
            .Where(cookie => Json.Str(cookie, "name").Length > 0
                && (cookie["domain"] is not null || cookie["host"] is not null))
            .ToList();
        if (valid.Count == 0)
        {
            throw new BrowserException("没有可用的 Cookie（缺少 name 或 domain），无法启动浏览器");
        }

        var executable = ResolveExecutable(config);

        if (config.BrowserBackend == BackendSystem)
        {
            notify?.Invoke(
                $"system 模式使用本机浏览器（{executable}），依赖本机登录态，不是离线自包含。"
                + "会员/DRM 视频请确保已在对应浏览器登录。");
            log($"system 后端使用本机浏览器: {executable}");
        }
        else
        {
            log($"thorium 后端使用内置浏览器: {executable}");
        }

        var url = $"https://{normalizedSite}";
        var userDataDir = CreateTempDirectory(TempDirPrefix);
        Process? process = null;
        var cleaned = false;

        try
        {
            // 阶段1：Playwright 注入 Cookie 并落盘（profile 由 Chromium 原生写库）
            await SeedProfileAsync(userDataDir, valid, executable, url, log, cancellationToken)
                .ConfigureAwait(false);

            // 阶段2：同一 profile 由独立进程启动，浏览器独立运行
            var arguments = BuildLaunchArguments(userDataDir, config, url);
            log($"启动命令: {executable} {string.Join(' ', arguments)}");
            process = StartBrowser(executable, arguments);
            log($"浏览器已启动 (pid={process.Id})");

            if (config.Headless)
            {
                log($"无头模式：{HeadlessGraceSeconds:F0} 秒后自动关闭浏览器");
                await Task.Delay(TimeSpan.FromSeconds(HeadlessGraceSeconds), cancellationToken)
                    .ConfigureAwait(false);
                TryTerminate(process);
                WaitForExit(process, TimeSpan.FromSeconds(15));
            }
            else
            {
                log("等待用户关闭浏览器...");
                await Task.Run(() => WaitForExit(process), cancellationToken).ConfigureAwait(false);
            }

            log("浏览器已关闭");

            // 进程树全部退出后删除临时目录
            ProcessUtil.WaitProcessTreeExit(process.Id, ExitWaitTimeout, log);
            ProcessUtil.RemoveDirectoryWithRetry(userDataDir, RemoveDirTimeout, log);
            cleaned = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BrowserException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BrowserException($"浏览器启动失败，请检查浏览器是否完整: {ex.Message}", ex);
        }
        finally
        {
            if (!cleaned)
            {
                // 异常或取消时也要尽力清理，失败不阻塞退出
                if (process is not null)
                {
                    TryTerminate(process);
                    try
                    {
                        ProcessUtil.WaitProcessTreeExit(process.Id, TimeSpan.FromSeconds(10), log);
                    }
                    catch (Exception)
                    {
                        // 忽略
                    }
                }

                ProcessUtil.RemoveDirectoryWithRetry(userDataDir, TimeSpan.FromSeconds(5), log);
                log("已执行应急清理");
            }
        }
    }

    /// <summary>阶段1：用 Playwright 启动 persistent_context，注入 Cookie 后关闭。</summary>
    private static async Task SeedProfileAsync(
        string userDataDir,
        IReadOnlyList<JsonObject> cookies,
        string executable,
        string url,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var playwrightCookies = cookies
            .Select(PlaywrightCookieConverter.ToPlaywrightCookie)
            .OfType<Cookie>()
            .ToList();
        if (playwrightCookies.Count == 0)
        {
            throw new BrowserException("没有可注入的 Cookie（缺少 name / value / domain）");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IPlaywright playwright;
        try
        {
            playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new BrowserException(
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
                        Headless = true,
                        Args = FixedLaunchArgs,
                        ViewportSize = ViewportSize.NoViewport,
                    }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new BrowserException($"Playwright Cookie 注入失败: {ex.Message}", ex);
            }

            try
            {
                try
                {
                    await context.AddCookiesAsync(playwrightCookies).ConfigureAwait(false);
                    log($"已用 Playwright 注入 Cookie: {playwrightCookies.Count} 条");
                }
                catch (Exception ex)
                {
                    throw new BrowserException($"Playwright Cookie 注入失败: {ex.Message}", ex);
                }

                // 首页预加载：仅用于确认页面可访问，失败不影响已注入的 Cookie，
                // 因此只记录告警后继续启动（离线 / 网络抖动时浏览器仍可用）。
                try
                {
                    var page = await context.NewPageAsync().ConfigureAwait(false);
                    await page.GotoAsync(
                        url,
                        new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = SeedGotoTimeoutMs,
                        }).ConfigureAwait(false);
                    await page.WaitForTimeoutAsync((float)SeedLoadStable.TotalMilliseconds).ConfigureAwait(false);
                    log($"页面加载完成，Cookie 已落盘: {url}");
                }
                catch (PlaywrightException ex)
                {
                    log($"首页预加载失败（Cookie 已注入，浏览器将继续启动）: {ex.Message}");
                }
            }
            finally
            {
                try
                {
                    await context.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 浏览器可能已自行退出
                }
            }
        }
    }

    /// <summary>组装浏览器启动参数（thorium / system 共用同一套逻辑）。</summary>
    private static List<string> BuildLaunchArguments(string userDataDir, AppConfig config, string url)
    {
        var arguments = new List<string>(FixedLaunchArgs) { "--user-data-dir=" + userDataDir };

        if (config.Headless)
        {
            arguments.Add("--headless=new");
        }

        var size = config.ParseWindowSize();
        arguments.Add(size is null
            ? "--start-maximized"
            : $"--window-size={size.Value.Width},{size.Value.Height}");

        if (url.Length > 0)
        {
            arguments.Add(url);
        }

        return arguments;
    }

    private static Process StartBrowser(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo)
                ?? throw new BrowserException("浏览器启动失败，请检查浏览器是否完整");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BrowserException($"浏览器启动失败，请检查浏览器是否完整: {ex.Message}", ex);
        }
    }

    private static void WaitForExit(Process process, TimeSpan? timeout = null)
    {
        try
        {
            if (timeout is null)
            {
                process.WaitForExit();
            }
            else
            {
                process.WaitForExit(timeout.Value);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // 已退出或无权操作
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
