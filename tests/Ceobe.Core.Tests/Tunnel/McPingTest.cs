using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// McPing 协议编解码纯函数测试（整改项 1(b) 的可测核心；无网络参与）。
/// </summary>
public class McPingTest
{
    // —— VarInt：已知向量（与 wiki.vg / java 实现互验的字节表）——

    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(1, new byte[] { 0x01 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(255, new byte[] { 0xFF, 0x01 })]
    [InlineData(300, new byte[] { 0xAC, 0x02 })]
    [InlineData(2097151, new byte[] { 0xFF, 0xFF, 0x7F })]
    [InlineData(-1, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    [InlineData(-2147483648, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 })]
    public void VarInt已知向量编码(int value, byte[] expected)
    {
        Assert.Equal(expected, McPing.WriteVarInt(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(25565)]
    [InlineData(2097151)]
    [InlineData(268435455)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void VarInt编解码往返(int value)
    {
        var bytes = McPing.WriteVarInt(value);
        var decoded = McPing.ReadVarInt(bytes, 0, out var consumed);
        Assert.Equal(value, decoded);
        Assert.Equal(bytes.Length, consumed);
        Assert.InRange(bytes.Length, 1, 5);
    }

    [Fact(DisplayName = "VarInt：随机边界往返")]
    public void VarInt随机往返()
    {
        var rnd = new Random(20261021);
        for (var i = 0; i < 2000; i++)
        {
            var v = rnd.Next(int.MinValue, int.MaxValue);
            var bytes = McPing.WriteVarInt(v);
            Assert.Equal(v, McPing.ReadVarInt(bytes, 0, out _));
        }
    }

    [Fact(DisplayName = "VarInt：数据截断抛业务异常")]
    public void VarInt截断抛异常()
    {
        Assert.Throws<CeobeException>(() => McPing.ReadVarInt(new byte[] { 0x80, 0x80 }, 0, out _));
    }

    [Fact(DisplayName = "VarInt：超过 5 字节的畸形数据抛异常")]
    public void VarInt畸形长度抛异常()
    {
        Assert.Throws<CeobeException>(() =>
            McPing.ReadVarInt(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 }, 0, out _));
    }

    [Fact(DisplayName = "VarInt：offset 偏移读取正确")]
    public void VarInt偏移读取()
    {
        var buf = new byte[] { 0xAA, 0xAA, 0xAC, 0x02, 0xAA };
        Assert.Equal(300, McPing.ReadVarInt(buf, 2, out var consumed));
        Assert.Equal(2, consumed);
    }

    // —— 握手报文结构 ——

    [Fact(DisplayName = "BuildHandshake：结构字段逐项正确且尾部含 status request")]
    public void 握手报文结构()
    {
        var pkt = McPing.BuildHandshake("abc123.pinggy.io", 34567);
        // 首字节是报文长度 varint（数据体 22+2+... 字节，恒小于 128 → 1 字节）
        var off = 0;
        var pktLen = McPing.ReadVarInt(pkt, off, out var c); off += c;
        Assert.Equal(pkt.Length - off - 2, pktLen); // 减去尾部 status request 两字节
        Assert.Equal(0x00, McPing.ReadVarInt(pkt, off, out c)); off += c;   // packet id
        Assert.Equal(-1, McPing.ReadVarInt(pkt, off, out c)); off += c;     // protocol = -1
        var addrLen = McPing.ReadVarInt(pkt, off, out c); off += c;
        Assert.Equal("abc123.pinggy.io", System.Text.Encoding.UTF8.GetString(pkt, off, addrLen));
        off += addrLen;
        var port = (pkt[off] << 8) | pkt[off + 1]; // UInt16BE
        Assert.Equal(34567, port);
        off += 2;
        Assert.Equal(1, McPing.ReadVarInt(pkt, off, out c)); // nextState = status
        // 尾部：VarInt(1) VarInt(0)
        Assert.Equal(0x01, pkt[^2]);
        Assert.Equal(0x00, pkt[^1]);
    }

    [Fact(DisplayName = "BuildHandshake：端口越界抛 OverflowException（ProbeAsync 兜为无法验证）")]
    public void 端口越界()
    {
        Assert.Throws<OverflowException>(() => McPing.BuildHandshake("h", 70000));
    }

    // —— status 响应解析 ——

    [Fact(DisplayName = "ParseStatusResponse：完整帧解析出 JSON")]
    public void 解析状态响应帧()
    {
        const string json = "{\"version\":{\"name\":\"1.21.1\",\"protocol\":767}," +
                            "\"players\":{\"max\":20,\"online\":3},\"description\":\"ceobe\"}";
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        // body = VarInt(0x00) + VarInt(jsonLen) + json
        var body = new List<byte>();
        body.AddRange(McPing.WriteVarInt(0x00));
        body.AddRange(McPing.WriteVarInt(jsonBytes.Length));
        body.AddRange(jsonBytes);
        ms.Write(McPing.WriteVarInt(body.Count));
        ms.Write(body.ToArray(), 0, body.Count);
        Assert.Equal(json, McPing.ParseStatusResponse(ms.ToArray()));
    }

    [Fact(DisplayName = "ParseStatusResponse：JSON 被截断抛业务异常")]
    public void 解析截断帧抛异常()
    {
        var frame = new List<byte>();
        frame.AddRange(McPing.WriteVarInt(10));          // 声称报文 10 字节
        frame.AddRange(McPing.WriteVarInt(0x00));        // packet id
        frame.AddRange(McPing.WriteVarInt(100));         // json 长度 100 > 实际
        Assert.Throws<CeobeException>(() => McPing.ParseStatusResponse(frame.ToArray()));
    }

    [Fact(DisplayName = "WriteString：长度前缀 + UTF-8")]
    public void 字符串编码()
    {
        var b = McPing.WriteString("中文ab");
        var len = McPing.ReadVarInt(b, 0, out var c);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("中文ab"), len);
        Assert.Equal(len, b.Length - c);
    }
}
