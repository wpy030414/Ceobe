using Ceobe.Core.Mods;
using Xunit;

namespace Ceobe.Core.Tests.Mods;

/// <summary>
/// Modrinth 环境枚举判定（逐条移植自 Java 版 ModrinthClientTest）。
/// 回归点：曾误把 version.environment 当对象读取，导致客户端专属模组被装入服务端。
/// </summary>
public class ModrinthClientTest
{
    [Fact(DisplayName = "仅 client_only 视为客户端专属")]
    public void 仅client_only视为客户端专属()
    {
        Assert.True(ModrinthClient.ClientOnly("client_only"));
        Assert.False(ModrinthClient.ClientOnly("server_only"));
        Assert.False(ModrinthClient.ClientOnly("client_and_server"));
        Assert.False(ModrinthClient.ClientOnly("client_or_server_prefers_both"));
        Assert.False(ModrinthClient.ClientOnly("unknown"));
        Assert.False(ModrinthClient.ClientOnly("optional"));
    }
}
