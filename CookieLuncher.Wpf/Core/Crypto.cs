using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Konscious.Security.Cryptography;

namespace CookieLuncher.Core;

/// <summary>
/// 加密、解密与密码哈希（与 Python 版 <c>core/crypto.py</c> 完全互通）：
/// <list type="bullet">
///   <item>密码哈希：Argon2id PHC 字符串（等价 argon2-cffi 的默认参数）；</item>
///   <item>数据加密：Fernet（AES-128-CBC + HMAC-SHA256）；</item>
///   <item>密钥派生：PBKDF2-HMAC-SHA256，200000 次迭代，派生 32 字节。</item>
/// </list>
/// </summary>
public static class Crypto
{
    /// <summary>PBKDF2 迭代次数（与 Python 版保持一致，改动会导致已有数据无法解密）。</summary>
    public const int Pbkdf2Iterations = 200_000;

    /// <summary>派生密钥长度（字节）。</summary>
    public const int KeyBytes = 32;

    /// <summary>盐值长度（字节）。</summary>
    public const int SaltBytes = 16;

    /// <summary>Argon2 内存开销（KiB，64 MiB）。</summary>
    public const int Argon2MemoryCost = 65_536;

    /// <summary>Argon2 迭代次数。</summary>
    public const int Argon2TimeCost = 3;

    /// <summary>Argon2 并行度。</summary>
    public const int Argon2Parallelism = 4;

    /// <summary>Argon2 输出长度（字节）。</summary>
    public const int Argon2HashLength = 32;

    /// <summary>Argon2 支持的版本号（0x13）。</summary>
    private const int Argon2Version = 19;

    /// <summary>生成 base64 编码的随机盐值（16 字节）。</summary>
    /// <returns>盐值文本。</returns>
    public static string GenerateSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));

    /// <summary>由密码与盐值派生 Fernet 密钥。</summary>
    /// <param name="password">用户密码（明文）。</param>
    /// <param name="saltBase64">base64 编码的盐值。</param>
    /// <returns>Fernet 密钥。</returns>
    /// <exception cref="CryptoException">密码为空或盐值非法。</exception>
    public static FernetKey DeriveKey(string password, string saltBase64)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new CryptoException("密码不能为空");
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(saltBase64 ?? string.Empty);
        }
        catch (FormatException ex)
        {
            throw new CryptoException("盐值不是合法的 base64 字符串，无法派生密钥", ex);
        }

        if (salt.Length == 0)
        {
            throw new CryptoException("盐值为空，无法派生密钥");
        }

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

        return FernetKey.FromRaw(derived);
    }

    /// <summary>使用 Argon2id 生成密码哈希（PHC 字符串）。</summary>
    /// <param name="password">用户密码（明文）。</param>
    /// <returns>形如 <c>$argon2id$v=19$m=65536,t=3,p=4$salt$hash</c> 的哈希串。</returns>
    /// <exception cref="CryptoException">密码为空。</exception>
    public static string HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new CryptoException("密码不能为空");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = RunArgon2("argon2id", password, salt, Argon2MemoryCost, Argon2TimeCost, Argon2Parallelism, Argon2HashLength);

        return string.Concat(
            "$argon2id$v=", Argon2Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "$m=", Argon2MemoryCost.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",t=", Argon2TimeCost.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ",p=", Argon2Parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "$", Base64Url.EncodeUnpadded(salt),
            "$", Base64Url.EncodeUnpadded(hash));
    }

    /// <summary>校验密码是否与 Argon2 哈希匹配（不抛异常，失败返回 false）。</summary>
    /// <param name="hash">config.json 中保存的 Argon2 哈希。</param>
    /// <param name="password">用户输入的密码。</param>
    /// <returns>匹配返回 true。</returns>
    public static bool VerifyPassword(string? hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!TryParsePhc(hash, out var parsed))
        {
            return false;
        }

        byte[] actual;
        try
        {
            actual = RunArgon2(
                parsed.Type,
                password,
                parsed.Salt,
                parsed.MemoryCost,
                parsed.TimeCost,
                parsed.Parallelism,
                parsed.Hash.Length);
        }
        catch (Exception)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
    }

    /// <summary>把字典序列化为 JSON（中文不转义）后加密。</summary>
    /// <param name="data">待加密数据。</param>
    /// <param name="key">Fernet 密钥。</param>
    /// <returns>Fernet 令牌的 ASCII 字节串。</returns>
    /// <exception cref="CryptoException">序列化失败。</exception>
    public static byte[] EncryptDict(JsonObject data, FernetKey key)
    {
        string json;
        try
        {
            json = Json.Serialize(data);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new CryptoException($"待加密数据无法序列化为 JSON: {ex.Message}", ex);
        }

        try
        {
            return Encoding.ASCII.GetBytes(Fernet.Encrypt(Encoding.UTF8.GetBytes(json), key));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new CryptoException($"加密失败: {ex.Message}", ex);
        }
    }

    /// <summary>解密字节串并解析为 JSON 对象。</summary>
    /// <param name="token">Fernet 令牌字节串。</param>
    /// <param name="key">Fernet 密钥。</param>
    /// <returns>解密后的 JSON 对象。</returns>
    /// <exception cref="DecryptException">密钥错误或数据损坏。</exception>
    public static JsonObject DecryptDict(byte[] token, FernetKey key)
    {
        var plain = Fernet.Decrypt(Encoding.ASCII.GetString(token), key);

        try
        {
            var node = JsonNode.Parse(plain);
            if (node is not JsonObject obj)
            {
                throw new DecryptException();
            }

            return obj;
        }
        catch (JsonException ex)
        {
            throw new DecryptException("密码错误或数据损坏", ex);
        }
    }

    /// <summary>解析 Argon2 PHC 字符串。</summary>
    private static bool TryParsePhc(string phc, out PhcParts parts)
    {
        // $argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>
        parts = default;
        var segments = phc.Split('$');
        if (segments.Length != 6 || segments[0].Length != 0 || segments[1].Length == 0)
        {
            return false;
        }

        var type = segments[1];
        if (type is not ("argon2id" or "argon2i" or "argon2d"))
        {
            return false;
        }

        if (!segments[2].StartsWith("v=", StringComparison.Ordinal)
            || !int.TryParse(segments[2][2..], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var version)
            || version != Argon2Version)
        {
            return false;
        }

        int memory = 0, time = 0, parallelism = 0;
        foreach (var pair in segments[3].Split(','))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0
                || !int.TryParse(pair[(separator + 1)..], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                return false;
            }

            switch (pair[..separator])
            {
                case "m":
                    memory = value;
                    break;
                case "t":
                    time = value;
                    break;
                case "p":
                    parallelism = value;
                    break;
                default:
                    return false;
            }
        }

        if (memory <= 0 || time <= 0 || parallelism <= 0)
        {
            return false;
        }

        if (!Base64Url.TryDecode(segments[4], out var salt) || salt.Length < 8)
        {
            return false;
        }

        if (!Base64Url.TryDecode(segments[5], out var hash) || hash.Length < 4)
        {
            return false;
        }

        if (memory < 8 * parallelism)
        {
            return false;
        }

        parts = new PhcParts(type, memory, time, parallelism, salt, hash);
        return true;
    }

    /// <summary>按参数运行 Argon2 并返回原始哈希。</summary>
    private static byte[] RunArgon2(
        string type,
        string password,
        byte[] salt,
        int memoryCost,
        int timeCost,
        int parallelism,
        int outputLength)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        using Argon2 argon = type switch
        {
            "argon2i" => new Argon2i(passwordBytes),
            "argon2d" => new Argon2d(passwordBytes),
            _ => new Argon2id(passwordBytes),
        };

        argon.Salt = salt;
        argon.MemorySize = memoryCost;
        argon.Iterations = timeCost;
        argon.DegreeOfParallelism = parallelism;

        return argon.GetBytes(outputLength);
    }

    /// <summary>Argon2 PHC 字符串的解析结果。</summary>
    private readonly record struct PhcParts(
        string Type,
        int MemoryCost,
        int TimeCost,
        int Parallelism,
        byte[] Salt,
        byte[] Hash);
}
