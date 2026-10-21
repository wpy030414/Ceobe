using System.Text.Json.Serialization;
using Ceobe.Core.Mods;
using Ceobe.Core.Util;

namespace Ceobe.Core.Model;

/// <summary>
/// 实例持久化元数据（instance.json）。
/// JSON key 与 Java 版 Jackson 输出**字段级**一致（两侧都要能读写同一份文件，闸门 B）。注意缩进形态不同：
/// Jackson 是 "k" : v、System.Text.Json 是 "k": v，故不追求字节相同，只保证字段名/日期格式/null 省略对齐。/
/// 因此属性名**不得改名**（改名会让另一侧静默读回默认值）。
/// </summary>
public sealed class InstanceMeta
{
    public string Name { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ServerType ServerType { get; set; }

    public string GameVersion { get; set; } = "";

    /// <summary>fabric loader 版本，其余类型为 null。</summary>
    public string? LoaderVersion { get; set; }

    public int MemoryMb { get; set; } = 2048;

    public string JvmArgs { get; set; } = "";

    public int Port { get; set; } = 25565;

    /// <summary>null = 由 <c>JavaResolver</c> 解析（自管 JDK > 宿主 JAVA_HOME）。</summary>
    public string? JavaHome { get; set; }

    public string LaunchJar { get; set; } = "server.jar";

    /// <summary>非空时改用参数文件启动（NeoForge）：<c>java @&lt;file&gt; nogui</c>。</summary>
    public string? LaunchArgsFile { get; set; }

    /// <summary>非空时注入 authlib-injector 做三方鉴权：<c>-javaagent:authlib-injector.jar=&lt;url&gt;</c>。</summary>
    public string? AuthlibInjectorUrl { get; set; }

    [JsonConverter(typeof(InstantJsonConverter))]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ModEntry> Mods { get; set; } = [];

    /// <summary>最近一次各通道公网地址（便于重启后展示历史）。</summary>
    public string? LastPinggyAddress { get; set; }

    public string? LastCloudflareUrl { get; set; }
}
