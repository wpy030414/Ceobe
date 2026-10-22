using System.Collections.Concurrent;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// 穿透会话管理：启动/停止/查询 + Pinggy 到期自动续期。
/// 移植自 Java <c>tunnel/TunnelManager.java</c>，并实现 <see cref="IInstanceTunnels"/>
/// （runtime ↔ tunnel 只经契约互见，不互相引用具体类）。
///
/// <para>时序硬约束（必须与 Java 一致）：会话 55 分钟（PinggyTunnel 定义）；
/// 到期前 5 分钟自动续期；续期失败保留旧记录、30 秒后重试。</para>
///
/// <para>异步化差异：Java 用 <c>ScheduledExecutorService</c> + 阻塞 <c>up()</c>；
/// 这里用 <see cref="Task.Delay(TimeSpan, CancellationToken)"/> 定时器 + 每实例
/// <see cref="SemaphoreSlim"/>（把 Java 的整方法 synchronized 细化到实例粒度——
/// 一次建链可阻塞 45 秒，全局锁会把其它实例一起卡死，语义上 Java 的锁只是防重入，
/// 每实例锁达到同样目的）。续期定时器随 <see cref="Close"/> 一并取消。</para>
///
/// <para>对 Java 的一处防御性修正（不改时序语义）：Java 的续期定时器不随 down 取消，
/// 若「down 后马上重新 up」，旧定时器 50 分钟后会误把**新会话**再重启一次。
/// 这里续期前校验「当前活动会话仍是本定时器服务的那个 session」，孤儿定时器直接放弃。</para>
/// </summary>
public sealed class TunnelManager : IInstanceTunnels
{
    /// <summary>到期前多久续期（硬约束 2）。</summary>
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    /// <summary>续期失败后的重试间隔（硬约束 2）。</summary>
    internal static readonly TimeSpan RenewRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>最短续期延迟：防止 expiresAt 已过 / 时钟抖动导致忙循环（对齐 Java 的 max(1s, delay)）。</summary>
    internal static readonly TimeSpan MinRenewDelay = TimeSpan.FromSeconds(1);

    private readonly IInstanceHost _host;
    private readonly Dictionary<TunnelKind, ITunnelProvider> _providers = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<TunnelKind, TunnelSession>> _active =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile int _webConsolePort = -1;

    public TunnelManager(IInstanceHost host)
    {
        _host = host;
        Register(new PinggyTunnel());
        Register(new CloudflareQuickTunnel());
        Register(new LanDirect());
        Register(new TailscaleTunnel());
    }

    public void Register(ITunnelProvider provider) => _providers[provider.Kind] = provider;

    /// <summary>设置 Web 控制台端口（Cloudflare 通道的指向目标；Java 版 webConsolePort(int)）。</summary>
    public void WebConsolePort(int port) => _webConsolePort = port;

    public bool IsUp(string instance, TunnelKind kind) =>
        _active.TryGetValue(instance, out var m) && m.ContainsKey(kind);

    /// <summary>活动会话（富对象，GUI/CLI 可读取 <see cref="TunnelSession.Diagnostic"/>）。</summary>
    public List<TunnelSession> ActiveSessions(string instance) =>
        _active.TryGetValue(instance, out var m) ? new List<TunnelSession>(m.Values) : [];

    public ITunnelProvider Provider(TunnelKind kind) =>
        _providers.TryGetValue(kind, out var p)
            ? p
            : throw CeobeException.Of("通道未注册: {0}", kind);

    // —— IInstanceTunnels ——

    IReadOnlyList<(string Kind, TunnelReport Report)> IInstanceTunnels.ActiveOf(string instanceName) =>
        ActiveSessions(instanceName)
            .Select(s => (s.Kind.Id(), ToReport(s)))
            .ToList();

    async Task<(string Kind, TunnelReport Report)> IInstanceTunnels.UpAsync(
        string instanceName, string kindId, CancellationToken ct)
    {
        var kind = TunnelKindInfo.FromId(kindId);
        var s = await UpAsync(instanceName, kind, ct).ConfigureAwait(false);
        return (kind.Id(), ToReport(s));
    }

    void IInstanceTunnels.Down(string instanceName, string kindId) =>
        Down(instanceName, TunnelKindInfo.FromId(kindId));

    /// <summary>
    /// 把富会话压平成可过协议的报告。整改项 1(b) 的探测结论经此传出监督进程——
    /// 少了这一步，MC ping 自检的结果只能烂在内存里，界面与 CLI 都看不到。
    /// </summary>
    private static TunnelReport ToReport(TunnelSession s) =>
        new(s.PublicUrl, s.ExpiresAt, s.Verified, s.Diagnostic);

    // —— 主流程 ——

    /// <summary>
    /// 开启通道；目标端口：PINGGY/LAN/TAILSCALE → MC 端口，CLOUDFLARE → Web 控制台端口。
    /// 已在则幂等返回现有会话。
    /// </summary>
    public async Task<TunnelSession> UpAsync(string instance, TunnelKind kind, CancellationToken ct = default)
    {
        // require 先行：实例不存在直接抛（对齐 Java：up 第一句 require）
        var status = _host.Require(instance);
        var gate = _locks.GetOrAdd(instance, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsUp(instance, kind))
            {
                return _active[instance][kind];
            }

            var provider = Provider(kind);
            if (!provider.Available) throw new CeobeException(provider.UnavailableReason);

            // 硬约束 4：玩家通道直指 MC 端口——服务端未就绪时只会得到一个指向空端口的“死”通道，先行拦截。
            if (kind == TunnelKind.PINGGY && !status.State.IsLive())
            {
                throw CeobeException.Of("实例 {0} 未运行，玩家通道需在服务端就绪后开启", instance);
            }

            var localPort = kind switch
            {
                TunnelKind.PINGGY or TunnelKind.LAN or TunnelKind.TAILSCALE => status.Port,
                TunnelKind.CLOUDFLARE => ResolveWebPort(instance),
                _ => throw CeobeException.Of("未知通道: {0}", kind),
            };

            var session = await provider.StartAsync(instance, _host.Dir(instance), localPort,
                line => _host.TunnelLog(instance, line), ct).ConfigureAwait(false);

            _active.GetOrAdd(instance, _ => new ConcurrentDictionary<TunnelKind, TunnelSession>())[kind] = session;
            _host.RememberTunnel(instance, kind.Id(), session.PublicUrl);

            if (kind == TunnelKind.PINGGY) ScheduleRenewal(instance, session);
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    private int ResolveWebPort(string instance)
    {
        if (_webConsolePort <= 0)
        {
            throw new CeobeException("Web 控制台未启动，无法开启 Cloudflare 通道");
        }
        return _webConsolePort;
    }

    private void ScheduleRenewal(string instance, TunnelSession session)
    {
        if (session.ExpiresAt is not { } expiresAt) return;
        var delay = expiresAt - RenewBefore - DateTimeOffset.UtcNow;
        if (delay < MinRenewDelay) delay = MinRenewDelay;
        _ = RenewLoopAsync(instance, session, delay, _lifetime.Token);
    }

    private async Task RenewLoopAsync(string instance, TunnelSession old, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // 管理器已关闭
        }
        await RenewOnceAsync(instance, old, ct).ConfigureAwait(false);
    }

    private async Task RenewOnceAsync(string instance, TunnelSession old, CancellationToken ct)
    {
        try
        {
            if (!IsUp(instance, TunnelKind.PINGGY)) return;
            if (!_active.TryGetValue(instance, out var m) || !m.TryGetValue(TunnelKind.PINGGY, out var current))
                return;
            // 孤儿定时器防护：当前活动会话已不是本定时器服务的那个（用户 down/up 过），放弃本次续期。
            if (!ReferenceEquals(current, old)) return;

            Provider(TunnelKind.PINGGY).Stop(old);
            var fresh = await Provider(TunnelKind.PINGGY).StartAsync(instance, _host.Dir(instance),
                old.LocalPort, line => _host.TunnelLog(instance, line), ct).ConfigureAwait(false);
            m[TunnelKind.PINGGY] = fresh;
            _host.RememberTunnel(instance, TunnelKind.PINGGY.Id(), fresh.PublicUrl);
            ScheduleRenewal(instance, fresh);
        }
        catch (OperationCanceledException)
        {
            // 关闭中，不再重试
        }
        catch (Exception e)
        {
            // 硬约束 2：续期失败保留旧记录，30 秒后重试。
            // （旧记录保留在 active map 中；注意其进程在重试前已被 stop，地址字段仍可见，
            //  这是 Java 原实现的既有行为，保持一致。重试不设上限，与 Java 相同。）
            try
            {
                _host.TunnelLog(instance, "[Ceobe] Pinggy 续期失败（" + e.Message + "），30 秒后重试");
            }
            catch (Exception)
            {
                // 日志通道异常不影响重试
            }
            await RetryRenewalAsync(instance, old, ct).ConfigureAwait(false);
        }
    }

    private async Task RetryRenewalAsync(string instance, TunnelSession old, CancellationToken ct)
    {
        try
        {
            await Task.Delay(RenewRetryDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RenewOnceAsync(instance, old, ct).ConfigureAwait(false);
    }

    /// <summary>关闭某实例的指定通道（幂等）。</summary>
    public void Down(string instance, TunnelKind kind)
    {
        var gate = _locks.GetOrAdd(instance, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (!_active.TryGetValue(instance, out var m)) return;
            if (m.TryRemove(kind, out var session)) Provider(kind).Stop(session);
            if (m.IsEmpty) _active.TryRemove(instance, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>关闭某实例全部通道（对齐 Java：遍历枚举后整体移除）。</summary>
    public void DownAll(string instance)
    {
        foreach (var kind in Enum.GetValues<TunnelKind>())
        {
            Down(instance, kind);
        }
        _active.TryRemove(instance, out _);
    }

    /// <summary>
    /// 关停：取消所有定时器并停掉全部通道（Java AutoCloseable.close）。
    /// 定时器不响应即时取消（已排定），但 <see cref="Close"/> 后不会再有新的续期：
    /// 活动会话已清空，续期首句 IsUp 即返回。
    /// </summary>
    public void Close()
    {
        _lifetime.Cancel();
        foreach (var instance in _active.Keys.ToList())
        {
            DownAll(instance);
        }
    }
}
