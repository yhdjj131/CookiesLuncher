using System.Text;
using System.Text.Json.Nodes;
using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>导出 GitHub Action 配置测试。</summary>
public sealed class ExporterTests : TempRootTest
{
    private static readonly FernetKey Key = Crypto.DeriveKey("TestPass123", Crypto.GenerateSalt());

    private static JsonObject SampleData() => new()
    {
        ["bilibili.com"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "SESSDATA",
                ["value"] = "abc123",
                ["domain"] = ".bilibili.com",
                ["path"] = "/",
            },
        },
        ["zhihu.com"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "z_c0",
                ["value"] = "中文值",
                ["domain"] = ".zhihu.com",
                ["path"] = "/",
            },
        },
    };

    [Fact]
    public void BuildActionOutput_ShouldMatchPythonStructure()
    {
        var output = Exporter.BuildActionOutput(SampleData(), Key);
        var global = (JsonObject)output["global"]!;
        var cookies = (JsonArray)output["cookies"]!;

        Assert.Equal(3, global.Count);
        Assert.Equal(30000, global["timeout"]!.GetValue<int>());
        Assert.Equal("请登录", global["loginInvalidKeyword"]!.GetValue<string>());
        Assert.Contains("Mozilla/5.0", global["userAgent"]!.GetValue<string>());

        Assert.Equal(2, cookies.Count);
        Assert.Equal(["bilibili.com", "zhihu.com"], cookies.Select(item => item!["site"]!.GetValue<string>()));
    }

    [Fact]
    public void BuildActionOutput_ShouldEncryptEachSiteWithSameKey()
    {
        var output = Exporter.BuildActionOutput(SampleData(), Key);
        var cookies = (JsonArray)output["cookies"]!;

        foreach (var item in cookies)
        {
            var site = item!["site"]!.GetValue<string>();
            var token = item["fernet_token"]!.GetValue<string>();
            var plaintext = Encoding.UTF8.GetString(Fernet.Decrypt(token, Key));

            // 每个 token 解密后应是该站点 Cookie 数组的 JSON 文本
            var decoded = JsonNode.Parse(plaintext)!.AsArray();
            Assert.Equal(
                SampleData()[site]![0]!["name"]!.GetValue<string>(),
                decoded[0]!["name"]!.GetValue<string>());
        }
    }

    [Fact]
    public void BuildActionOutput_ShouldSkipEmptySitesAndReportThem()
    {
        var data = SampleData();
        data["empty.com"] = new JsonArray();

        var skipped = new List<string>();
        var output = Exporter.BuildActionOutput(data, Key, site => skipped.Add(site));

        Assert.Equal(["empty.com"], skipped);
        Assert.Equal(2, ((JsonArray)output["cookies"]!).Count);
    }

    [Fact]
    public void BuildActionOutput_ShouldRejectEmptyData()
    {
        Assert.Throws<ExporterException>(() => Exporter.BuildActionOutput(new JsonObject(), Key));

        var allEmpty = new JsonObject { ["empty.com"] = new JsonArray() };
        Assert.Throws<ExporterException>(() => Exporter.BuildActionOutput(allEmpty, Key));
    }

    [Fact]
    public void ExportActionConfig_ShouldWriteIndentedUtf8File()
    {
        var path = Exporter.ExportActionConfig(SampleData(), Key, AppPaths.ExportPath);

        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'\n', bytes[^1]);

        var text = File.ReadAllText(path);
        Assert.Contains("\n  \"global\"", text);
        Assert.DoesNotContain("encryption_salt", text);
        Assert.DoesNotContain("password", text);
    }
}
