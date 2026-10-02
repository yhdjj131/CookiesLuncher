using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace CookieLuncher.Core;

/// <summary>
/// 内部 Cookie 结构与 Playwright Cookie 之间的双向转换
/// （与 Python 版 <c>core/browser.py</c> 与 <c>core/cookie_capture.py</c> 的规则一致）。
/// </summary>
public static class PlaywrightCookieConverter
{
    /// <summary>Playwright 可识别的 sameSite 取值。</summary>
    private static readonly string[] SameSiteValues = ["Strict", "Lax", "None"];

    /// <summary>把内部 Cookie 转换为 <c>add_cookies</c> 接受的字段。</summary>
    /// <param name="cookie">内部 Cookie。</param>
    /// <returns>Playwright Cookie；缺少 name / value / domain 时返回 null。</returns>
    public static Cookie? ToPlaywrightCookie(JsonObject cookie)
    {
        var name = Json.Str(cookie, "name");
        var valueNode = cookie["value"];
        var domainNode = cookie["domain"] ?? cookie["host"];

        if (name.Length == 0 || valueNode is null || domainNode is null)
        {
            return null;
        }

        var domain = Json.PythonStr(domainNode).Trim();
        if (domain.Length == 0)
        {
            return null;
        }

        var path = Json.PythonStr(cookie["path"]).Trim();
        var result = new Cookie
        {
            Name = name,
            Value = Json.PythonStr(valueNode),
            Domain = domain,
            Path = path.Length == 0 ? "/" : path,
        };

        var expiresNode = cookie["expires"] ?? cookie["expirationDate"];
        var expires = Json.ToIntLike(expiresNode);
        if (expires is > 0)
        {
            result.Expires = expires.Value;
        }

        if (Json.Truthy(cookie["httpOnly"]) || Json.Truthy(cookie["httponly"]))
        {
            result.HttpOnly = true;
        }

        if (Json.Truthy(cookie["secure"]))
        {
            result.Secure = true;
        }

        var sameSite = Json.PythonStr(cookie["sameSite"]).Trim();
        if (SameSiteValues.Contains(sameSite, StringComparer.Ordinal))
        {
            // Chromium 要求 SameSite=None 必须配合 Secure，否则整批 add_cookies 会失败；
            // 此处仅省略该属性（退回浏览器默认 Lax），不影响登录态本身。
            if (sameSite != "None" || result.Secure == true)
            {
                result.SameSite = sameSite switch
                {
                    "Strict" => SameSiteAttribute.Strict,
                    "Lax" => SameSiteAttribute.Lax,
                    _ => SameSiteAttribute.None,
                };
            }
        }

        return result;
    }

    /// <summary>
    /// 把 <c>context.cookies()</c> 的结果转换为内部存储结构。
    /// <para>
    /// <b>不做域名过滤</b>：所有有效 Cookie 一律保留（保证登录态完整），
    /// 仅剔除无法构成合法条目的数据与完全重复的条目（同名同 domain 同 path）。
    /// </para>
    /// </summary>
    /// <param name="playwrightCookies">Playwright 返回的 Cookie 列表。</param>
    /// <returns>（内部 Cookie 列表, 剔除原因列表）。</returns>
    public static (List<JsonObject> Cookies, List<string> Dropped) NormalizeCaptured(
        IEnumerable<BrowserContextCookiesResult> playwrightCookies)
    {
        var result = new List<JsonObject>();
        var dropped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cookie in playwrightCookies)
        {
            var name = cookie.Name;
            if (string.IsNullOrEmpty(name) || cookie.Value is null || string.IsNullOrEmpty(cookie.Domain))
            {
                dropped.Add($"剔除缺少 name/value/domain 的条目: {(string.IsNullOrEmpty(name) ? "(无名称)" : name)}");
                continue;
            }

            var domain = cookie.Domain.Trim();

            var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
            if (!path.StartsWith('/'))
            {
                path = "/" + path;
            }

            var key = string.Join('\n', name, domain, path);
            if (!seen.Add(key))
            {
                dropped.Add($"剔除重复条目: {name}({domain}{path})");
                continue;
            }

            var item = new JsonObject
            {
                ["name"] = name,
                ["value"] = cookie.Value,
                ["domain"] = domain,
                ["path"] = path,
            };

            if (cookie.Expires > 0)
            {
                item["expires"] = (long)cookie.Expires;
            }

            if (cookie.HttpOnly)
            {
                item["httpOnly"] = true;
            }

            if (cookie.Secure)
            {
                item["secure"] = true;
            }

            var sameSite = cookie.SameSite switch
            {
                SameSiteAttribute.Strict => "Strict",
                SameSiteAttribute.Lax => "Lax",
                SameSiteAttribute.None => "None",
                _ => string.Empty,
            };
            if (sameSite.Length > 0)
            {
                item["sameSite"] = sameSite;
            }

            result.Add(item);
        }

        return (result, dropped);
    }
}
