using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// 整改项 1(a)：绑定一致性校验。三种 server-ip 输入 + 边界（缺键 / 缺文件 / 目标同为回环或同址）。
/// </summary>
public class TunnelBindGuardTest
{
    private static string TempDirWith(string? serverIpLine)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-bind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        if (serverIpLine is not null)
        {
            File.WriteAllText(Path.Combine(dir, "server.properties"),
                "#Minecraft server properties\n" + serverIpLine + "\nserver-port=25565\n",
                new System.Text.UTF8Encoding(false));
        }
        return dir;
    }

    private static void WithDir(string? line, Action<string> body)
    {
        var dir = TempDirWith(line);
        try
        {
            body(dir);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact(DisplayName = "server-ip 留空 → 放行（监听全部地址，与回环隧道兼容）")]
    public void 空值放行()
    {
        WithDir("server-ip=", dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
    }

    [Fact(DisplayName = "server-ip=0.0.0.0 → 放行")]
    public void 全零地址放行()
    {
        WithDir("server-ip=0.0.0.0", dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
    }

    [Fact(DisplayName = "server-ip=具体网卡地址 → 抛异常且消息含两个可执行选项")]
    public void 具体地址拦截()
    {
        WithDir("server-ip=192.168.1.50", dir =>
        {
            var e = Assert.Throws<CeobeException>(() =>
                TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
            // 三个必须出现的要素：不一致事实、实际值、隧道目标
            Assert.Contains("server-ip=192.168.1.50", e.Message);
            Assert.Contains("127.0.0.1", e.Message);
            Assert.Contains("不一致", e.Message);
            // 两个可执行选项：① 清空 server-ip ② 改隧道目标
            Assert.Contains("留空", e.Message);
            Assert.Contains("①", e.Message);
            Assert.Contains("②", e.Message);
        });
    }

    [Fact(DisplayName = "IPv6 具体地址同样拦截")]
    public void IPv6具体地址拦截()
    {
        WithDir("server-ip=fe80::1", dir =>
            Assert.Throws<CeobeException>(() =>
                TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道")));
    }

    [Fact(DisplayName = "server-ip 键不存在 → 放行（等价未配置）")]
    public void 缺键放行()
    {
        WithDir("motd=x", dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
    }

    [Fact(DisplayName = "server.properties 不存在 → 放行（不拦住开通道）")]
    public void 缺文件放行()
    {
        WithDir(null, dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
    }

    [Fact(DisplayName = "server-ip=127.0.0.1 且隧道目标也是回环 → 放行")]
    public void 回环对回环放行()
    {
        WithDir("server-ip=127.0.0.1", dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
    }

    [Fact(DisplayName = "server-ip=127.0.0.1 但隧道目标是 tailscale IP → 拦截")]
    public void 回环绑定打外部目标拦截()
    {
        WithDir("server-ip=127.0.0.1", dir =>
            Assert.Throws<CeobeException>(() =>
                TunnelBindGuard.AssertCompatible(dir, "100.101.102.103", 25565, "Tailscale 组网通道")));
    }

    [Fact(DisplayName = "server-ip 与隧道目标同为该具体地址 → 放行（Tailscale 场景）")]
    public void 目标与绑定一致放行()
    {
        WithDir("server-ip=100.101.102.103", dir =>
            TunnelBindGuard.AssertCompatible(dir, "100.101.102.103", 25565, "Tailscale 组网通道"));
    }

    [Fact(DisplayName = "localhost 视为回环字面量")]
    public void localhost回环()
    {
        WithDir("server-ip=localhost", dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
    }

    [Fact(DisplayName = "值带空格与大写不影响判定")]
    public void 空白与大写归一()
    {
        WithDir("server-ip= 0.0.0.0 ", dir =>
            TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道"));
        WithDir("server-ip=192.168.1.50", dir =>
            Assert.Throws<CeobeException>(() =>
                TunnelBindGuard.AssertCompatible(dir, "127.0.0.1", 25565, "Pinggy 玩家通道")));
    }
}
