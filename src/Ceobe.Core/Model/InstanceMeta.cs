using System.Text.Json.Serialization;
using Ceobe.Core.Mods;
using Ceobe.Core.Util;

namespace Ceobe.Core.Model;

/// <summary>
/// 实例持久化元数据（instance.json）。
/// JSON key 与 Java 版 Jackson 输出逐字节一致（闸门 B 要求两实现互读写），**不得改名**。
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
