using System.Text.Json;
using System.Text.Json.Nodes;
using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>config.json 读写与规范化测试。</summary>
public sealed class AppConfigTests : TempRootTest
{
    [Fact]
    public void FromJson_ShouldFillDefaults()
    {
        var config = AppConfig.FromJson(null);

        Assert.Equal(string.Empty, config.PasswordHash);
        Assert.Equal(string.Empty, config.EncryptionSalt);
        Assert.Equal(string.Empty, config.SecondPasswordHash);
        Assert.Equal("INFO", config.LogLevel);
        Assert.Equal(30, config.LogRetentionDays);
        Assert.Equal(AppConfig.BackendThorium, config.BrowserBackend);
        Assert.False(config.Headless);
        Assert.Equal("maximized", config.WindowSize);
        Assert.True(config.IsFirstRun);
    }

    [Fact]
    public void FromJson_ShouldFallBackOnInvalidValues()
    {
        var config = AppConfig.FromJson(new JsonObject
        {
            ["log_level"] = "chatty",
            ["log_retention_days"] = "abc",
            ["browser_backend"] = "opera",
            ["browser"] = new JsonObject { ["window_size"] = "   " },
        });

        Assert.Equal("INFO", config.LogLevel);
        Assert.Equal(30, config.LogRetentionDays);
        Assert.Equal(AppConfig.BackendThorium, config.BrowserBackend);
        Assert.Equal("maximized", config.WindowSize);
    }

    [Fact]
    public void FromJson_ShouldNormaliseCaseAndKeepValidValues()
    {
        var config = AppConfig.FromJson(new JsonObject
        {
            ["log_level"] = "debug",
            ["log_retention_days"] = 7,
            ["browser_backend"] = "SYSTEM",
            ["browser"] = new JsonObject { ["headless"] = true, ["window_size"] = "1280x720" },
        });

        Assert.Equal("DEBUG", config.LogLevel);
        Assert.Equal(7, config.LogRetentionDays);
        Assert.Equal(AppConfig.BackendSystem, config.BrowserBackend);
        Assert.True(config.Headless);
        Assert.Equal("1280x720", config.WindowSize);
    }

    [Fact]
    public void FromJson_ShouldDropDeprecatedAndUnknownBrowserKeysButKeepOtherUnknownKeys()
    {
        var config = AppConfig.FromJson(new JsonObject
        {
            ["modify_mode"] = true,
            ["custom_flag"] = "keep-me",
            ["browser"] = new JsonObject
            {
                ["headless"] = false,
                ["window_size"] = "maximized",
                ["legacy_option"] = "drop-me",
            },
        });

        Assert.False(config.Raw.ContainsKey("modify_mode"));
        Assert.Equal("keep-me", config.Raw["custom_flag"]!.GetValue<string>());
        Assert.False(((JsonObject)config.Raw["browser"]!).ContainsKey("legacy_option"));
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripWithoutBom()
    {
        var config = AppConfig.FromJson(new JsonObject { ["custom_flag"] = "中文值" });
        config.BrowserBackend = AppConfig.BackendSystem;
        config.Headless = true;
        config.LogRetentionDays = 5;
        config.Save();

        Assert.True(File.Exists(AppPaths.ConfigPath));

        var bytes = File.ReadAllBytes(AppPaths.ConfigPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain("\\u", System.Text.Encoding.UTF8.GetString(bytes));

        var reloaded = AppConfig.Load();
        Assert.Equal(AppConfig.BackendSystem, reloaded.BrowserBackend);
        Assert.True(reloaded.Headless);
        Assert.Equal(5, reloaded.LogRetentionDays);
        Assert.Equal("中文值", reloaded.Raw["custom_flag"]!.GetValue<string>());
    }

    [Fact]
    public void Load_ShouldCreateDefaultFileWhenMissing()
    {
        var config = AppConfig.Load();

        Assert.True(File.Exists(AppPaths.ConfigPath));
        Assert.True(config.IsFirstRun);

        // 新建的配置文件必须带 browser_backend（旧版缺失该字段，README 却要求它可切换后端）
        Assert.Equal(AppConfig.BackendThorium, config.Raw["browser_backend"]!.GetValue<string>());
    }

    [Fact]
    public void Load_ShouldRejectBrokenJson()
    {
        File.WriteAllText(AppPaths.ConfigPath, "{ not json }");

        var exception = Assert.Throws<ConfigException>(() => AppConfig.Load());
        Assert.Contains("不是合法的 JSON", exception.Message);
    }

    [Fact]
    public void Load_ShouldRejectNonObjectJson()
    {
        File.WriteAllText(AppPaths.ConfigPath, "[1,2,3]");

        Assert.Throws<ConfigException>(() => AppConfig.Load());
    }

    [Fact]
    public void Load_ShouldTreatEmptyFileAsDefault()
    {
        File.WriteAllText(AppPaths.ConfigPath, "   \n");

        var config = AppConfig.Load();
        Assert.True(config.IsFirstRun);
    }

    [Fact]
    public void Load_ShouldStripUtf8Bom()
    {
        var json = AppConfig.FromJson(null).Raw.ToJsonString();
        File.WriteAllText(AppPaths.ConfigPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var config = AppConfig.Load();

        Assert.True(config.IsFirstRun);
        Assert.Equal("INFO", config.LogLevel);
    }

    [Theory]
    [InlineData("maximized", null)]
    [InlineData("max", null)]
    [InlineData("default", null)]
    [InlineData("1280x720", 1280)]
    [InlineData("1280*720", 1280)]
    [InlineData("1024,768", 1024)]
    [InlineData("nonsense", null)]
    public void ParseWindowSize_ShouldMatchPythonRules(string value, int? expectedWidth)
    {
        var config = AppConfig.FromJson(new JsonObject
        {
            ["browser"] = new JsonObject { ["window_size"] = value },
        });

        var size = config.ParseWindowSize();

        if (expectedWidth is null)
        {
            Assert.Null(size);
            Assert.True(config.IsMaximized());
        }
        else
        {
            Assert.NotNull(size);
            Assert.Equal(expectedWidth, size!.Value.Width);
            Assert.False(config.IsMaximized());
        }
    }

    [Fact]
    public void JsonSerializer_ShouldNotEscapeChinese()
    {
        var json = Json.Serialize(new JsonObject { ["键"] = "值" });
        Assert.Equal("{\"键\":\"值\"}", json);
    }

    [Fact]
    public void Json_ShouldTreatStringNumbersLikePython()
    {
        Assert.Equal(12L, Json.ToIntLike(JsonValue.Create("12.9")));
        Assert.Null(Json.ToIntLike(JsonValue.Create("abc")));
        Assert.Equal(3L, Json.ToIntLike(JsonValue.Create(3.99)));
        Assert.True(Json.Truthy(JsonValue.Create("false")));
        Assert.False(Json.Truthy(JsonValue.Create(string.Empty)));
    }

    [Fact]
    public void Raw_ShouldBeJsonSerializable()
    {
        var config = AppConfig.FromJson(null);
        Assert.Equal(JsonValueKind.Object, JsonNode.Parse(Json.Serialize(config.Raw))!.GetValueKind());
    }
}
