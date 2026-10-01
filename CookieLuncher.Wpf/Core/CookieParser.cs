using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CookieLuncher.Core;

/// <summary>Cookie 解析失败。</summary>
public sealed class CookieParseException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public CookieParseException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public CookieParseException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Cookie 文本解析与格式统一（与 Python 版 <c>core/cookie_parser.py</c> 行为一致）。
/// <para>
/// 支持三种输入格式并自动识别：分号分隔字符串、标准 JSON 数组、
/// Cookie-Editor 导出的 JSON。解析结果统一为
/// <c>{name, value, domain, path, expires, httpOnly, secure, sameSite}</c>。
/// </para>
/// </summary>
public static class CookieParser
{
    /// <summary>提示信息中展示的最大片段长度，避免把完整 Cookie 输出到界面或日志。</summary>
    public const int PreviewLimit = 20;

    /// <summary>sameSite 取值映射：Cookie-Editor / 浏览器导出 → Playwright。</summary>
    private static readonly Dictionary<string, string?> SameSiteMap = new(StringComparer.Ordinal)
    {
        ["no_restriction"] = "None",
        ["none"] = "None",
        ["unspecified"] = null,
        ["lax"] = "Lax",
        ["strict"] = "Strict",
    };

    /// <summary>网站域名合法字符（支持 localhost、IPv4、多级域名）。</summary>
    private static readonly Regex SiteRegex = new(
        @"^[a-z0-9]([a-z0-9_\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9_\-]*[a-z0-9])?)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把用户输入的网站地址规范化为纯域名。
    /// </summary>
    /// <param name="raw">域名或 URL，例如 <c>https://www.bilibili.com/xxx</c>。</param>
    /// <returns>规范化后的域名，例如 <c>bilibili.com</c>。</returns>
    /// <exception cref="CookieParseException">输入为空或格式非法。</exception>
    public static string NormalizeSite(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            throw new CookieParseException("网站域名不能为空");
        }

        var schemeIndex = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            text = text[(schemeIndex + 3)..];
        }

        text = FirstSegment(text, '/');
        text = FirstSegment(text, '?');
        text = FirstSegment(text, '#');

        var atIndex = text.LastIndexOf('@');
        if (atIndex >= 0)
        {
            text = text[(atIndex + 1)..];
        }

        if (text.StartsWith('['))
        {
            throw new CookieParseException("暂不支持 IPv6 地址作为网站域名");
        }

        var colonIndex = text.IndexOf(':');
        if (colonIndex >= 0)
        {
            text = text[..colonIndex];
        }

        text = text.Trim().Trim('.');

        if (text.Length == 0)
        {
            throw new CookieParseException("网站域名不能为空");
        }

        if (!SiteRegex.IsMatch(text))
        {
            throw new CookieParseException($"网站域名格式不正确: {Preview(text)}");
        }

        return text;
    }

    /// <summary>由网站域名生成 Cookie 的 domain 值（前缀加 <c>.</c>）。</summary>
    /// <param name="site">网站域名。</param>
    /// <returns>Cookie domain。</returns>
    public static string CookieDomain(string site) => "." + site.TrimStart('.');

    /// <summary>由 Cookie 的 domain 还原网站域名（去掉前导点）。</summary>
    /// <param name="domain">Cookie domain。</param>
    /// <returns>网站域名。</returns>
    public static string SiteFromCookieDomain(string? domain)
        => (domain ?? string.Empty).Trim().ToLowerInvariant().TrimStart('.');

    /// <summary>判断 Cookie 文本属于哪种格式。</summary>
    /// <param name="raw">原始文本。</param>
    /// <returns><c>json</c> 或 <c>semicolon</c>。</returns>
    /// <exception cref="CookieParseException">两种格式都不匹配。</exception>
    public static string DetectFormat(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new CookieParseException("Cookie 内容不能为空");
        }

        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            return "json";
        }

        return text.Contains('=') ? "semicolon"
            : throw new CookieParseException("无法识别的 Cookie 格式，支持分号格式和 JSON 格式");
    }

    /// <summary>
    /// 解析 Cookie 文本，并返回解析过程中的警告信息。
    /// </summary>
    /// <param name="raw">用户粘贴的 Cookie 文本。</param>
    /// <param name="site">用户输入的网站域名（可为 URL）。</param>
    /// <returns>（Cookie 列表, 警告信息列表）。</returns>
    /// <exception cref="CookieParseException">格式无法识别，或没有任何有效 Cookie。</exception>
    public static (List<JsonObject> Cookies, List<string> Warnings) ParseDetailed(string raw, string site)
    {
        var normalizedSite = NormalizeSite(site);
        var defaultDomain = CookieDomain(normalizedSite);
        var warnings = new List<string>();

        var text = (raw ?? string.Empty).Trim();
        var cookies = DetectFormat(text) == "json"
            ? ParseJson(text, defaultDomain, warnings)
            : ParseSemicolon(text, defaultDomain, warnings);

        // 去掉完全重复的条目（同名同 domain 同 path）
        var unique = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cookie in cookies)
        {
            var key = string.Join(
                '\n',
                Json.Str(cookie, "name"),
                Json.Str(cookie, "domain"),
                Json.Str(cookie, "path"));
            if (seen.Add(key))
            {
                unique.Add(cookie);
            }
        }

        if (unique.Count == 0)
        {
            throw new CookieParseException("未解析到任何有效 Cookie，请检查粘贴的内容");
        }

        return (unique, warnings);
    }

    /// <summary>解析 Cookie 文本（简化接口，丢弃警告信息）。</summary>
    /// <param name="raw">用户粘贴的 Cookie 文本。</param>
    /// <param name="site">网站域名。</param>
    /// <returns>Cookie 列表。</returns>
    /// <exception cref="CookieParseException">解析失败。</exception>
    public static List<JsonObject> Parse(string raw, string site)
        => ParseDetailed(raw, site).Cookies;

    /// <summary>
    /// 生成 Cookie 名称列表摘要（不含值），用于界面展示。
    /// </summary>
    /// <param name="cookies">Cookie 列表。</param>
    /// <returns>形如 <c>SESSDATA, bili_jct, DedeUserID</c> 的字符串。</returns>
    public static string Summarize(IEnumerable<JsonObject> cookies)
    {
        var names = cookies
            .Select(cookie => Json.Str(cookie, "name"))
            .Where(name => name.Length > 0)
            .ToList();
        return names.Count == 0 ? "（无）" : string.Join(", ", names);
    }

    /// <summary>截断文本用于提示信息，避免泄露完整 Cookie 内容。</summary>
    /// <param name="text">原始文本。</param>
    /// <returns>截断后的文本。</returns>
    public static string Preview(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= PreviewLimit ? value : value[..PreviewLimit] + "...";
    }

    /// <summary>解析分号分隔的 Cookie 字符串。</summary>
    private static List<JsonObject> ParseSemicolon(string raw, string defaultDomain, List<string> warnings)
    {
        var cookies = new List<JsonObject>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var segments = raw.Split(';');

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index].Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            var separator = segment.IndexOf('=');
            if (separator < 0)
            {
                warnings.Add($"第 {index + 1} 个片段缺少 = 分隔符，已跳过: {Preview(segment)}");
                continue;
            }

            var name = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                warnings.Add($"第 {index + 1} 个片段缺少 Cookie 名称，已跳过");
                continue;
            }

            // 去掉成对的引号
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
            {
                value = value[1..^1];
            }

            var cookie = new JsonObject
            {
                ["name"] = name,
                ["value"] = value,
                ["domain"] = defaultDomain,
                ["path"] = "/",
            };

            if (seen.TryGetValue(name, out var existing))
            {
                warnings.Add($"Cookie 名称重复，已使用最后一个值: {name}");
                cookies[existing] = cookie;
                continue;
            }

            seen[name] = cookies.Count;
            cookies.Add(cookie);
        }

        return cookies;
    }

    /// <summary>解析 JSON 格式的 Cookie 文本。</summary>
    private static List<JsonObject> ParseJson(string raw, string defaultDomain, List<string> warnings)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new CookieParseException($"JSON 格式错误: {ex.Message}", ex);
        }

        List<JsonNode?> items;
        switch (parsed)
        {
            case JsonObject obj when obj["cookies"] is JsonArray inner:
                items = inner.ToList();
                break;
            case JsonObject obj when obj.ContainsKey("name"):
                items = [obj];
                break;
            case JsonArray array:
                // 形如 ["a=1; b=2", "c=3"] 的字符串数组
                if (array.Count > 0 && array.All(entry =>
                        entry is JsonValue value && value.TryGetValue<string>(out _)))
                {
                    var merged = new List<JsonObject>();
                    foreach (var entry in array)
                    {
                        merged.AddRange(ParseSemicolon(
                            entry is JsonValue v && v.TryGetValue<string>(out var text) ? text : string.Empty,
                            defaultDomain,
                            warnings));
                    }

                    return merged;
                }

                items = array.ToList();
                break;
            default:
                throw new CookieParseException("无法识别的 Cookie 格式，支持分号格式和 JSON 格式");
        }

        var cookies = new List<JsonObject>();
        for (var position = 0; position < items.Count; position++)
        {
            var cookie = NormalizeItem(items[position], defaultDomain, position + 1, warnings);
            if (cookie is not null)
            {
                cookies.Add(cookie);
            }
        }

        return cookies;
    }

    /// <summary>把单个 JSON 对象规范化为统一的 Cookie 结构。</summary>
    private static JsonObject? NormalizeItem(JsonNode? item, string defaultDomain, int position, List<string> warnings)
    {
        if (item is not JsonObject obj)
        {
            warnings.Add($"第 {position} 条不是 JSON 对象，已跳过");
            return null;
        }

        var name = obj["name"];
        if (name is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var nameText)
            || nameText.Trim().Length == 0)
        {
            warnings.Add($"第 {position} 条缺少 name 字段，已跳过");
            return null;
        }

        nameText = nameText.Trim();

        var valueNode = obj["value"];
        var value = valueNode is null ? string.Empty : Json.PythonStr(valueNode);

        var domainNode = obj["domain"];
        var domain = domainNode is JsonValue domainValue
            && domainValue.TryGetValue<string>(out var domainText)
            && domainText.Trim().Length > 0
                ? domainText.Trim()
                : defaultDomain;

        var pathNode = obj["path"];
        var path = pathNode is JsonValue pathValue
            && pathValue.TryGetValue<string>(out var pathText)
            && pathText.Trim().Length > 0
                ? pathText.Trim()
                : "/";
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var cookie = new JsonObject
        {
            ["name"] = nameText,
            ["value"] = value,
            ["domain"] = domain,
            ["path"] = path,
        };

        // expires / expirationDate -> expires（float 转 int）
        if (!obj.TryGetPropertyValue("expires", out var rawExpires))
        {
            obj.TryGetPropertyValue("expirationDate", out rawExpires);
        }

        if (rawExpires is not null && Json.PythonStr(rawExpires).Length > 0)
        {
            var expires = Json.ToIntLike(rawExpires);
            if (expires is null)
            {
                warnings.Add($"第 {position} 条 expires 字段非法，已忽略");
            }
            else if (expires > 0)
            {
                cookie["expires"] = expires.Value;
            }
        }

        // 布尔字段透传
        foreach (var key in new[] { "httpOnly", "secure" })
        {
            if (obj.TryGetPropertyValue(key, out var flag) && flag is not null)
            {
                cookie[key] = Json.Truthy(flag);
            }
        }

        // sameSite 映射
        if (!obj.TryGetPropertyValue("sameSite", out var rawSameSite))
        {
            obj.TryGetPropertyValue("same_site", out rawSameSite);
        }

        if (rawSameSite is not null && Json.PythonStr(rawSameSite).Trim().Length > 0)
        {
            var key = Json.PythonStr(rawSameSite).Trim().ToLowerInvariant();
            if (!SameSiteMap.TryGetValue(key, out var mapped))
            {
                warnings.Add($"第 {position} 条 sameSite 取值无法识别（{key}），已忽略");
            }
            else if (mapped is not null)
            {
                cookie["sameSite"] = mapped;
            }
        }

        return cookie;
    }

    private static string FirstSegment(string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index < 0 ? text : text[..index];
    }
}
