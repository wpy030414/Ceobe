using System.Text.Json;

namespace Ceobe.Core.Providers;

/// <summary>
/// 对齐 Jackson JsonNode 宽松读取语义的最小视图层（外部 API 响应偶尔缺字段，不得抛 KeyNotFound）。
/// 仅供 Providers 包内部使用——URL 与响应字段解析必须留在 SPI 实现内（ADR-004），不许外溢。
/// </summary>
internal static class JsonView
{
    /// <summary>等价 path(name)：缺失或非对象时返回 Missing。</summary>
    public static JsonElement Prop(this JsonElement e, string name)
    {
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v
            : default; // default 的 ValueKind == Undefined，视为缺失
    }

    public static bool IsMissing(this JsonElement e) =>
        e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    /// <summary>等价 asText(def)：字符串取原文，数字/布尔取字面量，缺失/容器取 def。</summary>
    public static string? AsText(this JsonElement e, string? def = null) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? def,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        _ => def,
    };

    /// <summary>等价 asLong(def)。</summary>
    public static long AsLong(this JsonElement e, long def = -1) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : def;

    /// <summary>等价 asBoolean(def)。</summary>
    public static bool AsBool(this JsonElement e, bool def = false) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => def,
    };

    /// <summary>等价元素数组逐个遍历（非数组返回空）。</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Array.Empty<JsonElement>();

    /// <summary>等价对象属性逐个遍历（非对象返回空）。</summary>
    public static IEnumerable<JsonProperty> Properties(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : Array.Empty<JsonProperty>();
}
