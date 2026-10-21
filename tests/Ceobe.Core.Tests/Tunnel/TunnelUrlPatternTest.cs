using Ceobe.Core.Tunnel;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// 公网地址正则（硬约束 1 / 3）与地址拆分。纯字符串测试，无进程无网络。
/// </summary>
public class TunnelUrlPatternTest
{
    [Theory]
    [InlineData("tcp://abc123.pinggy.io:34567", "abc123.pinggy.io", 34567)]
    [InlineData("Forwarding tcp://eu-central-1.pinggy.io:12345 to 127.0.0.1:25565",
        "eu-central-1.pinggy.io", 12345)]
    [InlineData("  tcp://a-b.example.com:80  ", "a-b.example.com", 80)]
    [InlineData("tcp://1.2.3.4.example:65535", "1.2.3.4.example", 65535)]
    public void Pinggy地址从噪声行中提取(string line, string host, int port)
    {
        var m = PinggyTunnel.TcpUrl.Match(line);
        Assert.True(m.Success);
        Assert.Equal(host, m.Groups[1].Value);
        Assert.Equal(port, int.Parse(m.Groups[2].Value));
        Assert.Equal($"tcp://{host}:{port}", m.Value);
        Assert.Equal((host, port), PinggyTunnel.SplitTcp(m.Value));
    }

    [Theory]
    // 大写主机不匹配：Java 的 Pattern 未开 CASE_INSENSITIVE，行为必须一致
    [InlineData("TCP://ABC.PINGGY.IO:1234")]
    [InlineData("tcp://host:25565/extra")] // 端口后仍匹配到 host:25565，见下方专用断言
    [InlineData("ssh: connect to host a.pinggy.io port 443: Connection reset")]
    [InlineData("Warning: Permanently added 'a.pinggy.io' to the list of known hosts.")]
    [InlineData("")]
    public void Pinggy正则对无地址行不误判(string line)
    {
        var m = PinggyTunnel.TcpUrl.Match(line);
        if (line == "tcp://host:25565/extra")
        {
            // 端口数字是贪婪匹配，trailing slash 不参与；只取到 :25565
            Assert.True(m.Success);
            Assert.Equal("tcp://host:25565", m.Value);
            return;
        }
        Assert.False(m.Success);
    }

    [Fact(DisplayName = "SplitTcp：非法地址抛业务异常")]
    public void 拆分非法地址()
    {
        Assert.Throws<Ceobe.Core.Util.CeobeException>(() => PinggyTunnel.SplitTcp("http://nope"));
    }

    [Theory]
    [InlineData("2026-10-21T00:00:00Z INFO + cloudflared trycloudflare https://my-server-1a2b3c.trycloudflare.com")]
    [InlineData("https://abc-123.trycloudflare.com")]
    public void Cloudflare地址提取(string line)
    {
        var m = CloudflareQuickTunnel.UrlPattern.Match(line);
        Assert.True(m.Success);
        Assert.Matches(@"^https://[a-z0-9-]+\.trycloudflare\.com$", m.Value);
    }

    [Theory]
    [InlineData("https://trycloudflare.com")]              // 缺 slug
    [InlineData("https://My Server.trycloudflare.com")]     // 大写 + 空格
    [InlineData("http://abc.trycloudflare.com")]            // 仅 https
    [InlineData("ftp://a1-b2.trycloudflare.com")]           // 非 http 系
    public void Cloudflare正则不误判(string line)
    {
        Assert.DoesNotMatch(CloudflareQuickTunnel.UrlPattern, line);
    }
}