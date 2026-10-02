using System.Text.Json.Nodes;

namespace CookieLuncher.Core;

/// <summary>Cookie 存储读写失败。</summary>
public sealed class StorageException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public StorageException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public StorageException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>已配置网站的摘要信息。</summary>
/// <param name="Site">网站域名。</param>
/// <param name="Count">Cookie 数量。</param>
public readonly record struct SiteEntry(string Site, int Count);

/// <summary>
/// cookies.enc 的读写封装。
/// <para>
/// 存储结构（加密前的明文）：<c>{ "bilibili.com": [ {cookie}, ... ] }</c>，
/// 整个对象用 Fernet 加密后写入单文件，与 Python 版完全互通。
/// </para>
/// </summary>
public sealed class CookieStore
{
    /// <summary>单个网站的最大 Cookie 数量，防止误粘贴超大内容。</summary>
    public const int MaxCookiesPerSite = 2000;

    private readonly FernetKey _key;

    /// <summary>初始化存储对象。</summary>
    /// <param name="path">cookies.enc 路径。</param>
    /// <param name="key">由密码与 salt 派生的 Fernet 密钥。</param>
    public CookieStore(string path, FernetKey key)
    {
        Path = path;
        _key = key;
    }

    /// <summary>cookies.enc 路径。</summary>
    public string Path { get; }

    /// <summary>判断 cookies.enc 是否存在。</summary>
    public bool Exists => File.Exists(Path);

    /// <summary>文件不存在时创建内容为空（<c>{}</c>）的加密文件。</summary>
    /// <returns>本次调用是否新建了文件。</returns>
    public bool EnsureFile()
    {
        if (Exists)
        {
            return false;
        }

        Save(new JsonObject());
        return true;
    }

    /// <summary>解密并读取全部网站 Cookie。</summary>
    /// <returns>域名 → Cookie 数组 的对象；文件不存在或为空时返回空对象。</returns>
    /// <exception cref="StorageException">文件读取失败或内容结构非法。</exception>
    /// <exception cref="DecryptException">密钥错误或文件损坏。</exception>
    public JsonObject Load()
    {
        if (!File.Exists(Path))
        {
            return new JsonObject();
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(Path);
        }
        catch (IOException ex)
        {
            throw new StorageException($"无法读取 {Path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new StorageException($"无法读取 {Path}: {ex.Message}", ex);
        }

        if (IsBlank(raw))
        {
            // 空文件视为空存储
            return new JsonObject();
        }

        return Validate(Crypto.DecryptDict(raw, _key));
    }

    /// <summary>加密并写回 cookies.enc（先写临时文件再替换）。</summary>
    /// <param name="data">完整的数据对象。</param>
    /// <exception cref="StorageException">写入失败。</exception>
    public void Save(JsonObject data)
    {
        byte[] payload;
        try
        {
            payload = Crypto.EncryptDict(data, _key);
        }
        catch (CryptoException ex)
        {
            throw new StorageException($"加密 Cookie 数据失败: {ex.Message}", ex);
        }

        var tempPath = Path + ".tmp";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException($"无法写入 {Path}: {ex.Message}", ex);
        }
    }

    /// <summary>列出所有已配置网站及其 Cookie 数量。</summary>
    /// <returns>按域名排序的条目列表。</returns>
    public List<SiteEntry> ListSites()
    {
        var data = Load();
        return data
            .Select(pair => new SiteEntry(pair.Key, pair.Value is JsonArray array ? array.Count : 0))
            .OrderBy(entry => entry.Site, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>读取指定网站的 Cookie（返回副本，修改不影响存储对象）。</summary>
    /// <param name="site">网站域名（会自动规范化）。</param>
    /// <returns>Cookie 列表；网站不存在时返回空列表。</returns>
    public List<JsonObject> Get(string site)
    {
        var key = SafeKey(site);
        foreach (var pair in Load())
        {
            if (SafeKey(pair.Key) == key && pair.Value is JsonArray array)
            {
                return array.OfType<JsonObject>().Select(item => (JsonObject)item.DeepClone()).ToList();
            }
        }

        return [];
    }

    /// <summary>新增或覆盖指定网站的 Cookie。</summary>
    /// <param name="site">网站域名（会自动规范化）。</param>
    /// <param name="cookies">Cookie 列表。</param>
    /// <returns>覆盖已有网站返回 true，新增返回 false。</returns>
    /// <exception cref="StorageException">Cookie 数量或结构非法。</exception>
    public bool Upsert(string site, IReadOnlyList<JsonObject> cookies)
    {
        if (cookies.Count == 0)
        {
            throw new StorageException("Cookie 列表不能为空");
        }

        if (cookies.Count > MaxCookiesPerSite)
        {
            throw new StorageException($"单个网站的 Cookie 数量超过上限 {MaxCookiesPerSite} 条");
        }

        var siteKey = SafeKey(site);
        var data = Load();
        var overwritten = false;
        foreach (var existing in data.Select(pair => pair.Key).ToList())
        {
            if (SafeKey(existing) == siteKey)
            {
                data.Remove(existing);
                overwritten = true;
            }
        }

        var array = new JsonArray();
        foreach (var cookie in cookies)
        {
            array.Add(cookie.DeepClone());
        }

        data[siteKey] = array;
        Save(data);
        return overwritten;
    }

    /// <summary>删除指定网站的 Cookie。</summary>
    /// <param name="site">网站域名（会自动规范化）。</param>
    /// <returns>实际删除返回 true，网站不存在返回 false。</returns>
    public bool Delete(string site)
    {
        var siteKey = SafeKey(site);
        var data = Load();
        var removed = false;
        foreach (var existing in data.Select(pair => pair.Key).ToList())
        {
            if (SafeKey(existing) == siteKey)
            {
                data.Remove(existing);
                removed = true;
            }
        }

        if (removed)
        {
            Save(data);
        }

        return removed;
    }

    /// <summary>校验解密后的数据结构。</summary>
    private static JsonObject Validate(JsonObject data)
    {
        var result = new JsonObject();
        foreach (var pair in data)
        {
            if (pair.Value is not JsonArray array)
            {
                throw new StorageException($"网站 {pair.Key} 的 Cookie 数据不是列表");
            }

            var cleaned = new JsonArray();
            foreach (var item in array)
            {
                if (item is JsonObject cookie)
                {
                    cleaned.Add(cookie.DeepClone());
                }
            }

            result[pair.Key] = cleaned;
        }

        return result;
    }

    /// <summary>把用户输入规范化为存储用的键，规范化失败时退化为原始字符串。</summary>
    private static string SafeKey(string site)
    {
        try
        {
            return CookieParser.NormalizeSite(site);
        }
        catch (Exception)
        {
            return (site ?? string.Empty).Trim().ToLowerInvariant().TrimStart('.');
        }
    }

    /// <summary>判断字节串是否为空白（等价 Python <c>not raw.strip()</c>）。</summary>
    private static bool IsBlank(byte[] data)
    {
        foreach (var value in data)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\v' or (byte)'\f'))
            {
                return false;
            }
        }

        return true;
    }
}
