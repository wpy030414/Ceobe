using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// Tailscale provider（整改项 1(c)）的可用性判定 + kind 契约往返。
/// 本机多数没有 tailscale，因此「缺失分支」是确定性断言；「已安装分支」按环境跳过。
/// </summary>
public class TailscaleTunnelTest
{
    private static bool Installed => TunnelProcess.Which("tailscale") is not null;

    [Fact(DisplayName = "tailscale 缺失分支：Available=false 且提示含可执行下一步")]
    public void 未安装时不可用并给指引()
    {
        var t = new TailscaleTunnel();
        if (!Installed)
        {
            // 本机确定缺 tailscale：走缺失分支（训练员点名的覆盖点）
            Assert.False(t.Available);
            Assert.Contains("tailscale", t.UnavailableReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                t.UnavailableReason.Contains("安装", StringComparison.Ordinal) ||
                t.UnavailableReason.Contains("up", StringComparison.Ordinal),
                "不可用原因必须包含可执行动作");
        }
        else
        {
            // 装有 tailscale 的机器（如未来 CI）：判定 = 已安装 && 已组网；断言与原始判定一致即可
            Assert.Equal(TailscaleTunnel.ReadTailscaleIp() is not null, t.Available);
        }
    }

    [Fact(DisplayName = "tailscale 不可用时 StartAsync 抛 CeobeException（不碰进程）")]
    public async Task 不可用时开启被拦截()
    {
        var t = new TailscaleTunnel();
        if (t.Available)
        {
            // 装有 tailscale 的机器会进真实查询路径——本机不走这里；留给 network 分类集成验证
            return;
        }
        var e = await Assert.ThrowsAsync<CeobeException>(() =>
            t.StartAsync("any", Path.GetTempPath(), 25565, _ => { }));
        Assert.Equal(t.UnavailableReason, e.Message);
    }

    [Fact(DisplayName = "已安装时 ip 查询不崩溃（超时/未登录均归 null 或异常被吞）")]
    public void 已安装时判定不崩溃()
    {
        if (!Installed) return; // 本机无 tailscale：缺失分支已在上一个用例覆盖
        _ = TailscaleTunnel.ReadTailscaleIp(); // 不抛即通过；未登录时应为 null
    }

    [Fact(DisplayName = "Stop 无进程会话安全幂等")]
    public void 停止空会话安全()
    {
        var t = new TailscaleTunnel();
        var s = new TunnelSession(TunnelKind.TAILSCALE, "x", 25565, "100.0.0.1:25565", null);
        t.Stop(s);
        t.Stop(s); // 两次不炸
    }

    [Theory]
    [InlineData(TunnelKind.PINGGY, "pinggy")]
    [InlineData(TunnelKind.CLOUDFLARE, "cloudflare")]
    [InlineData(TunnelKind.LAN, "lan")]
    [InlineData(TunnelKind.TAILSCALE, "tailscale")]
    public void KindId往返(TunnelKind k, string id)
    {
        Assert.Equal(id, k.Id());
        Assert.Equal(k, TunnelKindInfo.FromId(id));
        Assert.Equal(k, TunnelKindInfo.FromId(id.ToUpperInvariant())); // 忽略大小写
    }

    [Fact(DisplayName = "未知 id 抛业务异常")]
    public void 未知通道id()
    {
        Assert.Throws<CeobeException>(() => TunnelKindInfo.FromId("frp"));
    }
}
