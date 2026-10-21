using Ceobe.Core.Runtime;
using Xunit;

namespace Ceobe.Core.Tests.Runtime;

/// <summary>
/// <see cref="TunnelReport"/> 的控制通道线格式编解码。
///
/// 为什么必须有这份测试：协议是**竖线分隔 + 行分隔**的，而 Diagnostic 是探测过程攒出来的
/// 长文本，天然可能含 <c>|</c>、<c>\n</c>、<c>\r</c>。任一未转义都会撕碎协议帧——
/// 表现为「tunnel status 少一行」「诊断文本被截断成乱码」，且不会报错，很难查。
/// 这是整改项 1(b) 的诊断能安全传出监督进程的前提。
/// </summary>
public class TunnelReportTest
{
    private static readonly DateTimeOffset Expiry =
        new(2026, 10, 21, 8, 30, 0, TimeSpan.Zero);

    [Fact(DisplayName = "编解码往返无损（含中文与标点）")]
    public void 往返无损()
    {
        var original = new TunnelReport(
            "tcp://x.run.pinggy-free.link:35663", Expiry, true,
            "已验证：从公网侧取得 MC status 响应（协议 770，玩家 0/20）");
        var back = TunnelReport.FromLine(original.ToLine());

        Assert.Equal(original.Url, back.Url);
        Assert.Equal(original.ExpiresAt, back.ExpiresAt);
        Assert.Equal(original.Verified, back.Verified);
        Assert.Equal(original.Diagnostic, back.Diagnostic);
    }

    [Theory(DisplayName = "诊断文本含协议元字符时不被撕碎")]
    [InlineData("含竖线 | 分隔符的诊断")]
    [InlineData("含换行\n第二行\n第三行")]
    [InlineData("含回车\r\n混合换行")]
    [InlineData("含反斜杠 \\ 与 \\n 字面量")]
    [InlineData("|开头就是分隔符")]
    [InlineData("结尾是分隔符|")]
    [InlineData("竖线|多|个|连|续")]
    public void 元字符不撕碎协议(string diagnostic)
    {
        var line = new TunnelReport("tcp://h:1", Expiry, false, diagnostic).ToLine();

        // 关键不变量：一行报告恰好 4 个未转义的竖线分段，且不含裸换行
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Equal(4, CountUnescaped(line));

        var back = TunnelReport.FromLine(line);
        Assert.Equal(diagnostic, back.Diagnostic);
        Assert.Equal(false, back.Verified);
    }

    [Fact(DisplayName = "三态 Verified 分别编解码为 1/0/-")]
    public void 三态编码()
    {
        Assert.Equal("1", Segment(new TunnelReport("u", null, true, null).ToLine(), 2));
        Assert.Equal("0", Segment(new TunnelReport("u", null, false, null).ToLine(), 2));
        Assert.Equal("-", Segment(new TunnelReport("u", null, null, null).ToLine(), 2));

        Assert.Equal(true, TunnelReport.FromLine("u|-|1|-").Verified);
        Assert.Equal(false, TunnelReport.FromLine("u|-|0|-").Verified);
        Assert.Null(TunnelReport.FromLine("u|-|-|-").Verified);
    }

    [Fact(DisplayName = "null 与空串字段写出为占位符 -")]
    public void 空值占位()
    {
        var line = new TunnelReport(null, null, null, null).ToLine();
        Assert.Equal("-|-|-|-", line);

        var back = TunnelReport.FromLine(line);
        Assert.Null(back.Url);
        Assert.Null(back.ExpiresAt);
        Assert.Null(back.Verified);
        Assert.Null(back.Diagnostic);
    }

    [Fact(DisplayName = "空字符串诊断读回为 null（与「未探测」不混淆）")]
    public void 空诊断归一()
    {
        var back = TunnelReport.FromLine(new TunnelReport("u", null, true, "").ToLine());
        Assert.Null(back.Diagnostic);
    }

    [Fact(DisplayName = "过期时间往返保留 ISO-8601 精度")]
    public void 过期时间精度()
    {
        var back = TunnelReport.FromLine(new TunnelReport("u", Expiry, null, null).ToLine());
        Assert.Equal(Expiry, back.ExpiresAt);
        Assert.Equal(2026, back.ExpiresAt!.Value.Year);
        Assert.Equal(30, back.ExpiresAt!.Value.Minute);
    }

    [Fact(DisplayName = "不过期通道（LAN/Tailscale）读回 null 而非纪元时间")]
    public void 不过期通道()
    {
        var back = TunnelReport.FromLine(new TunnelReport("192.168.1.5:25565", null, true, "ok").ToLine());
        Assert.Null(back.ExpiresAt);
    }

    [Fact(DisplayName = "容忍旧版 3 段与 2 段格式（协议演进不回炸）")]
    public void 容忍旧格式()
    {
        // Java 版协议只有 kind|url|expires 三段，没有 verified/diagnostic
        var three = TunnelReport.FromLine("tcp://h:1|2026-10-21T08:30:00.0000000+00:00|-");
        Assert.Equal("tcp://h:1", three.Url);
        Assert.Null(three.Verified);
        Assert.Null(three.Diagnostic);

        var two = TunnelReport.FromLine("tcp://h:1|-");
        Assert.Equal("tcp://h:1", two.Url);
        Assert.Null(two.ExpiresAt);

        var one = TunnelReport.FromLine("tcp://h:1");
        Assert.Equal("tcp://h:1", one.Url);
    }

    [Fact(DisplayName = "畸形 expiresAt 读回 null 而不抛")]
    public void 畸形时间容错()
    {
        var back = TunnelReport.FromLine("u|不是时间|-|-");
        Assert.Null(back.ExpiresAt);
    }

    [Fact(DisplayName = "地址含冒号与点号不被误解析")]
    public void 地址字符安全()
    {
        const string url = "tcp://x.run.pinggy-free.link:35663";
        var back = TunnelReport.FromLine(new TunnelReport(url, null, true, null).ToLine());
        Assert.Equal(url, back.Url);
    }

    [Fact(DisplayName = "LineFormatHint 与实际写出段数一致（防止注释腐化）")]
    public void 格式提示与实现一致()
    {
        // 这条守卫的是「文档说的格式」与「代码写的格式」不脱节
        Assert.Equal(4, TunnelReport.LineFormatHint.Split('|').Length);
        Assert.Equal("url|expiresAt|verified|diagnostic", TunnelReport.LineFormatHint);
    }

    /// <summary>数出未被反斜杠转义的竖线数量（协议分段 = 数量 + 1）。</summary>
    private static int CountUnescaped(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '|') n++;
        }
        return n + 1;
    }

    /// <summary>按未转义竖线切分并取第 i 段（模拟解析器的分段视角）。</summary>
    private static string Segment(string s, int index)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                current.Append(s[i]).Append(s[i + 1]);
                i++;
                continue;
            }
            if (s[i] == '|')
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(s[i]);
        }
        segments.Add(current.ToString());
        return segments[index];
    }
}
