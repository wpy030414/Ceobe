using System.Text.Json.Serialization;
using Ceobe.Core.Util;

namespace Ceobe.Core.Mods;

/// <summary>
/// 已安装 mod 的记录（存于 instance.json 的 mods[]）。
/// key 与 Java 版逐字节一致，**不得改名**（闸门 B：Java 写的 instance.json 必须能被 C# 读出 mod 清单）。
/// </summary>
public sealed class ModEntry
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha1 { get; set; } = "";

    /// <summary>required / optional / unsupported。</summary>
    public string EnvServer { get; set; } = "";

    public string EnvClient { get; set; } = "";

    [JsonConverter(typeof(InstantJsonConverter))]
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}
