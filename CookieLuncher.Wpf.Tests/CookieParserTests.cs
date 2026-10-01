using System.Text.Json.Nodes;
using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>Cookie 文本解析测试（对照 Python 版 <c>core/cookie_parser.py</c> 的行为）。</summary>
public sealed class CookieParserTests
{
    [Theory]
    [InlineData("bilibili.com", "bilibili.com")]
    [InlineData(".BiliBili.COM", "bilibili.com")]
    [InlineData("https://www.bilibili.com/video/BV1xx?p=1#t", "www.bilibili.com")]
    [InlineData("bilibili.com:443", "bilibili.com")]
    [InlineData("user@bilibili.com", "bilibili.com")]
    [InlineData("  zhihu.com  ", "zhihu.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void NormalizeSite_ShouldReturnPlainDomain(string input, string expected)
        => Assert.Equal(expected, CookieParser.NormalizeSite(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[::1]")]
    [InlineData("not a domain")]
    [InlineData("http://")]
    public void NormalizeSite_ShouldRejectInvalidInput(string input)
        => Assert.Throws<CookieParseException>(() => CookieParser.NormalizeSite(input));

    [Fact]
    public void CookieDomain_ShouldPrefixDot()
    {
        Assert.Equal(".bilibili.com", CookieParser.CookieDomain("bilibili.com"));
        Assert.Equal(".bilibili.com", CookieParser.CookieDomain(".bilibili.com"));
        Assert.Equal("bilibili.com", CookieParser.SiteFromCookieDomain(".BiliBili.com"));
    }

    [Theory]
    [InlineData("SESSDATA=abc", "semicolon")]
    [InlineData("SESSDATA=abc; bili_jct=def", "semicolon")]
    [InlineData("[]", "json")]
    [InlineData("{\"name\":\"a\",\"value\":\"b\"}", "json")]
    public void DetectFormat_ShouldIdentifyFormat(string input, string expected)
        => Assert.Equal(expected, CookieParser.DetectFormat(input));

    [Fact]
    public void DetectFormat_ShouldRejectUnknown()
        => Assert.Throws<CookieParseException>(() => CookieParser.DetectFormat("no-separator-here"));

    [Fact]
    public void ParseSemicolon_ShouldBuildCookiesWithDefaultDomain()
    {
        var (cookies, warnings) = CookieParser.ParseDetailed("SESSDATA=abc; bili_jct=def", "https://bilibili.com/x");

        Assert.Empty(warnings);
        Assert.Equal(2, cookies.Count);
        Assert.Equal("SESSDATA", cookies[0]["name"]!.GetValue<string>());
        Assert.Equal("abc", cookies[0]["value"]!.GetValue<string>());
        Assert.Equal(".bilibili.com", cookies[0]["domain"]!.GetValue<string>());
        Assert.Equal("/", cookies[0]["path"]!.GetValue<string>());
    }

    [Fact]
    public void ParseSemicolon_ShouldStripQuotesAndKeepLastDuplicatedValue()
    {
        var (cookies, warnings) = CookieParser.ParseDetailed("a=\"1\"; b=2; a=3", "example.com");

        Assert.Equal(2, cookies.Count);
        Assert.Equal("3", cookies[0]["value"]!.GetValue<string>());
        Assert.Contains(warnings, warning => warning.Contains("重复"));
    }

    [Fact]
    public void ParseSemicolon_ShouldWarnOnBrokenSegment()
    {
        var (cookies, warnings) = CookieParser.ParseDetailed("ok=1; broken=; =novalue", "example.com");

        // 与 Python 一致："broken=" 仍是合法条目（值为空串），只有缺少名称的片段被跳过并告警
        Assert.Equal(2, cookies.Count);
        Assert.Equal("broken", cookies[1]["name"]!.GetValue<string>());
        Assert.Equal(string.Empty, cookies[1]["value"]!.GetValue<string>());
        Assert.Contains(warnings, warning => warning.Contains("缺少 Cookie 名称"));

        var (_, noSeparatorWarnings) = CookieParser.ParseDetailed("ok=1; broken", "example.com");
        Assert.Contains(noSeparatorWarnings, warning => warning.Contains("缺少 = 分隔符"));
    }

    [Fact]
    public void ParseJson_ShouldHandleCookieEditorExport()
    {
        const string json = """
        [
          {"name":"SESSDATA","value":"abc","domain":".bilibili.com","path":"/","expirationDate":1900000000.5,
           "httpOnly":true,"secure":true,"sameSite":"no_restriction","hostOnly":false,"session":false,"storeId":"0"},
          {"name":"bili_jct","value":"def","sameSite":"unspecified","path":"api"}
        ]
        """;

        var (cookies, warnings) = CookieParser.ParseDetailed(json, "bilibili.com");

        Assert.Empty(warnings);
        Assert.Equal(2, cookies.Count);

        var first = cookies[0];
        Assert.Equal("SESSDATA", first["name"]!.GetValue<string>());
        Assert.Equal(".bilibili.com", first["domain"]!.GetValue<string>());
        Assert.Equal(1900000000, first["expires"]!.GetValue<long>());
        Assert.True(first["httpOnly"]!.GetValue<bool>());
        Assert.True(first["secure"]!.GetValue<bool>());
        Assert.Equal("None", first["sameSite"]!.GetValue<string>());
        Assert.False(first.ContainsKey("hostOnly"));

        var second = cookies[1];
        Assert.Equal(".bilibili.com", second["domain"]!.GetValue<string>());
        Assert.Equal("/api", second["path"]!.GetValue<string>());
        Assert.False(second.ContainsKey("sameSite"));
        Assert.False(second.ContainsKey("expires"));
    }

    [Fact]
    public void ParseJson_ShouldHandleWrappedAndSingleObjectAndStringArray()
    {
        var wrapped = CookieParser.Parse("{\"cookies\":[{\"name\":\"a\",\"value\":\"1\"}]}", "example.com");
        Assert.Single(wrapped);

        var single = CookieParser.Parse("{\"name\":\"b\",\"value\":\"2\"}", "example.com");
        Assert.Single(single);
        Assert.Equal("b", single[0]["name"]!.GetValue<string>());

        var strings = CookieParser.Parse("[\"x=1; y=2\"]", "example.com");
        Assert.Equal(2, strings.Count);
    }

    [Fact]
    public void Parse_ShouldDropDuplicateEntries()
    {
        const string json = """
        [
          {"name":"a","value":"1","domain":".example.com","path":"/"},
          {"name":"a","value":"2","domain":".example.com","path":"/"},
          {"name":"a","value":"3","domain":".example.com","path":"/sub"}
        ]
        """;

        var cookies = CookieParser.Parse(json, "example.com");

        Assert.Equal(2, cookies.Count);
        Assert.Equal("1", cookies[0]["value"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_ShouldRejectEmptyOrUnusableInput()
    {
        Assert.Throws<CookieParseException>(() => CookieParser.Parse("", "example.com"));
        Assert.Throws<CookieParseException>(() => CookieParser.Parse("[]", "example.com"));
        Assert.Throws<CookieParseException>(() => CookieParser.Parse("[{\"value\":\"x\"}]", "example.com"));
        Assert.Throws<CookieParseException>(() => CookieParser.Parse("{not json}", "example.com"));
    }

    [Fact]
    public void Summarize_ShouldListNamesOnly()
    {
        var cookies = CookieParser.Parse("a=1; b=2", "example.com");
        Assert.Equal("a, b", CookieParser.Summarize(cookies));
        Assert.Equal("（无）", CookieParser.Summarize([]));
    }
}
