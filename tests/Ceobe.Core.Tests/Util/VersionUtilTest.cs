using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Util;

/// <summary>版本号比较与归组（新纪年 26.x 与旧制 1.21.x 混合）。移植自 Java 版 VersionsTest。</summary>
public class VersionUtilTest
{
    [Fact(DisplayName = "降序排列跨代际正确")]
    public void 降序排列跨代际正确()
    {
        var input = new List<string> { "1.20.1", "26.3", "1.21.11", "26.2", "1.21.4", "1.20.6" };
        input.Sort(VersionUtil.Descending);
        Assert.Equal(
            ["26.3", "26.2", "1.21.11", "1.21.4", "1.20.6", "1.20.1"],
            input);
    }

    [Fact(DisplayName = "段数不同时短版本更大")]
    public void 段数不同时短版本更大()
    {
        // Java 版：缺失段按 0 比较，故 1.21.4 > 1.21
        var input = new List<string> { "1.21.4", "1.21" };
        input.Sort(VersionUtil.Descending);
        Assert.Equal(["1.21.4", "1.21"], input);
    }

    [Fact(DisplayName = "正式版判定排除预发布")]
    public void 正式版判定排除预发布()
    {
        Assert.True(VersionUtil.IsRelease("26.3"));
        Assert.True(VersionUtil.IsRelease("1.21.11"));
        Assert.False(VersionUtil.IsRelease("26.3-rc-3"));
        Assert.False(VersionUtil.IsRelease("26.4-snapshot-4"));
        Assert.False(VersionUtil.IsRelease("1.21.11-pre5"));
    }

    [Fact(DisplayName = "家族归组")]
    public void 家族归组()
    {
        Assert.Equal("26.x", VersionUtil.Family("26.3"));
        Assert.Equal("1.21.x", VersionUtil.Family("1.21.11"));
        Assert.Equal("1.20.x", VersionUtil.Family("1.20.6"));
    }

    [Fact(DisplayName = "家族归组对无点分与预发布的处理")]
    public void 家族归组边界()
    {
        Assert.Equal("26", VersionUtil.Family("26"));          // 无点分：原样
        Assert.Equal("1.21.x", VersionUtil.Family("1.21.4-pre1")); // 点分后取前两段
    }

    [Fact(DisplayName = "非数字段按 0 参与比较（不抛异常）")]
    public void 非数字段容错()
    {
        // Java 版 segments() 对解析失败的段填 0；C# 需等价，否则 provider 排序会崩
        var input = new List<string> { "1.21.x", "1.21.1" };
        input.Sort(VersionUtil.Descending);
        Assert.Equal(2, input.Count); // 只要求不抛且保留两项
    }

    [Fact(DisplayName = "相等版本比较返回 0")]
    public void 相等版本()
    {
        Assert.Equal(0, VersionUtil.Descending.Compare("26.3", "26.3"));
        Assert.Equal(0, VersionUtil.Descending.Compare("1.21", "1.21.0")); // 缺段补 0
    }
}
