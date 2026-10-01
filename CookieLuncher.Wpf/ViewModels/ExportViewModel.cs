using System.Windows;
using System.Windows.Input;
using CookieLuncher.Core;

namespace CookieLuncher.ViewModels;

/// <summary>「导出配置」页面的视图模型。</summary>
public sealed class ExportViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly IUserDialogs _dialogs;

    private bool _secondPasswordSet;
    private string _outputPath = string.Empty;
    private string _fernetKey = string.Empty;
    private string _summary = string.Empty;
    private string _status = string.Empty;
    private bool _hasResult;

    /// <summary>初始化视图模型。</summary>
    /// <param name="session">运行期上下文。</param>
    /// <param name="dialogs">交互对话框。</param>
    public ExportViewModel(AppSession session, IUserDialogs dialogs)
    {
        _session = session;
        _dialogs = dialogs;

        SetSecondPasswordCommand = new RelayCommand(SetSecondPassword);
        ExportCommand = new RelayCommand(Export);
        CopyKeyCommand = new RelayCommand(CopyKey, () => FernetKey.Length > 0);
        OpenFolderCommand = new RelayCommand(() => _dialogs.OpenDirectory(AppPaths.Root));

        Reload();
    }

    /// <summary>设置二重导出密码命令。</summary>
    public ICommand SetSecondPasswordCommand { get; }

    /// <summary>导出命令。</summary>
    public ICommand ExportCommand { get; }

    /// <summary>复制密钥命令。</summary>
    public ICommand CopyKeyCommand { get; }

    /// <summary>打开数据目录命令。</summary>
    public ICommand OpenFolderCommand { get; }

    /// <summary>是否已设置二重导出密码。</summary>
    public bool SecondPasswordSet
    {
        get => _secondPasswordSet;
        private set
        {
            if (SetProperty(ref _secondPasswordSet, value))
            {
                OnPropertyChanged(nameof(SecondPasswordMissing));
            }
        }
    }

    /// <summary>是否尚未设置二重导出密码。</summary>
    public bool SecondPasswordMissing => !SecondPasswordSet;

    /// <summary>输出文件路径。</summary>
    public string OutputPath
    {
        get => _outputPath;
        private set => SetProperty(ref _outputPath, value);
    }

    /// <summary>Fernet 密钥（base64-urlsafe）。</summary>
    public string FernetKey
    {
        get => _fernetKey;
        private set => SetProperty(ref _fernetKey, value);
    }

    /// <summary>导出结果摘要。</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>是否存在导出结果。</summary>
    public bool HasResult
    {
        get => _hasResult;
        private set => SetProperty(ref _hasResult, value);
    }

    /// <summary>输出文件名（取自 <see cref="Exporter"/>，避免界面文案与代码定义不一致）。</summary>
    public string OutputFileName => Exporter.OutputFileName;

    /// <summary>建议的 GitHub Secret 名称（取自 <see cref="Exporter"/>）。</summary>
    public string SecretName => Exporter.GitHubSecretName;

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

    /// <summary>重新读取配置状态。</summary>
    public void Reload()
    {
        SecondPasswordSet = _session.Config.SecondPasswordHash.Length > 0;
        HasResult = false;
        FernetKey = string.Empty;
        OutputPath = string.Empty;
        Summary = string.Empty;
        Status = string.Empty;
    }

    private void SetSecondPassword()
    {
        var password = _dialogs.PromptNewPassword(
            "设置二重导出密码",
            "二重导出密码用于保护【导出 Cookie】操作：导出前必须输入该密码。\n"
            + "该密码仅保存哈希，明文不落盘；请勿与访问密码相同。");
        if (password is null)
        {
            return;
        }

        try
        {
            _session.Config.SecondPasswordHash = Crypto.HashPassword(password);
            _session.Config.Save();
            SecondPasswordSet = true;
            _session.NotifyConfigChanged();
            AppLogger.Info("二重导出密码设置成功");
            Status = "二重导出密码设置成功。";
        }
        catch (Exception ex) when (ex is CryptoException or ConfigException)
        {
            _dialogs.Error("设置失败", $"保存二重导出密码失败: {ex.Message}");
            AppLogger.Error($"设置二重导出密码失败: {ex.Message}");
        }
    }

    private void Export()
    {
        // 1. 二重密码：未设置则先引导创建
        if (!SecondPasswordSet)
        {
            if (!_dialogs.Confirm(
                    "需要设置二重导出密码",
                    "首次使用导出功能，需要先设置二重导出密码。是否现在设置？",
                    "现在设置",
                    "取消"))
            {
                return;
            }

            SetSecondPassword();
            if (!SecondPasswordSet)
            {
                return;
            }
        }

        // 2. 循环校验二重密码，错误可重试
        var secondHash = _session.Config.SecondPasswordHash;
        while (true)
        {
            var entered = _dialogs.PromptPassword(
                "导出 Cookie",
                "请输入二重导出密码后开始导出。导出产物只包含密文，不会出现明文 Cookie。",
                "二重导出密码");
            if (entered is null)
            {
                Status = "已取消导出。";
                return;
            }

            if (entered.Length == 0)
            {
                _dialogs.Warn("密码不能为空", "密码不能为空，请重新输入。");
                continue;
            }

            if (!Crypto.VerifyPassword(secondHash, entered))
            {
                _dialogs.Warn("验证失败", "二重密码错误，请重试。");
                AppLogger.Warning("二重密码验证失败");
                continue;
            }

            break;
        }

        // 3. 读取本地 Cookie 数据（只读）
        System.Text.Json.Nodes.JsonObject data;
        try
        {
            data = _session.Store().Load();
        }
        catch (DecryptException)
        {
            _dialogs.Error("导出失败", "密码错误或数据损坏，无法读取 Cookie 数据。");
            AppLogger.Error("导出失败：cookies.enc 解密失败");
            return;
        }
        catch (StorageException ex)
        {
            _dialogs.Error("导出失败", ex.Message);
            AppLogger.Error($"导出失败: {ex.Message}");
            return;
        }

        // 4. 组装并写出输出文件（仅密文，明文 Cookie 只在内存）
        try
        {
            var skipped = new List<string>();
            var path = Exporter.ExportActionConfig(
                data,
                _session.Key,
                _session.ExportPath,
                site => skipped.Add(site));

            OutputPath = path;
            FernetKey = _session.Key.Base64UrlText;
            Summary = $"已导出 {data.Count} 个站点"
                + (skipped.Count > 0 ? $"（跳过 {skipped.Count} 个空站点：{string.Join(", ", skipped)}）" : string.Empty);
            HasResult = true;
            Status = "导出完成。";
            AppLogger.Info($"导出 Cookie 配置成功（{data.Count} 个站点）");
        }
        catch (ExporterException ex)
        {
            _dialogs.Error("导出失败", ex.Message);
            AppLogger.Error($"导出失败: {ex.Message}");
        }
    }

    private void CopyKey()
    {
        try
        {
            Clipboard.SetText(FernetKey);
            Status = "密钥已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            _dialogs.Warn("复制失败", $"无法写入剪贴板: {ex.Message}");
        }
    }
}
