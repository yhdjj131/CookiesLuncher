using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CookieLuncher.Core;

namespace CookieLuncher.ViewModels;

/// <summary>「启动浏览器」页面的视图模型。</summary>
public sealed class LaunchViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly IUserDialogs _dialogs;
    private bool _isBusy;
    private string _status = string.Empty;

    /// <summary>初始化视图模型。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <param name="dialogs">交互对话框。</param>
    public LaunchViewModel(AppSession session, IUserDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;

        RefreshCommand = new RelayCommand(Reload, () => !IsBusy);
        GoToManageCommand = new RelayCommand(() => ManageRequested?.Invoke());

        _session.ConfigChanged += (_, _) => Reload();
    }

    /// <summary>请求切换到「Cookie 管理」页面。</summary>
    public event Action? ManageRequested;

    /// <summary>已配置的网站列表。</summary>
    public ObservableCollection<SiteItemViewModel> Sites { get; } = [];

    /// <summary>刷新命令。</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>跳转到「Cookie 管理」命令。</summary>
    public ICommand GoToManageCommand { get; }

    /// <summary>是否正在启动浏览器。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    /// <summary>是否空闲。</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>页面状态文案。</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    /// <summary>是否存在状态文案。</summary>
    public bool HasStatus => Status.Length > 0;

    /// <summary>当前后端是否为 system（需要额外提示）。</summary>
    public bool IsSystemBackend => _session.Config.BrowserBackend == AppConfig.BackendSystem;

    /// <summary>后端提示文案。</summary>
    public string BackendNotice =>
        "当前使用 system 后端（本机 Edge/Chrome）：依赖本机登录态，不是离线自包含。"
        + "会员 / DRM 视频请确保已在该浏览器登录。";

    /// <summary>是否没有任何网站。</summary>
    public bool IsEmpty => Sites.Count == 0 && !IsBusy;

    /// <summary>重新读取网站列表。</summary>
    public void Reload()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var entries = _session.Store().ListSites();
            Sites.Clear();
            foreach (var (site, count) in entries)
            {
                Sites.Add(new SiteItemViewModel(
                    site,
                    count,
                    new AsyncRelayCommand(() => LaunchAsync(site), () => IsIdle)));
            }

            Status = string.Empty;
        }
        catch (DecryptException)
        {
            Sites.Clear();
            Status = "密码错误或数据损坏，无法读取 Cookie 数据。";
            AppLogger.Error("读取网站列表失败：cookies.enc 解密失败");
        }
        catch (StorageException ex)
        {
            Sites.Clear();
            Status = ex.Message;
            AppLogger.Error($"读取网站列表失败: {ex.Message}");
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSystemBackend));
    }

    private async Task LaunchAsync(string site)
    {
        List<System.Text.Json.Nodes.JsonObject> cookies;
        try
        {
            cookies = _session.Store().Get(site);
        }
        catch (DecryptException)
        {
            _dialogs.Error("启动失败", "密码错误或数据损坏，无法读取 Cookie 数据。");
            AppLogger.Error($"启动网站 {site} 失败：cookies.enc 解密失败");
            return;
        }
        catch (StorageException ex)
        {
            _dialogs.Error("启动失败", ex.Message);
            AppLogger.Error($"启动网站 {site} 失败: {ex.Message}");
            return;
        }

        if (cookies.Count == 0)
        {
            _dialogs.Warn("无法启动", $"网站 {site} 没有可用的 Cookie。");
            AppLogger.Warning($"网站 {site} 没有可用的 Cookie");
            return;
        }

        IsBusy = true;
        Status = $"正在启动 {site} …";
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            var config = _session.Config;
            await Task.Run(async () =>
                await BrowserLauncher.RunSessionAsync(
                    site,
                    cookies,
                    config,
                    log: message => RunOnUi(() => Status = message),
                    notify: message => RunOnUi(() => _dialogs.Info("提示", message))).ConfigureAwait(false)).ConfigureAwait(true);

            AppLogger.Info($"网站 {site} 的浏览器已关闭，Cookie 与临时数据已清理");
            Status = "浏览器已关闭，Cookie 与临时数据已清理。";
        }
        catch (BrowserException ex)
        {
            _dialogs.Error("启动失败", ex.Message);
            AppLogger.Error($"浏览器会话失败: {ex.Message}");
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            _dialogs.Error("启动失败", $"浏览器运行时出现异常: {ex.Message}");
            AppLogger.Error("浏览器运行时异常", ex);
            Status = $"浏览器运行时出现异常: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
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
