using System.Text.RegularExpressions;

namespace Ceobe.Core.Model;

/// <summary>服务端类型（与 provider 一一对应）。枚举名会被写进 instance.json，不得改名。</summary>
public enum ServerType
{
    VANILLA,
    PAPER,
    FABRIC,
    NEOFORGE,
    PURPUR,
}

public static class ServerTypeInfo
{
    public static string ZhLabel(this ServerType t) => t switch
    {
        ServerType.VANILLA => "原版",
        ServerType.PAPER => "Paper（高性能插件服）",
        ServerType.FABRIC => "Fabric（模组服）",
        ServerType.NEOFORGE => "NeoForge（模组服）",
        ServerType.PURPUR => "Purpur（Paper 魔改）",
        _ => t.ToString(),
    };

    /// <summary>是否支持 mods/ 目录（Modrinth 模组安装）。原版与 Paper 无模组加载器。</summary>
    public static bool HasModsDir(this ServerType t) =>
        t is ServerType.FABRIC or ServerType.NEOFORGE or ServerType.PURPUR;

    /// <summary>对应的 Modrinth 加载器 id（fabric/neoforge）；无模组加载器的类型返回 null。</summary>
    public static string? LoaderId(this ServerType t) => t switch
    {
        ServerType.FABRIC => "fabric",
        ServerType.NEOFORGE => "neoforge",
        _ => null,
    };

    /// <summary>Modrinth 加载器 id → 服务端类型；不支持时抛业务异常。</summary>
    public static ServerType FromLoaderId(string? loaderId) => loaderId switch
    {
        "fabric" => ServerType.FABRIC,
        "neoforge" => ServerType.NEOFORGE,
        _ => throw Util.CeobeException.Of("暂不支持该整合包加载器: {0}（当前支持 fabric / neoforge）", loaderId),
    };
}

/// <summary>实例运行状态机：CREATED→STARTING→RUNNING→STOPPING→STOPPED；异常退出→CRASHED。</summary>
public enum InstanceState
{
    CREATED,
    STARTING,
    RUNNING,
    STOPPING,
    STOPPED,
    CRASHED,
}

public static class InstanceStateInfo
{
    public static string ZhLabel(this InstanceState s) => s switch
    {
        InstanceState.CREATED => "已创建",
        InstanceState.STARTING => "启动中",
        InstanceState.RUNNING => "运行中",
        InstanceState.STOPPING => "停止中",
        InstanceState.STOPPED => "已停止",
        InstanceState.CRASHED => "崩溃",
        _ => s.ToString(),
    };

    /// <summary>处于「进程存在或正在过渡」的活跃态。</summary>
    public static bool IsLive(this InstanceState s) =>
        s is InstanceState.STARTING or InstanceState.RUNNING or InstanceState.STOPPING;
}

/// <summary>创建实例的输入规格。</summary>
public sealed record InstanceSpec(
    string Name,
    ServerType ServerType,
    string GameVersion,
    string? LoaderVersion,   // null = 最新 stable loader
    int MemoryMb,
    string? JvmArgs,
    int Port,
    string? JavaHome)        // null = 由 JavaResolver 解析
{
    public const int MinMem = 512;

    private static readonly Regex NameRegex = new("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

    /// <summary>校验并返回自身，便于链式使用（对齐 Java record 的紧凑构造器语义）。</summary>
    public InstanceSpec Validate()
    {
        if (Name is null || !NameRegex.IsMatch(Name))
        {
            throw new Util.CeobeSpec("实例名只能包含小写字母/数字/下划线/连字符，长度 1-32");
        }
        if (MemoryMb < MinMem)
        {
            throw new Util.CeobeSpec($"内存至少 {MinMem}MB");
        }
        if (Port is < 1 or > 65535)
        {
            throw new Util.CeobeSpec("端口必须在 1-65535 之间");
        }
        return this;
    }
}
