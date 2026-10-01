namespace CookieLuncher.Core;

/// <summary>
/// URL 安全的 base64 编解码，与 Python <c>base64.urlsafe_b64encode / urlsafe_b64decode</c> 一致：
/// 编码结果带 <c>=</c> 填充；解码时容忍缺失填充。
/// </summary>
public static class Base64Url
{
    /// <summary>把字节串编码为带填充的 URL 安全 base64 文本。</summary>
    /// <param name="data">原始字节串。</param>
    /// <returns>编码结果。</returns>
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');

    /// <summary>解码 URL 安全 base64 文本（容忍缺失填充）。</summary>
    /// <param name="text">编码文本。</param>
    /// <returns>解码后的字节串。</returns>
    /// <exception cref="FormatException">文本不是合法的 base64。</exception>
    public static byte[] Decode(string text)
    {
        var normalized = text.Trim().Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder == 1)
        {
            throw new FormatException("base64 文本长度非法");
        }

        if (remainder > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(normalized);
    }

    /// <summary>尝试解码；失败时返回 false 而不抛异常。</summary>
    /// <param name="text">编码文本。</param>
    /// <param name="data">解码结果。</param>
    /// <returns>成功返回 true。</returns>
    public static bool TryDecode(string? text, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            data = Decode(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 编码 Argon2 PHC 字符串中的 base64 段：标准字母表、去掉填充（与 argon2 一致）。
    /// </summary>
    /// <param name="data">原始字节串。</param>
    /// <returns>编码结果。</returns>
    public static string EncodeUnpadded(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');
}
