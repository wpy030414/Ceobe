using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// TunnelManager 装配与语义测试（不含真实子进程/网络：全部走假 provider 与假 host）。
/// 覆盖硬约束 4（未就绪拦截）、幂等 up、ActiveOf 契约映射、down 停止、续期时序接线。
/// 续期时序语义（55 分钟会话 / 提前 5 分钟 / 失败 30 秒重试）以常量断言 +
/// 「tick 会话」压缩时间线方式验证，不真实等待 50 分钟。
/// </summary>
public class TunnelManagerTest
{
    // —— 假地基 ——

    private sealed class FakeHost : IInstanceHost
    {
        public InstanceState State = InstanceState.RUNNING;
        public readonly List<(string Kind, string? Url)> Remembered = [];
        public readonly List<string> Logs = [];

        public InstanceStatus Require(string name) =>
            new(new InstanceMeta { Name = name, Port = 25565 }, State, 1, true);

        public string Dir(string name) => Path.GetTempPath();

        public void RememberTunnel(string name, string kindId, string? publicUrl) =>
            Remembered.Add((kindId, publicUrl));

        public void TunnelLog(string name, string line) => Logs.Add(line);
    }

    private sealed class FakeProvider(TunnelKind kind, TimeSpan? sessionLength) : ITunnelProvider
    {
        public int StartCount;
        public readonly List<TunnelSession> Stopped = [];
        public Func<CancellationToken, Task>? FailLaterStarts; // 仅续期路径注入（StartCount>=2 时触发一次）

        public TunnelKind Kind => kind;
        public bool Available => true;
        public string UnavailableReason => "fake 不可用";

        public async Task<TunnelSession> StartAsync(string instance, string instanceDir, int localPort,
            Action<string> logSink, CancellationToken ct = default)
        {
            StartCount++;
            if (StartCount >= 2 && FailLaterStarts is { } fl)
            {
                var hook = fl;
                FailLaterStarts = null; // 只失败这一次，之后恢复可用（模拟瞬时网络抖动）
                await hook(ct);
            }
            var expires = sessionLength is { } len
                ? (DateTimeOffset?)DateTimeOffset.UtcNow.Add(len)
                : null;
            var s = new TunnelSession(kind, instance, localPort, kind.Id() + "://fake:" + (10000 + StartCount), expires);
            logSink("fake start " + StartCount);
            return s;
        }

        public void Stop(TunnelSession session) => Stopped.Add(session);
    }

    private sealed record Harness(TunnelManager M, FakeHost H, Dictionary<TunnelKind, FakeProvider> P)
    {
        public IInstanceTunnels View => M;
    }

    private static Harness NewHarness(TimeSpan? pinggyLength = null)
    {
        var h = new FakeHost();
        var m = new TunnelManager(h);
        var providers = new Dictionary<TunnelKind, FakeProvider>
        {
            [TunnelKind.PINGGY] = new FakeProvider(TunnelKind.PINGGY, pinggyLength),
            [TunnelKind.CLOUDFLARE] = new FakeProvider(TunnelKind.CLOUDFLARE, null),
            [TunnelKind.LAN] = new FakeProvider(TunnelKind.LAN, null),
            [TunnelKind.TAILSCALE] = new FakeProvider(TunnelKind.TAILSCALE, null),
        };
        foreach (var p in providers.Values) m.Register(p);
        return new Harness(m, h, providers);
    }

    /// <summary>
    /// 「压缩时间线」用的会话长度：expiresAt - RenewBefore(5min) ≈ 2 秒未来。
    /// 首次续期 ~2s 触发，而新会话的下次续期在 ~5min 后——测试窗口内不会形成循环。
    /// </summary>
    private static readonly TimeSpan TickSession = TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(2));

    private static async Task<TunnelSession> WaitReplacedAsync(TunnelManager m, TunnelSession old, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            var sessions = m.ActiveSessions("alpha");
            if (sessions.Count == 1 && !ReferenceEquals(sessions[0], old)) return sessions[0];
            await Task.Delay(100);
        }
        throw new TimeoutException("续期未在窗口内替换会话");
    }

    // —— 用例 ——

    [Fact(DisplayName = "硬约束2：会话 55 分钟、提前 5 分钟续期、失败 30 秒重试（常量对齐）")]
    public void 续期常量对齐Java()
    {
        Assert.Equal(TimeSpan.FromMinutes(55), PinggyTunnel.Session);
        Assert.Equal(TimeSpan.FromMinutes(5), TunnelManager.RenewBefore);
        Assert.Equal(TimeSpan.FromSeconds(30), TunnelManager.RenewRetryDelay);
    }

    [Fact(DisplayName = "硬约束4：实例未就绪时 PINGGY 被拦截，且不启动 provider")]
    public async Task 未就绪拦截玩家通道()
    {
        var x = NewHarness();
        x.H.State = InstanceState.STOPPED;
        var e = await Assert.ThrowsAsync<CeobeException>(() => x.M.UpAsync("alpha", TunnelKind.PINGGY));
        Assert.Contains("需在服务端就绪后开启", e.Message);
        Assert.Equal(0, x.P[TunnelKind.PINGGY].StartCount);
    }

    [Theory]
    [InlineData(InstanceState.RUNNING)]
    [InlineData(InstanceState.STARTING)]
    [InlineData(InstanceState.STOPPING)]
    public async Task IsLive态均放行玩家通道(InstanceState state)
    {
        var x = NewHarness();
        x.H.State = state;
        var s = await x.M.UpAsync("alpha", TunnelKind.PINGGY);
        Assert.NotNull(s.PublicUrl);
    }

    [Fact(DisplayName = "up 幂等：重复 up 返回同一会话，不重复起进程")]
    public async Task 重复up幂等()
    {
        var x = NewHarness();
        var first = await x.M.UpAsync("alpha", TunnelKind.LAN);
        var again = await x.M.UpAsync("alpha", TunnelKind.LAN);
        Assert.Same(first, again);
        Assert.Equal(1, x.P[TunnelKind.LAN].StartCount);
    }

    [Fact(DisplayName = "up 记录 RememberTunnel(kindId,url) 并置 IsUp/ActiveOf")]
    public async Task up落盘与查询()
    {
        var x = NewHarness();
        await x.M.UpAsync("alpha", TunnelKind.LAN);
        var (kindId, url) = Assert.Single(x.H.Remembered);
        Assert.Equal("lan", kindId);
        Assert.NotNull(url);
        Assert.True(x.M.IsUp("alpha", TunnelKind.LAN));
        var view = x.View.ActiveOf("alpha");
        var item = Assert.Single(view);
        Assert.Equal("lan", item.Kind);
        // ActiveOf 现在返回 (Kind, Report)：报告里才带 Url/ExpiresAt/Verified/Diagnostic
        Assert.Equal(url, item.Report.Url);
        Assert.Null(item.Report.ExpiresAt); // 不过期通道
    }

    [Fact(DisplayName = "硬约束5：PINGGY/LAN/TAILSCALE 指向 meta.Port；CLOUDFLARE 指向 webConsolePort")]
    public async Task 端口指向()
    {
        var x = NewHarness();
        var pg = await x.M.UpAsync("alpha", TunnelKind.PINGGY);
        Assert.Equal(25565, pg.LocalPort);
        var lan = await x.M.UpAsync("alpha", TunnelKind.LAN);
        Assert.Equal(25565, lan.LocalPort);
        var tail = await x.M.UpAsync("alpha", TunnelKind.TAILSCALE);
        Assert.Equal(25565, tail.LocalPort);

        var e = await Assert.ThrowsAsync<CeobeException>(() => x.M.UpAsync("alpha", TunnelKind.CLOUDFLARE));
        Assert.Contains("Web 控制台未启动", e.Message);
        x.M.WebConsolePort(9987);
        var cf = await x.M.UpAsync("alpha", TunnelKind.CLOUDFLARE);
        Assert.Equal(9987, cf.LocalPort);
    }

    [Fact(DisplayName = "down 停止会话并从活动表移除；再 down 幂等")]
    public async Task down停止并移除()
    {
        var x = NewHarness();
        var s = await x.M.UpAsync("alpha", TunnelKind.LAN);
        x.M.Down("alpha", TunnelKind.LAN);
        Assert.Contains(s, x.P[TunnelKind.LAN].Stopped);
        Assert.False(x.M.IsUp("alpha", TunnelKind.LAN));
        x.M.Down("alpha", TunnelKind.LAN); // 幂等
        Assert.Empty(x.View.ActiveOf("alpha"));
    }

    [Fact(DisplayName = "downAll 停全部并清表")]
    public async Task downAll清空()
    {
        var x = NewHarness();
        await x.M.UpAsync("alpha", TunnelKind.LAN);
        await x.M.UpAsync("alpha", TunnelKind.PINGGY);
        x.M.DownAll("alpha");
        Assert.Single(x.P[TunnelKind.LAN].Stopped);
        Assert.Single(x.P[TunnelKind.PINGGY].Stopped);
        Assert.Empty(x.View.ActiveOf("alpha"));
    }

    [Fact(DisplayName = "IInstanceTunnels.UpAsync 按 kindId 字符串开启；未知 id 抛业务异常")]
    public async Task 接口按id开启与未知id()
    {
        var x = NewHarness();
        var (url, _) = await x.View.UpAsync("alpha", "lan");
        Assert.NotNull(url);
        await Assert.ThrowsAsync<CeobeException>(() => x.View.UpAsync("alpha", "frp"));
    }

    [Fact(DisplayName = "provider 不可用时抛不可用原因，不落活动表")]
    public async Task 不可用provider被拦截()
    {
        var x = NewHarness();
        await x.M.UpAsync("alpha", TunnelKind.LAN);
        x.M.Down("alpha", TunnelKind.LAN);
        x.M.Register(new AlwaysUnavailable(TunnelKind.LAN));
        var e = await Assert.ThrowsAsync<CeobeException>(() => x.M.UpAsync("alpha", TunnelKind.LAN));
        Assert.Equal("缺东西", e.Message);
        Assert.False(x.M.IsUp("alpha", TunnelKind.LAN));
    }

    private sealed class AlwaysUnavailable(TunnelKind kind) : ITunnelProvider
    {
        public TunnelKind Kind => kind;
        public bool Available => false;
        public string UnavailableReason => "缺东西";
        public Task<TunnelSession> StartAsync(string i, string d, int p, Action<string> l,
            CancellationToken ct = default) => throw new NotSupportedException();
        public void Stop(TunnelSession s) { }
    }

    [Fact(DisplayName = "续期换新：按 tick 长度约 2 秒触发；新地址落盘、旧会话被停")]
    public async Task 续期触发换新会话()
    {
        var x = NewHarness(pinggyLength: TickSession);
        var old = await x.M.UpAsync("alpha", TunnelKind.PINGGY);
        var fresh = await WaitReplacedAsync(x.M, old, 10_000);

        Assert.NotSame(old, fresh);
        Assert.Contains(old, x.P[TunnelKind.PINGGY].Stopped);   // 旧进程被停
        Assert.Equal(2, x.P[TunnelKind.PINGGY].StartCount);     // 新会话已起
        Assert.Equal(2, x.H.Remembered.Count);                  // 初始 + 续期各落盘一次
        Assert.Equal("pinggy://fake:10002", x.H.Remembered[1].Url);
        x.M.Close();
    }

    [Fact(DisplayName = "续期失败：保留旧记录并记日志，30 秒后重试成功")]
    public async Task 续期失败保留旧记录并重试()
    {
        var x = NewHarness(pinggyLength: TickSession);
        var old = await x.M.UpAsync("alpha", TunnelKind.PINGGY);
        // 第 2 次 Start（即续期）失败一次，模拟瞬时网络抖动
        x.P[TunnelKind.PINGGY].FailLaterStarts = _ => Task.FromException(new CeobeException("fake 网络炸了"));

        // ~2s 后续期失败 → 日志应出现「续期失败…30 秒后重试」；旧记录保留
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!x.H.Logs.Any(l => l.Contains("续期失败") && l.Contains("30 秒后重试")) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Contains(x.H.Logs, l => l.Contains("续期失败"));
        Assert.True(x.M.IsUp("alpha", TunnelKind.PINGGY)); // 旧记录仍在（Java 同款行为）

        // 30 秒后重试成功（fake 恢复可用）→ 会话被替换
        var fresh = await WaitReplacedAsync(x.M, old, 40_000);
        Assert.NotSame(old, fresh);
        Assert.Equal(3, x.P[TunnelKind.PINGGY].StartCount); // 1 初始 + 1 失败 + 1 重试
        x.M.Close();
    }

    [Fact(DisplayName = "down 后续期定时器不得复活会话（对 Java 版孤儿定时器竞态的修正）")]
    public async Task down后续期不复活()
    {
        var x = NewHarness(pinggyLength: TickSession);
        await x.M.UpAsync("alpha", TunnelKind.PINGGY);
        x.M.Down("alpha", TunnelKind.PINGGY);
        await Task.Delay(3_000); // 定时器本应在 ~2s 触发
        Assert.Equal(1, x.P[TunnelKind.PINGGY].StartCount); // 没有因续期再起
        Assert.False(x.M.IsUp("alpha", TunnelKind.PINGGY));
        x.M.Close();
    }

    [Fact(DisplayName = "Close 停掉所有实例的全部通道")]
    public async Task close停全部()
    {
        var x = NewHarness();
        await x.M.UpAsync("alpha", TunnelKind.LAN);
        await x.M.UpAsync("beta", TunnelKind.LAN);
        x.M.Close();
        Assert.Equal(2, x.P[TunnelKind.LAN].Stopped.Count);
        Assert.Equal(2, x.P[TunnelKind.LAN].StartCount);
        Assert.Empty(x.View.ActiveOf("alpha"));
        Assert.Empty(x.View.ActiveOf("beta"));
    }

    [Fact(DisplayName = "IsExpiringSoon：不过期恒 false；过期会话在 5 分钟窗口内命中；满血不命中")]
    public void 到期判定语义()
    {
        var none = new TunnelSession(TunnelKind.LAN, "a", 25565, "1.2.3.4:25565", null);
        Assert.False(none.IsExpiringSoon(TimeSpan.FromMinutes(5))); // 不过期恒 false

        var expired = new TunnelSession(TunnelKind.PINGGY, "a", 25565, "tcp://x:1", DateTimeOffset.UtcNow);
        Assert.True(expired.IsExpiringSoon(TimeSpan.FromMinutes(5)));

        var fresh = new TunnelSession(TunnelKind.PINGGY, "a", 25565, "tcp://x:1",
            DateTimeOffset.UtcNow.Add(PinggyTunnel.Session));
        Assert.False(fresh.IsExpiringSoon(TunnelManager.RenewBefore)); // 满血 55 分钟不算临期
        Assert.True(fresh.IsExpiringSoon(PinggyTunnel.Session));       // 窗口大于剩余则临期
    }
}
