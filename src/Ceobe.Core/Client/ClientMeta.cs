using System.Text.Json.Serialization;
using Ceobe.Core.Util;

namespace Ceobe.Core.Client;

/// <summary>
/// 客户端加载器。取值与 Fabric/NeoForge/Forge 的官方命名一致，直接用于拼清单与界面文案。
/// </summary>
public enum ClientLoader
{
    VANILLA,
    FABRIC,
    QUILT,
    NEOFORGE,
    FORGE,
}

/// <summary>
/// 客户端进程状态。刻意与服务端的 <see cref="Model.InstanceState"/> 分开：
/// 客户端没有「创建中/安装中」这类由服务端生命周期派生出来的态，两者的状态机不同构。
/// </summary>
public enum ClientState
{
    STOPPED,
    STARTING,
    RUNNING,
    STOPPING,
    CRASHED,
}

public static class ClientStateText
{
    public static bool IsLive(this ClientState s) =>
        s is ClientState.STARTING or ClientState.RUNNING or ClientState.STOPPING;

    public static string ZhLabel(this ClientState s) => s switch
    {
        ClientState.STARTING => "启动中",
        ClientState.RUNNING => "运行中",
        ClientState.STOPPING => "停止中",
        ClientState.CRASHED => "已崩溃",
        _ => "就绪",
    };
}

/// <summary>
/// 客户端实例的持久化元数据（<c>&lt;home&gt;/clients/&lt;name&gt;/client.json</c>）。
///
/// 为什么与 <see cref="Model.InstanceMeta"/> 分家而不是加个 <c>kind</c> 字段：
/// 服务端元数据里 port / server.properties / tunnel 地址这些字段对客户端毫无意义，
/// 混在一起会让每一处 <c>switch (type)</c> 都要多一个分支，且 instance.json 的字段契约
/// 已经与服务端行为绑定（闸门 B），不该为客户端再背一份历史包袱。
/// 两者的共同点只有「名字即目录」这一条，靠 <see cref="ClientStore"/> 的目录布局对齐。
/// </summary>
public sealed class ClientMeta
{
    public string Name { get; set; } = "";

    public string GameVersion { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClientLoader Loader { get; set; } = ClientLoader.VANILLA;

    /// <summary>加载器版本（Fabric Loader / NeoForge 版本）；原版为 null。</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>堆内存上限（MB）。客户端比服务端吃内存，默认 4096。</summary>
    public int MemoryMb { get; set; } = 4096;

    /// <summary>附加 JVM 参数（不含 -Xmx，堆由 <see cref="MemoryMb"/> 生成）。</summary>
    public string JvmArgs { get; set; } = "";

    /// <summary>附加游戏参数（如 <c>--width 1920 --height 1080</c>）。</summary>
    public string GameArgs { get; set; } = "";

    /// <summary>null = 由 JavaResolver 解析（自管 JDK &gt; JAVA_HOME）。</summary>
    public string? JavaHome { get; set; }

    /// <summary>离线账户名（正版账户由全局客户端设置提供令牌，这里只记「用哪个身份」）。</summary>
    public string PlayerName { get; set; } = "Steve";

    /// <summary>
    /// 配对的 Ceobe 服务端实例名。非空时启动会把它写进 servers.dat 并作为默认直连目标——
    /// 这是客户端编排相对通用启动器的结构性差异（设计稿 04 分区）。
    /// </summary>
    public string? PairedServer { get; set; }

    /// <summary>是否在启动前做依赖体检（默认开；关掉是为了让高级用户能「明知有缺失也先跑」）。</summary>
    public bool PreflightCheck { get; set; } = true;

    [JsonConverter(typeof(InstantJsonConverter))]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonConverter(typeof(InstantJsonConverter))]
    public DateTimeOffset? LastLaunchedAt { get; set; }

    /// <summary>上次会话时长（秒）。用于启动页「上次会话」卡片。</summary>
    public int? LastSessionSeconds { get; set; }

    /// <summary>上次退出方式：normal / crashed / unknown。</summary>
    public string? LastExit { get; set; }

    /// <summary>侧栏第二行：版本 + 加载器，一眼认出是哪个实例。</summary>
    [JsonIgnore]
    public string DetailLine => Loader == ClientLoader.VANILLA
        ? $"{GameVersion} · 原版"
        : $"{GameVersion} · {Loader.ToString().ToLowerInvariant()} {LoaderVersion}";
}

/// <summary>
/// 全局客户端设置（<c>&lt;home&gt;/client-settings.json</c>）。
/// 账户与 Java 运行时挂在全局、实例只引用——否则每建一个实例都要重配一遍 Java 路径，
/// 维护成本会随实例数线性上涨（设计稿 05 说明板的前提）。
/// </summary>
public sealed class ClientSettings
{
    /// <summary>当前使用的账户名（对应 <see cref="Accounts"/> 里的一项）。</summary>
    public string? ActiveAccount { get; set; }

    public List<ClientAccount> Accounts { get; set; } = [];

    /// <summary>全局默认 JVM 参数；实例的 JvmArgs 会追加在它后面。</summary>
    public string JvmArgs { get; set; } = "-XX:+UseG1GC -XX:MaxGCPauseMillis=200";

    /// <summary>全局默认 Java 目录；实例未指定时用它。</summary>
    public string? JavaHome { get; set; }

    /// <summary>上次新建时选过的版本，作为新建对话框的默认选中项。</summary>
    public string? LastGameVersion { get; set; }

    /// <summary>新建实例时默认的加载器。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ClientLoader LastLoader { get; set; } = ClientLoader.FABRIC;

    public static string FilePath() => Path.Combine(Store.CeobeHome.Root(), "client-settings.json");

    public static ClientSettings Load()
    {
        var f = FilePath();
        if (!File.Exists(f)) return new ClientSettings();
        try
        {
            return JsonUtil.Parse<ClientSettings>(File.ReadAllText(f, new System.Text.UTF8Encoding(false)))
                   ?? new ClientSettings();
        }
        catch (Exception)
        {
            // 设置损坏不致命：回落默认（与服务端 AppSettings 同策略）
            return new ClientSettings();
        }
    }

    public bool Save()
    {
        try
        {
            Store.CeobeHome.Ensure(Store.CeobeHome.Root());
            File.WriteAllText(FilePath(), JsonUtil.Write(this), new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>当前生效的账户；未设置时给一个离线默认身份，保证「装了就能进游戏」。</summary>
    public ClientAccount ActiveOrDefault()
    {
        var active = Accounts.FirstOrDefault(a =>
            string.Equals(a.Name, ActiveAccount, StringComparison.OrdinalIgnoreCase));
        return active ?? new ClientAccount { Name = "Player", Kind = AccountKind.OFFLINE };
    }

    public void Upsert(ClientAccount account)
    {
        Accounts.RemoveAll(a => string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase));
        Accounts.Add(account);
    }
}

public enum AccountKind
{
    OFFLINE,
    MICROSOFT,
    AUTHLIB,
}

/// <summary>
/// 一个可用身份。离线账户只靠名字派生 UUID（<see cref="Players.OfflinePlayerUuid"/>）；
/// 正版账户额外带访问令牌，由外部授权流程写入，Core 只负责消费。
/// </summary>
public sealed class ClientAccount
{
    public string Name { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccountKind Kind { get; set; } = AccountKind.OFFLINE;

    /// <summary>32 位无连字符串 UUID（Minecraft 启动参数要求的形式）。离线账户由名字派生。</summary>
    public string? Uuid { get; set; }

    public string? AccessToken { get; set; }

    [JsonConverter(typeof(InstantJsonConverter))]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonIgnore]
    public bool IsExpired => ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow;

    /// <summary>启动参数用的 uuid：离线账户按服务端同一算法派生，别处不要各算一份。</summary>
    public string EffectiveUuid => string.IsNullOrWhiteSpace(Uuid)
        ? Players.OfflinePlayerUuid.Compute(Name).Replace("-", "")
        : Uuid!.Replace("-", "").ToLowerInvariant();

    /// <summary>正版令牌缺失时只能用离线身份进游戏，界面必须如实说明。</summary>
    public string KindLabel => Kind switch
    {
        AccountKind.MICROSOFT => IsExpired ? "微软账户 · 登录已过期" : "微软账户 · 正版验证有效",
        AccountKind.AUTHLIB => "第三方鉴权账户",
        _ => "离线账户 · 仅本地可用",
    };
}
