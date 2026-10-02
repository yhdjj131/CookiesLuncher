namespace CookieLuncher.Core;

/// <summary>密码规则校验（与 Python 版 <c>ui.prompt_new_password</c> 一致）。</summary>
public static class PasswordRules
{
    /// <summary>密码最小长度。</summary>
    public const int MinLength = 6;

    /// <summary>校验非空密码（用于解锁、二重导出密码等单次输入）。</summary>
    /// <param name="password">密码。</param>
    /// <returns>错误信息；通过时返回 null。</returns>
    public static string? ValidateNonEmpty(string? password)
        => string.IsNullOrEmpty(password) ? "密码不能为空。" : null;

    /// <summary>校验新密码（长度、首尾空格、两次一致）。</summary>
    /// <param name="password">新密码。</param>
    /// <param name="confirm">确认密码。</param>
    /// <param name="minLength">最小长度。</param>
    /// <returns>错误信息；通过时返回 null。</returns>
    public static string? ValidateNew(string? password, string? confirm, int minLength = MinLength)
    {
        var value = password ?? string.Empty;
        if (value.Length == 0)
        {
            return "密码不能为空。";
        }

        if (value.Length < minLength)
        {
            return $"密码长度不能少于 {minLength} 位。";
        }

        if (value != value.Trim())
        {
            return "密码首尾不能包含空格。";
        }

        return value != (confirm ?? string.Empty) ? "两次输入的密码不一致。" : null;
    }
}
