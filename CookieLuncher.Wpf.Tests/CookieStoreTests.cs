using System.Text.Json.Nodes;
using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>cookies.enc 存储测试。</summary>
public sealed class CookieStoreTests : TempRootTest
{
    private const string Password = "TestPass123";

    private FernetKey CreateKey() => Crypto.DeriveKey(Password, Crypto.GenerateSalt());

    private static JsonObject Cookie(string name, string value, string domain = ".bilibili.com")
        => new()
        {
            ["name"] = name,
            ["value"] = value,
            ["domain"] = domain,
            ["path"] = "/",
        };

    [Fact]
    public void EnsureFile_ShouldCreateEmptyEncryptedStore()
    {
        var key = CreateKey();
        var store = new CookieStore(AppPaths.CookiePath, key);

        Assert.False(store.Exists);
        Assert.True(store.EnsureFile());
        Assert.True(store.Exists);
        Assert.False(store.EnsureFile());
        Assert.Empty(store.Load());
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripAllFields()
    {
        var key = CreateKey();
        var store = new CookieStore(AppPaths.CookiePath, key);
        var cookie = Cookie("SESSDATA", "值中文");
        cookie["expires"] = 1900000000L;
        cookie["httpOnly"] = true;
        cookie["secure"] = true;
        cookie["sameSite"] = "Lax";

        store.Save(new JsonObject { ["bilibili.com"] = new JsonArray(cookie) });
        var loaded = store.Load();

        var restored = loaded["bilibili.com"]![0]!.AsObject();
        Assert.Equal("值中文", restored["value"]!.GetValue<string>());
        Assert.Equal(1900000000L, restored["expires"]!.GetValue<long>());
        Assert.True(restored["httpOnly"]!.GetValue<bool>());
        Assert.Equal("Lax", restored["sameSite"]!.GetValue<string>());
    }

    [Fact]
    public void Load_ShouldFailWithWrongKey()
    {
        var store = new CookieStore(AppPaths.CookiePath, CreateKey());
        store.Save(new JsonObject { ["example.com"] = new JsonArray(Cookie("a", "1", ".example.com")) });

        var other = new CookieStore(AppPaths.CookiePath, CreateKey());
        Assert.Throws<DecryptException>(() => other.Load());
    }

    [Fact]
    public void Load_ShouldReturnEmptyForBlankFile()
    {
        File.WriteAllText(AppPaths.CookiePath, "  ");
        Assert.Empty(new CookieStore(AppPaths.CookiePath, CreateKey()).Load());
    }

    [Fact]
    public void Load_ShouldRejectNonListSiteData()
    {
        var key = CreateKey();
        new CookieStore(AppPaths.CookiePath, key).Save(new JsonObject { ["example.com"] = "not-a-list" });

        var exception = Assert.Throws<StorageException>(() => new CookieStore(AppPaths.CookiePath, key).Load());
        Assert.Contains("不是列表", exception.Message);
    }

    [Fact]
    public void ListSites_ShouldSortAndCount()
    {
        var store = new CookieStore(AppPaths.CookiePath, CreateKey());
        store.Save(new JsonObject
        {
            ["zhihu.com"] = new JsonArray(Cookie("z", "1", ".zhihu.com")),
            ["bilibili.com"] = new JsonArray(Cookie("a", "1"), Cookie("b", "2")),
        });

        var sites = store.ListSites();

        Assert.Equal(["bilibili.com", "zhihu.com"], sites.Select(entry => entry.Site));
        Assert.Equal(2, sites[0].Count);
        Assert.Equal(1, sites[1].Count);
    }

    [Fact]
    public void Upsert_ShouldReportOverwriteAndNormaliseKeys()
    {
        var store = new CookieStore(AppPaths.CookiePath, CreateKey());

        Assert.False(store.Upsert("https://BiliBili.com/video", [Cookie("a", "1")]));
        Assert.True(store.Upsert("bilibili.com", [Cookie("a", "2")]));

        var sites = store.ListSites();
        Assert.Single(sites);
        Assert.Equal("bilibili.com", sites[0].Site);

        var cookies = store.Get("BILIBILI.COM");
        Assert.Single(cookies);
        Assert.Equal("2", cookies[0]["value"]!.GetValue<string>());
    }

    [Fact]
    public void Get_ShouldReturnCopyThatDoesNotAffectStore()
    {
        var store = new CookieStore(AppPaths.CookiePath, CreateKey());
        store.Upsert("example.com", [Cookie("a", "1", ".example.com")]);

        var first = store.Get("example.com");
        first.Add(Cookie("b", "2", ".example.com"));

        Assert.Single(store.Get("example.com"));
    }

    [Fact]
    public void Upsert_ShouldRejectEmptyAndOversizedLists()
    {
        var store = new CookieStore(AppPaths.CookiePath, CreateKey());

        Assert.Throws<StorageException>(() => store.Upsert("example.com", []));

        var oversized = Enumerable.Range(0, CookieStore.MaxCookiesPerSite + 1)
            .Select(index => Cookie("c" + index, "v"))
            .ToList();
        Assert.Throws<StorageException>(() => store.Upsert("example.com", oversized));
    }

    [Fact]
    public void Delete_ShouldRemoveSiteAndReportMissing()
    {
        var store = new CookieStore(AppPaths.CookiePath, CreateKey());
        store.Upsert("example.com", [Cookie("a", "1", ".example.com")]);

        Assert.True(store.Delete("https://example.com/"));
        Assert.Empty(store.ListSites());
        Assert.False(store.Delete("example.com"));
    }

    [Fact]
    public void Get_ShouldReturnEmptyForUnknownSite()
        => Assert.Empty(new CookieStore(AppPaths.CookiePath, CreateKey()).Get("nothing.example"));
}
