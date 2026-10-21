using System.Text.Json;
using System.Text.Json.Serialization;
using Ceobe.Core.Util;

namespace Ceobe.Core.Util;

/// <summary>
/// JSON 读写：配置与 Java 版 Jackson 的输出**字节级兼容**（同一 instance.json / settings.json
/// 必须能被两个实现互相读写，见 docs/MIGRATION.md 闸门 B）。
///
/// 对齐点：
/// - 缩进输出（Jackson INDENT_OUTPUT）
/// - null 字段不写出（Jackson <c>@JsonInclude(NON_NULL)</c> 在 InstanceMeta/ModEntry 上）
/// - 日期为 ISO-8601 字符串，非数字时间戳（Jackson JavaTimeModule + 关闭 WRITE_DATES_AS_TIMESTAMPS）
/// - 枚举写出为大写名（Jackson 默认）
/// </summary>
public static class JsonUtil
{
    /// <summary>写盘用：缩进 + 忽略 null。</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读盘用：允许注释与尾逗号（对外 API 响应偶尔带注释）。</summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>解析外部 API 响应为 DOM（等价 Jackson readTree）。</summary>
    public static JsonDocument Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new CeobeException($"JSON 解析失败: {e.Message}", e);
        }
    }

    public static T Parse<T>(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, ReadOptions)
                   ?? throw new CeobeException("JSON 反序列化结果为 null");
        }
        catch (JsonException e)
        {
            throw new CeobeException($"JSON 反序列化失败: {e.Message}", e);
        }
    }

    public static string Write<T>(T value)
    {
        try
        {
            return JsonSerializer.Serialize(value, WriteOptions);
        }
        catch (JsonException e)
        {
            throw new CeobeException($"JSON 序列化失败: {e.Message}", e);
        }
    }
}
