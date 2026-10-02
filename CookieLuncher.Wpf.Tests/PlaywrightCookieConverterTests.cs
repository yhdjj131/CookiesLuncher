using System.Text.Json.Nodes;
using CookieLuncher.Core;
using Microsoft.Playwright;

namespace CookieLuncher.Tests;

/// <summary>内部 Cookie 与 Playwright Cookie 的转换测试。</summary>
public sealed class PlaywrightCookieConverterTests
{
    private static JsonObject Cookie(params (string Key, object Value)[] entries)
    {
        var cookie = new JsonObject();
        foreach (var (key, value) in entries)
        {
            cookie[key] = value switch
            {
                string text => JsonValue.Create(text),
                int number => JsonValue.Create(number),
                long number => JsonValue.Create(number),
                bool flag => JsonValue.Create(flag),
                _ => throw new ArgumentOutOfRangeException(nameof(entries)),
            };
        }

        return cookie;
    }

    [Fact]
    public void ToPlaywrightCookie_ShouldMapAllKnownFields()
    {
        var cookie = Cookie(
            ("name", "SESSDATA"),
            ("value", "abc"),
            ("domain", ".bilibili.com"),
            ("path", "/"),
            ("expires", 1900000000L),
            ("httpOnly", true),
            ("secure", true),
            ("sameSite", "Lax"));

        var playwright = PlaywrightCookieConverter.ToPlaywrightCookie(cookie);

        Assert.NotNull(playwright);
        Assert.Equal("SESSDATA", playwright!.Name);
        Assert.Equal("abc", playwright.Value);
        Assert.Equal(".bilibili.com", playwright.Domain);
        Assert.Equal("/", playwright.Path);
        Assert.Equal(1900000000f, playwright.Expires);
        Assert.True(playwright.HttpOnly);
        Assert.True(playwright.Secure);
        Assert.Equal(SameSiteAttribute.Lax, playwright.SameSite);
    }

    [Fact]
    public void ToPlaywrightCookie_ShouldFallBackToHostAndDefaultPath()
    {
        var cookie = Cookie(("name", "sid"), ("value", "1"), ("host", ".example.com"));

        var playwright = PlaywrightCookieConverter.ToPlaywrightCookie(cookie);

        Assert.NotNull(playwright);
        Assert.Equal(".example.com", playwright!.Domain);
        Assert.Equal("/", playwright.Path);
        Assert.Null(playwright.Expires);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("value")]
    [InlineData("domain")]
    public void ToPlaywrightCookie_ShouldRejectIncompleteCookies(string missing)
    {
        var entries = new List<(string, object)>
        {
            ("name", "a"),
            ("value", "1"),
            ("domain", ".example.com"),
        };
        entries.RemoveAll(entry => entry.Item1 == missing);

        Assert.Null(PlaywrightCookieConverter.ToPlaywrightCookie(Cookie(entries.ToArray())));
    }

    [Fact]
    public void ToPlaywrightCookie_ShouldDropSameSiteNoneWithoutSecure()
    {
        // Chromium 要求 SameSite=None 必须配合 Secure，否则整批 add_cookies 会失败
        var insecure = PlaywrightCookieConverter.ToPlaywrightCookie(
            Cookie(("name", "a"), ("value", "1"), ("domain", ".example.com"), ("sameSite", "None"), ("secure", false)));
        Assert.NotNull(insecure);
        Assert.Null(insecure!.SameSite);

        var secure = PlaywrightCookieConverter.ToPlaywrightCookie(
            Cookie(("name", "a"), ("value", "1"), ("domain", ".example.com"), ("sameSite", "None"), ("secure", true)));
        Assert.Equal(SameSiteAttribute.None, secure!.SameSite);
    }

    [Fact]
    public void ToPlaywrightCookie_ShouldIgnoreUnknownSameSiteValue()
    {
        var playwright = PlaywrightCookieConverter.ToPlaywrightCookie(
            Cookie(("name", "a"), ("value", "1"), ("domain", ".example.com"), ("sameSite", "weird")));

        Assert.Null(playwright!.SameSite);
    }

    [Fact]
    public void NormalizeCaptured_ShouldKeepEverythingAndDropInvalidAndDuplicate()
    {
        var captured = new List<BrowserContextCookiesResult>
        {
            new()
            {
                Name = "SESSDATA",
                Value = "abc",
                Domain = ".bilibili.com",
                Path = "/",
                Expires = 1900000000f,
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteAttribute.Lax,
            },
            new()
            {
                Name = "SESSDATA",
                Value = "dup",
                Domain = ".bilibili.com",
                Path = "/",
                SameSite = SameSiteAttribute.Lax,
            },
            new()
            {
                Name = string.Empty,
                Value = "x",
                Domain = ".bilibili.com",
                Path = "/",
            },
            new()
            {
                Name = "other_site",
                Value = "keep-me",
                Domain = ".other.com",
                Path = "sub",
                Expires = -1f,
                SameSite = SameSiteAttribute.None,
            },
        };

        var (cookies, dropped) = PlaywrightCookieConverter.NormalizeCaptured(captured);

        // 不做域名过滤：其它站点的 Cookie 也要保留，保证登录态完整
        Assert.Equal(2, cookies.Count);
        Assert.Equal(2, dropped.Count);

        var first = cookies[0];
        Assert.Equal(1900000000L, first["expires"]!.GetValue<long>());
        Assert.True(first["httpOnly"]!.GetValue<bool>());
        Assert.Equal("Lax", first["sameSite"]!.GetValue<string>());

        var second = cookies[1];
        Assert.Equal(".other.com", second["domain"]!.GetValue<string>());
        Assert.Equal("/sub", second["path"]!.GetValue<string>());
        Assert.False(second.ContainsKey("expires"));
        Assert.Equal("None", second["sameSite"]!.GetValue<string>());
    }
}
