using Ceobe.Core.Providers;
using Xunit;

namespace Ceobe.Core.Tests.Providers;

/// <summary>移植自 Java <c>provider/NeoForgeProviderTest</c>：版本映射的归一化前缀（1.x 去前导 "1."，26.x 保留）。</summary>
public class NeoForgeProviderTest
{
    [Fact]
    public void 版本前缀归一化()
    {
        Assert.Equal("26.3", NeoForgeProvider.Prefix("26.3"));
        Assert.Equal("26.1.2", NeoForgeProvider.Prefix("26.1.2"));
        Assert.Equal("21.8", NeoForgeProvider.Prefix("1.21.8"));
        Assert.Equal("20.4", NeoForgeProvider.Prefix("1.20.4"));
        // 1.0 → "0"：主版本 <20 会被 SupportedPrefix 过滤，避免伪匹配
        Assert.Equal("0", NeoForgeProvider.Prefix("1.0"));
    }
}
