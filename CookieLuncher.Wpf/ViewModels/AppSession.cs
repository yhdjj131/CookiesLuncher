using System.Text.Json.Nodes;
using CookieLuncher.Core;

namespace CookieLuncher.ViewModels;

/// <summary>密码修改失败。</summary>
public sealed class PasswordChangeException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public PasswordChangeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 运行期上下文：配置、加密密钥与各类路径（对应 Python 版 <c>core/ui.py</c> 的 AppContext）。
/// </summary>
public sealed class AppSession
{
    /// <summary>初始化上下文。</summary>
    /// <param name="config">配置对象（与 config.json 同步）。</param>
    /// <param name="key">当前有效的 Fernet 密钥。</param>
    public AppSession(AppConfig config, FernetKey key)
    {
        Config = config;
        Key = key;
    }

    /// <summary>配置对象。</summary>
    public AppConfig Config { get; }

    /// <summary>当前有效的 Fernet 密钥。</summary>
    public FernetKey Key { get; private set; }

    /// <summary>导出产物路径。</summary>
    public string ExportPath => AppPaths.ExportPath;

    /// <summary>配置发生变化（例如浏览器后端、密码）。</summary>
    public event EventHandler? ConfigChanged;

    /// <summary>按当前密钥构造 Cookie 存储对象。</summary>
    /// <returns>存储对象。</returns>
    public CookieStore Store() => new(AppPaths.CookiePath, Key);

    /// <summary>通知订阅方配置已变化。</summary>
    public void NotifyConfigChanged() => ConfigChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 修改访问密码，并用新密钥重新加密 cookies.enc。
    /// <para>流程与 Python 版一致：旧密钥读数据 → 新密钥写回 → 更新 config.json；
    /// 写配置失败时回滚 cookies.enc 与配置，避免新密码无法解密旧数据。</para>
    /// </summary>
    /// <param name="oldPassword">当前密码。</param>
    /// <param name="newPassword">新密码。</param>
    /// <exception cref="PasswordChangeException">旧密码错误、数据损坏或写入失败。</exception>
    public void ChangePassword(string oldPassword, string newPassword)
    {
        if (!Crypto.VerifyPassword(Config.PasswordHash, oldPassword))
        {
            throw new PasswordChangeException("当前密码错误。");
        }

        if (newPassword == oldPassword)
        {
            throw new PasswordChangeException("新密码与旧密码相同，无需修改。");
        }

        JsonObject data;
        try
        {
            data = Store().Load();
        }
        catch (DecryptException)
        {
            throw new PasswordChangeException("密码错误或数据损坏，无法读取现有 Cookie 数据。");
        }
        catch (StorageException ex)
        {
            throw new PasswordChangeException(ex.Message);
        }

        var newSalt = Crypto.GenerateSalt();
        var newKey = Crypto.DeriveKey(newPassword, newSalt);
        var newHash = Crypto.HashPassword(newPassword);

        byte[]? backup = null;
        if (File.Exists(AppPaths.CookiePath))
        {
            try
            {
                backup = File.ReadAllBytes(AppPaths.CookiePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                backup = null;
            }
        }

        // 先用新密钥重写 cookies.enc，再更新 config.json
        try
        {
            new CookieStore(AppPaths.CookiePath, newKey).Save(data);
        }
        catch (StorageException ex)
        {
            throw new PasswordChangeException($"重新加密 Cookie 数据失败: {ex.Message}");
        }

        var previousHash = Config.PasswordHash;
        var previousSalt = Config.EncryptionSalt;
        Config.PasswordHash = newHash;
        Config.EncryptionSalt = newSalt;

        try
        {
            Config.Save();
        }
        catch (ConfigException ex)
        {
            // 回滚：恢复旧的配置与 cookies.enc
            Config.PasswordHash = previousHash;
            Config.EncryptionSalt = previousSalt;
            if (backup is not null)
            {
                try
                {
                    File.WriteAllBytes(AppPaths.CookiePath, backup);
                }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
                {
                    AppLogger.Error($"回滚 cookies.enc 失败，请手动用备份恢复: {writeError.Message}");
                }
            }

            throw new PasswordChangeException($"保存配置失败，已回滚: {ex.Message}");
        }

        Key = newKey;
        NotifyConfigChanged();
    }
}
