using CookieLuncher.Core;

namespace CookieLuncher.ViewModels;

/// <summary>
/// 解锁 / 首次设置密码窗口的视图模型。
/// </summary>
public sealed class AuthViewModel : ObservableObject
{
    /// <summary>密码最小长度。</summary>
    public const int MinPasswordLength = 6;

    private string _error = string.Empty;

    /// <summary>初始化视图模型。</summary>
    /// <param name="config">配置对象。</param>
    public AuthViewModel(AppConfig config)
    {
        IsSetup = config.IsFirstRun;
    }

    /// <summary>是否首次运行（设置密码）。</summary>
    public bool IsSetup { get; }

    /// <summary>窗口标题。</summary>
    public string Title => "Cookie 启动器";

    /// <summary>副标题说明。</summary>
    public string Subtitle => IsSetup
        ? "首次运行，请设置访问密码。该密码同时用于登录本程序与加密 Cookie 文件，忘记后无法找回 Cookie。"
        : "请输入访问密码以解锁本地 Cookie 数据。";

    /// <summary>主输入框标签。</summary>
    public string PasswordLabel => IsSetup
        ? $"新密码（至少 {MinPasswordLength} 位）"
        : "访问密码";

    /// <summary>确认按钮文案。</summary>
    public string ConfirmText => IsSetup ? "创建并进入" : "解锁";

    /// <summary>错误提示。</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>是否存在错误提示。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>清除错误提示。</summary>
    public void ClearError() => Error = string.Empty;

    /// <summary>
    /// 校验输入并返回可用的密钥。
    /// </summary>
    /// <param name="config">配置对象。</param>
    /// <param name="password">输入的密码。</param>
    /// <param name="confirm">确认密码（首次设置时使用）。</param>
    /// <param name="key">成功时输出的密钥。</param>
    /// <returns>校验通过返回 true；否则设置 <see cref="Error"/> 并返回 false。</returns>
    public bool TryUnlock(AppConfig config, string password, string confirm, out FernetKey? key)
    {
        key = null;

        if (password.Length == 0)
        {
            Error = "密码不能为空。";
            return false;
        }

        if (IsSetup)
        {
            if (password.Length < MinPasswordLength)
            {
                Error = $"密码长度不能少于 {MinPasswordLength} 位。";
                return false;
            }

            if (password != password.Trim())
            {
                Error = "密码首尾不能包含空格。";
                return false;
            }

            if (password != confirm)
            {
                Error = "两次输入的密码不一致。";
                return false;
            }

            try
            {
                var salt = Crypto.GenerateSalt();
                key = Crypto.DeriveKey(password, salt);

                config.PasswordHash = Crypto.HashPassword(password);
                config.EncryptionSalt = salt;
                config.Save();
                new CookieStore(AppPaths.CookiePath, key).EnsureFile();
            }
            catch (Exception ex) when (ex is CryptoException or ConfigException or StorageException)
            {
                Error = $"初始化失败: {ex.Message}";
                return false;
            }

            AppLogger.Info("首次运行：密码设置完成，配置文件已生成");
            return true;
        }

        if (!Crypto.VerifyPassword(config.PasswordHash, password))
        {
            Error = "密码错误，请重试。";
            AppLogger.Warning("密码验证失败");
            return false;
        }

        var saltValue = config.EncryptionSalt;
        if (saltValue.Length == 0)
        {
            Error = "配置文件缺少 encryption_salt，无法解锁。请删除 config.json 后重新运行。";
            AppLogger.Error("配置文件缺少 encryption_salt");
            return false;
        }

        FernetKey derived;
        try
        {
            derived = Crypto.DeriveKey(password, saltValue);
        }
        catch (CryptoException ex)
        {
            Error = ex.Message;
            return false;
        }

        // 用密钥试解密 cookies.enc，确保密钥与数据一致（同时能发现数据损坏）
        try
        {
            new CookieStore(AppPaths.CookiePath, derived).Load();
        }
        catch (DecryptException)
        {
            Error = "密码错误或数据损坏，请重试。";
            AppLogger.Warning("解密 cookies.enc 失败：密码错误或数据损坏");
            return false;
        }
        catch (StorageException ex)
        {
            Error = $"Cookie 数据文件异常: {ex.Message}";
            AppLogger.Error($"cookies.enc 结构异常: {ex.Message}");
            return false;
        }

        key = derived;
        AppLogger.Info("密码验证成功");
        return true;
    }
}
