using System.Text;
using System.Text.Json.Nodes;

namespace CookieLuncher.Core;

/// <summary>导出失败（空数据 / 加密失败 / 写入失败等）。</summary>
public sealed class ExporterException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public ExporterException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public ExporterException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 导出 Cookie 为 GitHub Action 保活配置文件（与 Python 版 <c>core/exporter.py</c> 输出完全一致）。
/// <para>输出文件只包含 Fernet 密文与站点域名，不含明文 Cookie、不含 encryption_salt、不含用户密码。</para>
/// </summary>
public static class Exporter
{
    /// <summary>建议的 GitHub Secret 名称。</summary>
    public const string GitHubSecretName = "COOKIE_FERNET_KEY";

    /// <summary>输出文件名（生成在程序数据目录）。</summary>
    public const string OutputFileName = "action_config_output.json";

    /// <summary>global 固定请求参数（GitHub Action 保活侧使用，与本地配置无关）。</summary>
    public static JsonObject BuildGlobalConfig() => new()
    {
        ["userAgent"] =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
        ["timeout"] = 30000,
        ["loginInvalidKeyword"] = "请登录",
    };

    /// <summary>把解密后的 Cookie 数据组装为 Action 保活配置结构。</summary>
    /// <param name="data">cookies.enc 解密结果（site → Cookie 数组）。</param>
    /// <param name="key">本地 Fernet 密钥。</param>
    /// <param name="onSkip">回调：收到被跳过的空站点域名。</param>
    /// <returns><c>{"global": {...}, "cookies": [{"site": ..., "fernet_token": ...}]}</c>。</returns>
    /// <exception cref="ExporterException">没有可导出的站点数据，或加密失败。</exception>
    public static JsonObject BuildActionOutput(
        JsonObject data,
        FernetKey key,
        Action<string>? onSkip = null)
    {
        if (data.Count == 0)
        {
            throw new ExporterException("cookies.enc 中没有可导出的站点数据，未生成输出文件。");
        }

        var cookiesOut = new JsonArray();
        foreach (var pair in data)
        {
            var cleaned = new JsonArray();
            if (pair.Value is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject cookie)
                    {
                        cleaned.Add(cookie.DeepClone());
                    }
                }
            }

            if (cleaned.Count == 0)
            {
                onSkip?.Invoke(pair.Key);
                continue;
            }

            string token;
            try
            {
                token = Fernet.Encrypt(
                    Encoding.UTF8.GetBytes(Json.Serialize(cleaned)),
                    key);
            }
            catch (Exception ex)
            {
                throw new ExporterException($"站点 {pair.Key} 的 Cookie 加密失败: {ex.Message}", ex);
            }

            cookiesOut.Add(new JsonObject
            {
                ["site"] = pair.Key,
                ["fernet_token"] = token,
            });
        }

        if (cookiesOut.Count == 0)
        {
            throw new ExporterException("没有可导出的站点 Cookie（全部站点均为空），未生成输出文件。");
        }

        return new JsonObject
        {
            ["global"] = BuildGlobalConfig(),
            ["cookies"] = cookiesOut,
        };
    }

    /// <summary>把输出结构写入 JSON 文件（先写临时文件再替换）。</summary>
    /// <param name="output">组装好的输出结构。</param>
    /// <param name="outPath">目标文件路径（已存在时直接覆盖）。</param>
    /// <returns>写入后的文件路径。</returns>
    /// <exception cref="ExporterException">写入失败。</exception>
    public static string WriteActionOutput(JsonObject output, string outPath)
    {
        var tempPath = outPath + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                Json.Serialize(output, indented: true) + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, outPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExporterException($"无法写入输出文件 {outPath}: {ex.Message}", ex);
        }

        return outPath;
    }

    /// <summary>组装并写出 Action 保活配置文件。</summary>
    /// <param name="data">cookies.enc 解密结果（site → Cookie 数组）。</param>
    /// <param name="key">本地 Fernet 密钥。</param>
    /// <param name="outPath">输出文件路径。</param>
    /// <param name="onSkip">回调：收到被跳过的空站点域名。</param>
    /// <returns>输出文件路径。</returns>
    /// <exception cref="ExporterException">空数据 / 加密失败 / 写入失败。</exception>
    public static string ExportActionConfig(
        JsonObject data,
        FernetKey key,
        string outPath,
        Action<string>? onSkip = null)
    {
        var output = BuildActionOutput(data, key, onSkip);
        return WriteActionOutput(output, outPath);
    }
}
