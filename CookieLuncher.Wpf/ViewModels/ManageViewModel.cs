using System.Collections.ObjectModel;
using System.Windows.Input;
using CookieLuncher.Core;

namespace CookieLuncher.ViewModels;

/// <summary>「Cookie 管理」页面的视图模型。</summary>
public sealed class ManageViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly IUserDialogs _dialogs;

    private SiteItemViewModel? _selected;
    private string _detailSummary = "未选择网站";
    private string _cookieNames = "（无）";
    private string _status = string.Empty;
    private bool _isBusy;

    /// <summary>初始化视图模型。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <param name="dialogs">交互对话框。</param>
    public ManageViewModel(AppSession session, IUserDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;

        RefreshCommand = new RelayCommand(Reload, () => !IsBusy);
        PasteImportCommand = new RelayCommand(ImportPasted, () => !IsBusy);
        CaptureCommand = new RelayCommand(Capture, () => !IsBusy);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null && !IsBusy);
        ChangePasswordCommand = new RelayCommand(ChangePassword, () => !IsBusy);

        _session.ConfigChanged += (_, _) => Reload();
    }

    /// <summary>已配置的网站列表。</summary>
    public ObservableCollection<SiteItemViewModel> Sites { get; } = [];

    /// <summary>刷新命令。</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>粘贴导入命令。</summary>
    public ICommand PasteImportCommand { get; }

    /// <summary>浏览器采集命令。</summary>
    public ICommand CaptureCommand { get; }

    /// <summary>删除命令。</summary>
    public ICommand DeleteCommand { get; }

    /// <summary>修改密码命令。</summary>
    public ICommand ChangePasswordCommand { get; }

    /// <summary>当前选中的网站。</summary>
    public SiteItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                UpdateDetail();
            }
        }
    }

    /// <summary>是否存在选中网站。</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>列表是否为空。</summary>
    public bool IsEmpty => Sites.Count == 0;

    /// <summary>是否正在执行耗时操作（采集）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>详情区标题。</summary>
    public string DetailSummary
    {
        get => _detailSummary;
        private set => SetProperty(ref _detailSummary, value);
    }

    /// <summary>详情区的 Cookie 名称列表（只显示名称，不显示值）。</summary>
    public string CookieNames
    {
        get => _cookieNames;
        private set => SetProperty(ref _cookieNames, value);
    }

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

    /// <summary>重新读取网站列表。</summary>
    public void Reload()
    {
        var keep = Selected?.Site;

        try
        {
            var entries = _session.Store().ListSites();
            Sites.Clear();
            foreach (var (site, count) in entries)
            {
                Sites.Add(new SiteItemViewModel(site, count));
            }

            if (keep is not null)
            {
                Selected = Sites.FirstOrDefault(item => item.Site == keep);
            }
        }
        catch (DecryptException)
        {
            Sites.Clear();
            Selected = null;
            _dialogs.Error("读取失败", "密码错误或数据损坏，无法读取 Cookie 数据。");
            AppLogger.Error("读取网站列表失败：cookies.enc 解密失败");
        }
        catch (StorageException ex)
        {
            Sites.Clear();
            Selected = null;
            _dialogs.Error("读取失败", ex.Message);
            AppLogger.Error($"读取网站列表失败: {ex.Message}");
        }

        OnPropertyChanged(nameof(IsEmpty));
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        if (Selected is null)
        {
            DetailSummary = "未选择网站";
            CookieNames = "（无）";
            return;
        }

        DetailSummary = $"{Selected.Site} · {Selected.Count} 个 Cookie";
        try
        {
            // 仅展示 Cookie 名称，不展示值，避免敏感信息出现在界面上
            CookieNames = CookieParser.Summarize(_session.Store().Get(Selected.Site));
        }
        catch (Exception ex) when (ex is DecryptException or StorageException)
        {
            CookieNames = "（读取失败）";
        }
    }

    private void ImportPasted()
    {
        if (_dialogs.PromptPasteCookie() is not PasteCookieInput input)
        {
            return;
        }

        try
        {
            var site = CookieParser.NormalizeSite(input.Site);
            var (cookies, warnings) = CookieParser.ParseDetailed(input.CookieText, site);

            var store = _session.Store();
            var exists = store.Get(site).Count > 0;
            if (exists && !_dialogs.Confirm(
                    "覆盖确认",
                    $"网站 {site} 已存在 Cookie，本次操作将整体替换覆盖，是否继续？"))
            {
                Status = "已取消，未做修改。";
                return;
            }

            var overwritten = store.Upsert(site, cookies);
            AppLogger.Info(
                $"{(overwritten ? "已覆盖" : "已添加")}网站 Cookie {site}（{cookies.Count} 条，{warnings.Count} 条提示）");

            Reload();
            Selected = Sites.FirstOrDefault(item => item.Site == site);
            Status = $"{(overwritten ? "已覆盖" : "已添加")} {site}，共 {cookies.Count} 条 Cookie。";
        }
        catch (CookieParseException ex)
        {
            _dialogs.Error("导入失败", ex.Message);
            AppLogger.Warning($"解析 Cookie 失败: {ex.Message}");
        }
        catch (DecryptException)
        {
            _dialogs.Error("导入失败", "密码错误或数据损坏，无法读取现有 Cookie 数据。");
            AppLogger.Error("写入 Cookie 失败：cookies.enc 解密失败");
        }
        catch (StorageException ex)
        {
            _dialogs.Error("导入失败", ex.Message);
            AppLogger.Error($"写入 Cookie 失败: {ex.Message}");
        }
    }

    private void Capture()
    {
        if (_dialogs.PromptCapture(_session) is not CaptureCookieOutput output)
        {
            return;
        }

        try
        {
            var site = CookieParser.NormalizeSite(output.Site);
            var store = _session.Store();
            var exists = store.Get(site).Count > 0;
            if (exists && !_dialogs.Confirm(
                    "覆盖确认",
                    $"网站 {site} 已存在 Cookie，本次操作将整体替换覆盖，是否继续？"))
            {
                Status = "已取消，未做修改。";
                return;
            }

            var overwritten = store.Upsert(site, output.Cookies);
            AppLogger.Info($"{(overwritten ? "已覆盖" : "已添加")}网站 Cookie {site}（{output.Cookies.Count} 条，采集）");

            Reload();
            Selected = Sites.FirstOrDefault(item => item.Site == site);
            Status = $"{(overwritten ? "已覆盖" : "已添加")} {site}，共 {output.Cookies.Count} 条 Cookie。";
        }
        catch (CookieParseException ex)
        {
            _dialogs.Error("保存失败", ex.Message);
        }
        catch (DecryptException)
        {
            _dialogs.Error("保存失败", "密码错误或数据损坏，无法读取现有 Cookie 数据。");
            AppLogger.Error("写入 Cookie 失败：cookies.enc 解密失败");
        }
        catch (StorageException ex)
        {
            _dialogs.Error("保存失败", ex.Message);
            AppLogger.Error($"写入 Cookie 失败: {ex.Message}");
        }
    }

    private void Delete()
    {
        var selected = Selected;
        if (selected is null)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "删除确认",
                $"确定要删除 {selected.Site} 的全部 Cookie 吗？\n此操作不可恢复。",
                "删除",
                "取消"))
        {
            return;
        }

        try
        {
            var removed = _session.Store().Delete(selected.Site);
            if (removed)
            {
                AppLogger.Info($"已删除网站 Cookie {selected.Site}");
                Reload();
                Status = $"已删除 {selected.Site}。";
            }
            else
            {
                Status = $"未找到网站 {selected.Site}，可能已被删除。";
            }
        }
        catch (Exception ex) when (ex is DecryptException or StorageException)
        {
            _dialogs.Error("删除失败", ex.Message);
            AppLogger.Error($"删除网站 {selected.Site} 失败: {ex.Message}");
        }
    }

    private void ChangePassword()
    {
        if (_dialogs.PromptChangePassword(_session))
        {
            Reload();
            Status = "密码修改成功，Cookie 已用新密钥重新加密。";
        }
    }
}
