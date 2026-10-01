using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CookieLuncher.Core;

/// <summary>
/// Fernet 对称加密实现，与 Python <c>cryptography.fernet.Fernet</c> 完全互通。
/// <para>
/// 令牌结构：<c>base64url(0x80 || 时间戳(8, 大端) || IV(16) || AES-128-CBC(PKCS7) || HMAC-SHA256(32))</c>，
/// 其中 HMAC 覆盖版本字节到密文的全部内容。
/// </para>
/// </summary>
public static class Fernet
{
    private const byte VersionByte = 0x80;
    private const int TimestampLength = 8;
    private const int IvLength = 16;
    private const int HmacLength = 32;
    private const int BlockLength = 16;

    /// <summary>最小合法令牌长度（版本 + 时间戳 + IV + 一个分组密文 + HMAC）。</summary>
    private const int MinTokenLength = 1 + TimestampLength + IvLength + BlockLength + HmacLength;

    /// <summary>加密字节串。</summary>
    /// <param name="plaintext">明文。</param>
    /// <param name="key">Fernet 密钥。</param>
    /// <param name="timestamp">令牌时间戳；省略时取当前时间。</param>
    /// <returns>base64-urlsafe 令牌文本。</returns>
    public static string Encrypt(byte[] plaintext, FernetKey key, DateTimeOffset? timestamp = null)
    {
        var iv = RandomNumberGenerator.GetBytes(IvLength);
        var ciphertext = AesCbcEncrypt(plaintext, key.EncryptionKey, iv);

        var basic = new byte[1 + TimestampLength + IvLength + ciphertext.Length];
        basic[0] = VersionByte;
        BinaryPrimitives.WriteInt64BigEndian(
            basic.AsSpan(1, TimestampLength),
            (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        iv.CopyTo(basic.AsSpan(1 + TimestampLength));
        ciphertext.CopyTo(basic.AsSpan(1 + TimestampLength + IvLength));

        var hmac = HMACSHA256.HashData(key.SigningKey, basic);

        var token = new byte[basic.Length + HmacLength];
        basic.CopyTo(token, 0);
        hmac.CopyTo(token, basic.Length);
        return Base64Url.Encode(token);
    }

    /// <summary>解密令牌。</summary>
    /// <param name="token">base64-urlsafe 令牌文本。</param>
    /// <param name="key">Fernet 密钥。</param>
    /// <returns>明文。</returns>
    /// <exception cref="DecryptException">令牌非法、签名不匹配或解密失败。</exception>
    public static byte[] Decrypt(string token, FernetKey key)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new DecryptException();
        }

        if (!Base64Url.TryDecode(token, out var data) || data.Length < MinTokenLength)
        {
            throw new DecryptException();
        }

        var basic = data.AsSpan(0, data.Length - HmacLength);
        var expected = HMACSHA256.HashData(key.SigningKey, basic);
        if (!CryptographicOperations.FixedTimeEquals(expected, data.AsSpan(data.Length - HmacLength)))
        {
            throw new DecryptException();
        }

        if (data[0] != VersionByte)
        {
            throw new DecryptException();
        }

        var iv = data.AsSpan(1 + TimestampLength, IvLength);
        var ciphertext = data.AsSpan(1 + TimestampLength + IvLength, data.Length - HmacLength - 1 - TimestampLength - IvLength);

        try
        {
            return AesCbcDecrypt(ciphertext.ToArray(), key.EncryptionKey, iv.ToArray());
        }
        catch (CryptographicException ex)
        {
            throw new DecryptException("密码错误或数据损坏", ex);
        }
    }

    private static byte[] AesCbcEncrypt(byte[] plaintext, ReadOnlySpan<byte> key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.ToArray();
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] AesCbcDecrypt(byte[] ciphertext, ReadOnlySpan<byte> key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.ToArray();
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}
