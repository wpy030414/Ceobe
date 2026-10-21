using System.Diagnostics;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// 一次穿透会话。移植自 Java <c>tunnel/TunnelSession.java</c>，新增两个整改字段：
/// <list type="bullet">
/// <item><see cref="Diagnostic"/>：公网侧就绪探测的结论（整改项 1(b)）。为 null 表示未探测或不适用。</item>
/// <item><see cref="Verified"/>：探测是否拿到合法 MC status 响应。null = 未验证（≠ 失败）。</item>
/// </list>
/// 可变字段全部 <c>volatile</c>（Java 同款语义）：日志泵线程写、管理线程读。
/// </summary>
public sealed class TunnelSession
{
    public TunnelSession(TunnelKind kind, string instance, int localPort, string? publicUrl,
                         DateTimeOffset? expiresAt)
    {
        Kind = kind;
        Instance = instance;
        LocalPort = localPort;
        PublicUrl = publicUrl;
        ExpiresAt = expiresAt;
    }

    public TunnelKind Kind { get; }
    public string Instance { get; }
    public int LocalPort { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>公网地址（Pinggy=tcp://host:port，Cloudflare=https://…，LAN/Tailscale=ip:port）。日志泵线程发现后即写入。</summary>
    public volatile string? PublicUrl;

    /// <summary>
    /// 到期时间（null = 不过期）。
    /// 注：C# 的 <c>volatile</c> 不适用于可空值类型（CS0677），故用普通属性；
    /// 该字段仅在建立/续期时由管理线程写，读取方是同一线程或 UI 轮询，无需 volatile 语义。
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// MC ping 验证结论：true=已验证可达，false=无法验证，null=未做验证。
    /// 三态用 int 编码 + Interlocked 保证跨线程原子读写（volatile bool? 同样不合法）。
    /// </summary>
    private int _verified; // 0=未验证 1=已验证 2=无法验证

    public bool? Verified
    {
        get => Volatile.Read(ref _verified) switch { 1 => true, 2 => false, _ => null };
        set => Interlocked.Exchange(ref _verified, value switch { true => 1, false => 2, _ => 0 });
    }

    /// <summary>诊断文本（验证结论 + 依据），可直接展示给训练员。</summary>
    public volatile string? Diagnostic;

    /// <summary>隧道子进程句柄；由 <see cref="TunnelProcess"/> 写入，stop 后置 null。</summary>
    internal volatile Process? Process;

    /// <summary><paramref name="within"/> 之内是否到期（不过期恒 false）。</summary>
    public bool IsExpiringSoon(TimeSpan within) =>
        ExpiresAt is { } e && DateTimeOffset.UtcNow.Add(within) > e;
}
