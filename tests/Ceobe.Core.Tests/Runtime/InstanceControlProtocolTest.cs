using Ceobe.Core.Runtime;
using Xunit;

namespace Ceobe.Core.Tests.Runtime;

/// <summary>控制通道应答解析（回归：应答前缀 "OK" 无尾随空格）。移植自 Java 版同名测试。</summary>
public class InstanceControlProtocolTest
{
    [Fact(DisplayName = "剥离无尾空格的前缀行")]
    public void 剥离无尾空格的前缀行() =>
        Assert.Equal(new[] { "RUNNING" }, InstanceControl.Parse(["OK", "RUNNING"]));

    [Fact(DisplayName = "剥离带正文的前缀行")]
    public void 剥离带正文的前缀行() =>
        Assert.Equal(new[] { "a", "b" }, InstanceControl.Parse(["OK extra", "a", "b"]));

    [Fact(DisplayName = "纯确认应答解析为空列表")]
    public void 纯确认应答解析为空列表() =>
        Assert.Empty(InstanceControl.Parse(["OK"]));

    [Fact(DisplayName = "ERR 转异常并保留消息")]
    public void ERR转异常并保留消息()
    {
        var ex = Assert.Throws<Util.CeobeException>(() => InstanceControl.Parse(["ERR 实例未运行"]));
        Assert.Equal("实例未运行", ex.Message);
    }

    [Fact(DisplayName = "空应答不抛异常")]
    public void 空应答不抛异常() => Assert.Empty(InstanceControl.Parse([]));

    [Fact(DisplayName = "隧道地址应答形如地址加有效期")]
    public void 隧道地址应答形如地址加有效期()
    {
        var parsed = InstanceControl.Parse(
            ["OK", "tcp://x.run.pinggy-free.link:35663", "2026-10-15T04:55:00Z"]);
        Assert.Equal("tcp://x.run.pinggy-free.link:35663", parsed[0]);
        Assert.Equal(2, parsed.Count);
    }

    [Fact(DisplayName = "无 .ctl 文件时状态查询返回 null")]
    public void 无Ctl文件时不可用()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-ctl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(InstanceControl.IsAvailable(dir));
            Assert.Null(InstanceControl.State(dir));
            // Call 必须给出「未由监督进程托管」的可操作提示，而不是裸异常
            var ex = Assert.Throws<Util.CeobeException>(() => InstanceControl.Call(dir, "STATE"));
            Assert.Contains("监督进程", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact(DisplayName = "畸形 .ctl 视为不可用而非崩溃")]
    public void 畸形Ctl不可用()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ceobe-ctl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".ctl"), "not-a-port-token-line");
            Assert.False(InstanceControl.IsAvailable(dir));
            File.WriteAllText(Path.Combine(dir, ".ctl"), "70000:tok");
            // 端口非法不得让解析过程抛出未捕获异常
            Assert.ThrowsAny<Exception>(() => InstanceControl.Call(dir, "STATE"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
