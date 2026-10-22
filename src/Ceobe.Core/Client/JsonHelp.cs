using System.Text.Json;

namespace Ceobe.Core.Client;

/// <summary>
/// 外部响应（版本 JSON / Fabric meta）的宽松读取。
/// 刻意与 Providers 包的 <c>JsonView</c> 分开：那个是 SPI 实现专属（ADR-004），
/// 客户端的版本装配是另一条链路，不该把 internal 工具跨包复用成事实上的公共 API。
/// 语义一致：缺字段返回默认值，绝不抛 KeyNotFound。
/// </summary>
internal static class JsonHelp
{
    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static bool IsMissing(this JsonElement e) =>
        e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    public static string? AsText(this JsonElement e, string? def = null) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? def,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        _ => def,
    };

    public static int AsInt(this JsonElement e, int def = 0) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : def;

    public static long AsLong(this JsonElement e, long def = -1) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : def;

    public static bool AsBool(this JsonElement e, bool def = false) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => def,
    };

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static IEnumerable<JsonProperty> Properties(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? e.EnumerateObject() : [];

    /// <summary>数组里全部取字符串（非字符串元素跳过），用于 rules/arguments 的混合数组。</summary>
    public static IEnumerable<string> Strings(this JsonElement e) =>
        e.Items().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString() ?? "");
}
