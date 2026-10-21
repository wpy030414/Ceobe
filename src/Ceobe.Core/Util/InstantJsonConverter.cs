using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ceobe.Core.Util;

/// <summary>
/// 时间戳转换器：输出与 Java Jackson JavaTimeModule（关闭 WRITE_DATES_AS_TIMESTAMPS）
/// 写出的 ISO-8601 字符串对齐——UTC、带 Z、**省略尾随零**，最多 6 位小数。
///
/// 为什么需要：System.Text.Json 默认用 "O" 格式固定 7 位小数
/// （2026-10-15T11:31:44.1230000Z），而 Jackson 输出 2026-10-15T11:31:44.123Z。
/// instance.json / settings.json 需要被 Java 与 C# 两个实现互读写（docs/MIGRATION.md 闸门 B），
/// 格式不统一会让对照 diff 全是噪音。读取侧保持宽容（任意位数均可）。
/// </summary>
public sealed class InstantJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            // 容忍数字时间戳（秒）——Jackson 在某些配置下会写 epoch
            return DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64());
        }
        var text = reader.GetString()
                   ?? throw new JsonException("时间戳为 null");
        // 优先 ISO-8601 往返格式，退回通用格式；统一换算为 UTC
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.ToUniversalTime();
        }
        throw new JsonException($"无法解析时间戳: {text}");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Format(value));

    /// <summary>公共：nullable 版本共用同一格式。</summary>
    internal static string Format(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        // FFFFFF：最多 6 位小数且自动省略尾随零，对齐 Java Instant.toString()
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}

/// <summary><see cref="DateTimeOffset?"/> 版本，语义同上。</summary>
public sealed class NullableInstantJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var text = reader.GetString();
        if (text is null) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(InstantJsonConverter.Format(value.Value));
    }
}
