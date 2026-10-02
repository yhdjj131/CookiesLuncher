using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CookieLuncher.Core;

/// <summary>
/// System.Text.Json 公共选项与取值辅助方法。
/// <para>
/// 统一的编码器为 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>，
/// 与 Python 的 <c>json.dumps(..., ensure_ascii=False)</c> 行为一致：
/// 中文等非 ASCII 字符按原样写入，不转义为 <c>\uXXXX</c>。
/// </para>
/// </summary>
public static class Json
{
    /// <summary>不做 HTML 转义的编码器（中文原样输出）。</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>紧凑输出选项（等价 Python <c>json.dumps</c> 默认）。</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = Encoder,
        WriteIndented = false,
    };

    /// <summary>两空格缩进输出选项（等价 Python <c>json.dumps(indent=2)</c>）。</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = Encoder,
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
    };

    /// <summary>把 JSON 节点序列化为字符串。</summary>
    /// <param name="node">待序列化的节点。</param>
    /// <param name="indented">是否两空格缩进。</param>
    /// <returns>JSON 文本。</returns>
    public static string Serialize(JsonNode? node, bool indented = false)
        => node?.ToJsonString(indented ? Indented : Compact) ?? "null";

    /// <summary>解析 JSON 文本。</summary>
    /// <param name="text">JSON 文本。</param>
    /// <returns>解析结果；文本为空白时返回 null。</returns>
    /// <exception cref="JsonException">JSON 非法。</exception>
    public static JsonNode? Parse(string text)
        => string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);

    /// <summary>读取字符串字段，非字符串返回空串。</summary>
    public static string Str(JsonObject? obj, string key)
    {
        if (obj is not null && obj.TryGetPropertyValue(key, out var node)
            && node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return string.Empty;
    }

    /// <summary>
    /// 模仿 Python <c>str(value)</c> 的取值方式：布尔值输出 True / False，数值输出其字面量；
    /// <c>null</c> 与缺失字段返回空串，便于调用方直接使用。
    /// </summary>
    /// <param name="node">JSON 节点。</param>
    /// <returns>字符串表示。</returns>
    public static string PythonStr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return string.Empty;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return flag ? "True" : "False";
            default:
                return node.ToJsonString(Compact);
        }
    }

    /// <summary>
    /// 模仿 Python 的真值判断（<c>bool(value)</c>）：空字符串与 0 为假，非空字符串为真。
    /// </summary>
    /// <param name="node">JSON 节点。</param>
    /// <returns>真值结果。</returns>
    public static bool Truthy(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return false;
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return flag;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text.Length > 0;
            case JsonValue value:
                var number = ToNumber(value);
                return number is null || number.Value != 0;
            default:
                return true;
        }
    }

    /// <summary>
    /// 模仿 Python <c>int(float(value))</c>（截断取整），失败返回 null。
    /// </summary>
    /// <param name="node">JSON 节点。</param>
    /// <returns>整数结果；无法转换时返回 null。</returns>
    public static long? ToIntLike(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        // Python 的 float(True) == 1.0
        if (value.TryGetValue<bool>(out var flag))
        {
            return flag ? 1 : 0;
        }

        var raw = value.TryGetValue<string>(out var text) ? text : value.ToJsonString(Compact);
        if (!double.TryParse(
                raw.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number))
        {
            return null;
        }

        return Clamp(number);
    }

    /// <summary>把 JSON 数值节点转换为 double（非数值返回 null）。</summary>
    private static double? ToNumber(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        var raw = value.TryGetValue<string>(out var text) ? text : value.ToJsonString(Compact);
        return double.TryParse(
            raw.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number)
            ? number
            : null;
    }

    private static long? Clamp(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            return null;
        }

        return number >= long.MaxValue || number <= long.MinValue ? null : (long)number;
    }
}
