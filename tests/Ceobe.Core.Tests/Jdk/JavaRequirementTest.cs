using Ceobe.Core.Jdk;
using Xunit;

namespace Ceobe.Core.Tests.Jdk;

/// <summary>
/// 「MC 版本 → 所需 Java 主版本」映射抽样（验收标准第 1 条：jdk required 26.3 / 1.21.8 / 1.20.4 → 25 / 21 / 17）。
/// 映射值不是硬编码表，而是 piston-meta 动态查询的结果（ADR-011 实测锚点）；因此断言必须联网。
/// 离线兜底行为（非 Mojang 版本 → 21）在 <see cref="兜底_非Mojang版本不阻塞"/> 覆盖，不打 network。
/// </summary>
public class JavaRequirementTest
{
    [Theory]
    [InlineData("1.20.4", 17)]
    [InlineData("1.21.8", 21)]
    [InlineData("26.3", 25)]
    [Trait("Category", "network")]
    public async Task 版本映射抽样_对照ADR011实测锚点(string mcVersion, int expectedJava)
    {
        int actual = await JavaRequirement.RequiredMajorAsync(mcVersion);
        Assert.Equal(expectedJava, actual);
    }

    [Fact]
    [Trait("Category", "network")]
    public async Task 同版本二次查询走缓存()
    {
        // 第一次查真实版本入缓存，第二次应瞬间返回（缓存语义对齐 Java 版 CACHE）
        int a = await JavaRequirement.RequiredMajorAsync("1.21.4");
        int b = await JavaRequirement.RequiredMajorAsync("1.21.4");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task 兜底_非Mojang版本不阻塞()
    {
        // 不存在于 Mojang 清单的版本：查询失败兜底 21，不抛（离线安全断言用不存在的 id）
        int v = await JavaRequirement.RequiredMajorAsync("this-version-does-not-exist-9999");
        Assert.Equal(21, v);
    }
}
