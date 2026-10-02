namespace CookieLuncher.Core;

/// <summary>
/// Fernet 密钥（32 字节）。同时保留原始字节与 base64-urlsafe 文本形式，
/// 后者即写入 GitHub Secret 的字符串（带 <c>=</c> 填充，与 Python 版一致）。
/// </summary>
public sealed class FernetKey
{
    /// <summary>Fernet 要求的密钥长度（字节）。</summary>
    public const int RawLength = 32;

    /// <summary>签名子密钥（前 16 字节）。</summary>
    public const int HalfLength = 16;

    private FernetKey(byte[] raw, string base64Url)
    {
        Raw = raw;
        Base64UrlText = base64Url;
    }

    /// <summary>32 字节原始密钥。</summary>
    public byte[] Raw { get; }

    /// <summary>base64-urlsafe 文本形式（44 个字符，含一个 <c>=</c> 填充）。</summary>
    public string Base64UrlText { get; }

    /// <summary>签名子密钥。</summary>
    public ReadOnlySpan<byte> SigningKey => Raw.AsSpan(0, HalfLength);

    /// <summary>加密子密钥。</summary>
    public ReadOnlySpan<byte> EncryptionKey => Raw.AsSpan(HalfLength, HalfLength);

    /// <summary>由 32 字节原始密钥构造。</summary>
    /// <param name="raw">原始密钥。</param>
    /// <returns>密钥对象。</returns>
    public static FernetKey FromRaw(byte[] raw)
    {
        if (raw.Length != RawLength)
        {
            throw new CryptoException("Fernet 密钥长度必须为 32 字节");
        }

        return new FernetKey(raw, Base64Url.Encode(raw));
    }

    /// <summary>由 base64-urlsafe 文本构造。</summary>
    /// <param name="base64UrlText">base64-urlsafe 文本。</param>
    /// <returns>密钥对象。</returns>
    /// <exception cref="CryptoException">文本非法或长度不符。</exception>
    public static FernetKey FromBase64Url(string base64UrlText)
    {
        if (!Base64Url.TryDecode(base64UrlText, out var raw))
        {
            throw new CryptoException("Fernet 密钥不是合法的 base64-urlsafe 字符串");
        }

        if (raw.Length != RawLength)
        {
            throw new CryptoException("Fernet 密钥长度必须为 32 字节");
        }

        return new FernetKey(raw, base64UrlText);
    }
}
