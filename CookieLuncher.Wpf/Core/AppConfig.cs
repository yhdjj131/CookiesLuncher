using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CookieLuncher.Core;

/// <summary>配置文件读写或校验失败。</summary>
public sealed class ConfigException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public ConfigException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public ConfigException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// config.json 的读写、默认值合并与基础校验。
/// <para>
/// 字段名、默认值、合并规则、序列化格式均与 Python 版 <c>core/config.py</c> 一致，
/// 并额外保留文件中出现的未知字段，避免两个版本交替运行时丢配置。
/// </para>
/// </summary>
public sealed class AppConfig
{
    /// <summary>内置 Thorium 后端。</summary>
    public const string BackendThorium = "thorium";

    /// <summary>本机 Edge / Chrome 后端。</summary>
    public const string BackendSystem = "system";

    /// <summary>允许的日志级别。</summary>
    public static readonly string[] ValidLogLevels = ["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"];

    /// <summary>默认日志保留天数。</summary>
    public const int DefaultLogRetentionDays = 30;

    /// <summary>默认窗口尺寸。</summary>
    public const string DefaultWindowSize = "maximized";

    /// <summary>已知字段（规范化时按此顺序重排，其余字段追加在末尾）。</summary>
    private static readonly string[] KnownKeys =
    [
        "password_hash",
        "encryption_salt",
        "second_password_hash",
        "log_level",
        "log_retention_days",
        "browser_backend",
        "browser",
    ];

    private static readonly Regex WindowSizeRegex = new(
        @"^\s*(\d{3,5})\s*[xX*,]\s*(\d{3,5})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly JsonObject _data;

    private AppConfig(JsonObject data) => _data = data;

    /// <summary>访问密码的 Argon2 哈希。</summary>
    public string PasswordHash
    {
        get => Json.Str(_data, "password_hash");
        set => _data["password_hash"] = value;
    }

    /// <summary>密钥派生用的随机盐（base64）。</summary>
    public string EncryptionSalt
    {
        get => Json.Str(_data, "encryption_salt");
        set => _data["encryption_salt"] = value;
    }

    /// <summary>二重导出密码的 Argon2 哈希。</summary>
    public string SecondPasswordHash
    {
        get => Json.Str(_data, "second_password_hash");
        set => _data["second_password_hash"] = value;
    }

    /// <summary>日志级别。</summary>
    public string LogLevel
    {
        get => Json.Str(_data, "log_level");
        set => _data["log_level"] = value;
    }

    /// <summary>日志保留天数。</summary>
    public int LogRetentionDays
    {
        get => (int)(Json.ToIntLike(_data["log_retention_days"]) ?? DefaultLogRetentionDays);
        set => _data["log_retention_days"] = value;
    }

    /// <summary>浏览器后端：<c>thorium</c> 或 <c>system</c>。</summary>
    public string BrowserBackend
    {
        get => Json.Str(_data, "browser_backend");
        set => _data["browser_backend"] = value;
    }

    /// <summary>是否以无头模式启动浏览器。</summary>
    public bool Headless
    {
        get => Json.Truthy(BrowserObject["headless"]);
        set => BrowserObject["headless"] = value;
    }

    /// <summary>浏览器窗口尺寸（<c>maximized</c> 或 <c>1280x720</c>）。</summary>
    public string WindowSize
    {
        get
        {
            var node = BrowserObject["window_size"];
            if (node is null)
            {
                return DefaultWindowSize;
            }

            var value = Json.PythonStr(node).Trim();
            return value.Length == 0 ? DefaultWindowSize : value;
        }

        set => BrowserObject["window_size"] = value;
    }

    /// <summary>是否尚未设置访问密码（首次运行）。</summary>
    public bool IsFirstRun => string.IsNullOrWhiteSpace(PasswordHash);

    /// <summary>底层的完整配置对象。</summary>
    public JsonObject Raw => _data;

    private JsonObject BrowserObject
    {
        get
        {
            if (_data["browser"] is not JsonObject browser)
            {
                browser = new JsonObject();
                _data["browser"] = browser;
            }

            return browser;
        }
    }

    /// <summary>由 JSON 对象构造规范化后的配置。</summary>
    /// <param name="data">原始配置对象（可为 null）。</param>
    /// <returns>规范化配置。</returns>
    public static AppConfig FromJson(JsonObject? data) => new(Normalize(data));

    /// <summary>
    /// 读取 config.json；文件不存在或为空时生成默认配置并落盘。
    /// </summary>
    /// <returns>规范化后的配置。</returns>
    /// <exception cref="ConfigException">文件存在但无法读取或 JSON 损坏。</exception>
    public static AppConfig Load()
    {
        var path = AppPaths.ConfigPath;
        if (!File.Exists(path))
        {
            var fresh = FromJson(null);
            fresh.Save();
            return fresh;
        }

        string raw;
        try
        {
            // UTF-8 严格解码 + 自动跳过 BOM，兼容记事本等编辑器写入的内容
            using var reader = new StreamReader(
                path,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            raw = reader.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new ConfigException($"配置文件 {path} 不是 UTF-8 编码: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"无法读取配置文件 {path}: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            // 空文件按默认配置处理，避免用户误清空后无法启动
            var fresh = FromJson(null);
            fresh.Save();
            return fresh;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new ConfigException(
                $"配置文件 {path} 不是合法的 JSON（{ex.Message}），请修复或删除后重新运行", ex);
        }

        if (node is not JsonObject obj)
        {
            throw new ConfigException("配置文件内容必须是 JSON 对象");
        }

        return FromJson(obj);
    }

    /// <summary>
    /// 把配置写回 config.json（先写临时文件再替换，避免写坏原文件）。
    /// </summary>
    /// <exception cref="ConfigException">写入失败。</exception>
    public void Save()
    {
        var path = AppPaths.ConfigPath;
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                Json.Serialize(_data, indented: true) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new ConfigException($"无法写入配置文件 {path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ConfigException($"无法写入配置文件 {path}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 解析 <c>browser.window_size</c>。
    /// </summary>
    /// <returns>（宽, 高）；为 maximized 或无法识别时返回 null。</returns>
    public (int Width, int Height)? ParseWindowSize()
    {
        var value = WindowSize;
        if (value.Length == 0)
        {
            return null;
        }

        var text = value.ToLowerInvariant();
        if (text is "maximized" or "max" or "fullscreen" or "default")
        {
            return null;
        }

        var match = WindowSizeRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var width = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return width <= 0 || height <= 0 ? null : (width, height);
    }

    /// <summary>窗口配置是否为「最大化」模式。</summary>
    /// <returns>最大化返回 true。</returns>
    public bool IsMaximized() => ParseWindowSize() is null;

    /// <summary>
    /// 校验并规范化配置内容，非法值回退为默认值；未知字段原样保留并追加在末尾。
    /// </summary>
    private static JsonObject Normalize(JsonObject? input)
    {
        input ??= new JsonObject();

        // 兼容旧版 config.json：modify_mode 标记已废弃
        input.Remove("modify_mode");

        var browser = input["browser"] as JsonObject;

        var result = new JsonObject
        {
            ["password_hash"] = Json.Str(input, "password_hash"),
            ["encryption_salt"] = Json.Str(input, "encryption_salt"),
            ["second_password_hash"] = Json.Str(input, "second_password_hash"),
            ["log_level"] = NormalizeLogLevel(Json.Str(input, "log_level")),
            ["log_retention_days"] = NormalizeRetention(input),
            ["browser_backend"] = NormalizeBackend(Json.Str(input, "browser_backend")),
        };

        var windowSize = Json.Str(browser, "window_size").Trim();
        result["browser"] = new JsonObject
        {
            ["headless"] = browser is not null
                && browser.TryGetPropertyValue("headless", out var headless)
                && Json.Truthy(headless),
            ["window_size"] = windowSize.Length == 0 ? DefaultWindowSize : windowSize,
        };

        foreach (var pair in input)
        {
            if (!KnownKeys.Contains(pair.Key))
            {
                result[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return result;
    }

    private static string NormalizeLogLevel(string value)
    {
        var level = value.Trim().ToUpperInvariant();
        return ValidLogLevels.Contains(level) ? level : "INFO";
    }

    private static string NormalizeBackend(string value)
    {
        var backend = value.Trim().ToLowerInvariant();
        return backend is BackendSystem or BackendThorium ? backend : BackendThorium;
    }

    private static int NormalizeRetention(JsonObject input)
    {
        var value = Json.ToIntLike(input["log_retention_days"]) ?? DefaultLogRetentionDays;
        return value > 0 ? (int)Math.Min(value, int.MaxValue) : DefaultLogRetentionDays;
    }
}
