using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// 穿透通道类型。移植自 Java <c>tunnel/TunnelKind.java</c>，
/// 新增 <see cref="TAILSCALE"/>（整改项 1(c)：长期联机首选）。
/// 枚举名写入 instance.json 的事件流，不得改名；<see cref="Id"/> 为对外稳定字符串。
/// </summary>
public enum TunnelKind
{
    /// <summary>Pinggy（免登录、裸 TCP、55 分钟会话自动续期）——承载玩家 MC 连接。</summary>
    PINGGY,
    /// <summary>Cloudflare Quick Tunnel（免登录、仅 HTTP）——承载 Web 控制台。</summary>
    CLOUDFLARE,
    /// <summary>局域网直连（无第三方依赖，仅同一网络内可达）。</summary>
    LAN,
    /// <summary>Tailscale 组网直连（稳定内网地址、不过期）——长期联机首选（整改项 1(c)）。</summary>
    TAILSCALE,
}

/// <summary>
/// <see cref="TunnelKind"/> 的对外契约：稳定 id + 中文标签 + 反查。
/// Java 用枚举字段承载；C# 枚举不能有实例字段，拆成静态扩展类（语义等价）。
/// </summary>
public static class TunnelKindInfo
{
    /// <summary>通道 id（写进事件流 / CLI / 控制协议，跨版本稳定）。</summary>
    public static string Id(this TunnelKind k) => k switch
    {
        TunnelKind.PINGGY => "pinggy",
        TunnelKind.CLOUDFLARE => "cloudflare",
        TunnelKind.LAN => "lan",
        TunnelKind.TAILSCALE => "tailscale",
        _ => throw CeobeException.Of("未知通道: {0}", k),
    };

    /// <summary>中文友好名。</summary>
    public static string ZhLabel(this TunnelKind k) => k switch
    {
        TunnelKind.PINGGY => "玩家连接（Pinggy TCP）",
        TunnelKind.CLOUDFLARE => "Web 控制台（Cloudflare）",
        TunnelKind.LAN => "局域网直连",
        TunnelKind.TAILSCALE => "Tailscale 组网直连",
        _ => k.ToString(),
    };

    /// <summary>id → 枚举（忽略大小写）；未知 id 抛业务异常。</summary>
    /// <summary>
    /// id → 枚举（忽略大小写）；未知 id 或 null 抛业务异常。
    /// 签名刻意收 <c>string?</c>：调用方常从 <c>--kind</c> 这类可空选项取值，
    /// 而 null 的语义就是「未指定通道」，应当得到与拼错 id 同样的可诊断报错，
    /// 而不是 NullReferenceException（原实现体内已用 <c>id?.</c> 判空，签名却声明非空）。
    /// </summary>
    public static TunnelKind FromId(string? id)
    {
        switch (id?.ToLowerInvariant())
        {
            case "pinggy": return TunnelKind.PINGGY;
            case "cloudflare": return TunnelKind.CLOUDFLARE;
            case "lan": return TunnelKind.LAN;
            case "tailscale": return TunnelKind.TAILSCALE;
            default: throw CeobeException.Of("未知通道: {0}", (object?)id ?? "(null)");
        }
    }
}
