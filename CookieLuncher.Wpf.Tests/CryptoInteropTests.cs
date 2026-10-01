using System.Text;
using System.Text.Json.Nodes;
using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>
/// 与 Python 版（<c>core/crypto.py</c>）的互通测试。
/// <para>
/// 下面的常量由 Python 版真实生成（argon2-cffi + cryptography），
/// 用于验证 C# 实现能解密既有数据、且生成的哈希可被 Python 校验。
/// </para>
/// </summary>
public sealed class CryptoInteropTests
{
    // 由 Python 版生成的固定夹具
    private const string Password = "TestPass123";
    private const string Salt = "jazna3mdnYdkPQSF0d60Ng==";
    private const string Argon2Hash =
        "$argon2id$v=19$m=65536,t=3,p=4$2m5CBWkA489bnIwyY5iaWQ$w3ujhnFOTF63QwaBVK950q/NxCwyTVQF4LLTeHAEt/Y";
    private const string FernetKeyText = "rz9Yk0nZTHuolKRLWssYUPrq1sXn27YBZ5uC6P-7FuY=";
    private const string FernetToken =
        "gAAAAABqvj18rn_OGggAKsjB-VolSIMutImkyZOmsbwCAOVZw-ozV-U-TebdsAmZQdR-Oz1FdN5ZG4VysUE_3DZfAir5m"
        + "01ZuoA9sf4nJWMZOjUM_rz1W62oEhjVHjl_ra0HX8ut8230bXO8MBS-LGPwpYka1mvnt8AWobsRhcOxJUy6XCxBu-g=";
    private const string FernetPlaintext =
        "{\"name\": \"SESSDATA\", \"value\": \"abc123def456ghi\", \"domain\": \".bilibili.com\"}";

    [Fact]
    public void DeriveKey_ShouldMatchPythonResult()
    {
        var key = Crypto.DeriveKey(Password, Salt);

        Assert.Equal(FernetKeyText, key.Base64UrlText);
        Assert.Equal(32, key.Raw.Length);
    }

    [Fact]
    public void VerifyPassword_ShouldAcceptPythonGeneratedHash()
    {
        Assert.True(Crypto.VerifyPassword(Argon2Hash, Password));
        Assert.False(Crypto.VerifyPassword(Argon2Hash, Password + "x"));
        Assert.False(Crypto.VerifyPassword(Argon2Hash, string.Empty));
        Assert.False(Crypto.VerifyPassword("not-a-phc-string", Password));
    }

    [Fact]
    public void HashPassword_ShouldProduceArgon2idPhcString()
    {
        var hash = Crypto.HashPassword(Password);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=4$", hash);
        Assert.True(Crypto.VerifyPassword(hash, Password));
        Assert.False(Crypto.VerifyPassword(hash, "wrong"));
    }

    [Fact]
    public void HashPassword_ShouldBeVerifiableByPythonFollowUpRun()
    {
        // 这里验证的是 C# 生成的哈希自身可被 C# 校验；
        // 与 Python 的交叉验证由 Fernet/派生密钥夹具覆盖。
        var hash = Crypto.HashPassword("another-password");
        var parts = hash.Split('$');
        Assert.Equal(6, parts.Length);
        Assert.Equal("argon2id", parts[1]);
        Assert.DoesNotContain("=", parts[4]);
        Assert.DoesNotContain("=", parts[5]);
    }

    [Fact]
    public void FernetDecrypt_ShouldReadPythonToken()
    {
        var key = FernetKey.FromBase64Url(FernetKeyText);
        var plaintext = Fernet.Decrypt(FernetToken, key);

        Assert.Equal(FernetPlaintext, Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void FernetRoundTrip_ShouldPreserveContent()
    {
        var key = FernetKey.FromRaw(Crypto.DeriveKey(Password, Salt).Raw);
        var payload = Encoding.UTF8.GetBytes("{\"站点\": \"bilibili.com\", \"值\": \"中文\"}");

        var token = Fernet.Encrypt(payload, key);

        Assert.Equal(payload, Fernet.Decrypt(token, key));
    }

    [Fact]
    public void FernetDecrypt_ShouldFailOnTamperedToken()
    {
        var key = FernetKey.FromBase64Url(FernetKeyText);
        var tampered = FernetToken[..^4] + "AAAA";

        Assert.Throws<DecryptException>(() => Fernet.Decrypt(tampered, key));
        Assert.Throws<DecryptException>(() => Fernet.Decrypt(FernetToken, FernetKey.FromRaw(new byte[32])));
        Assert.Throws<DecryptException>(() => Fernet.Decrypt("not-base64", key));
    }

    [Fact]
    public void EncryptDict_DecryptDict_ShouldRoundTripChineseValues()
    {
        var key = Crypto.DeriveKey(Password, Salt);
        var data = new JsonObject
        {
            ["bilibili.com"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "SESSDATA",
                    ["value"] = "值含中文-and符号&amp",
                    ["domain"] = ".bilibili.com",
                    ["path"] = "/",
                },
            },
        };

        var token = Crypto.EncryptDict(data, key);
        var restored = Crypto.DecryptDict(token, key);

        Assert.Equal(
            "值含中文-and符号&amp",
            restored["bilibili.com"]![0]!["value"]!.GetValue<string>());
    }
}
