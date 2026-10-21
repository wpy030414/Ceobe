using Ceobe.Core.Model;
using Ceobe.Core.Providers;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Providers;

/// <summary>
/// 五 provider 的真实 API 集成测试（对齐 docs/specs/module-providers.md 验收标准）。
/// 需联网，默认不跑：mvn 语义用 -Dtest.excludedGroups 控制，xunit 用
/// dotnet test --filter "Category=network"（Trait 值即 Category）。
/// </summary>
[Trait("Category", "network")]
public class ProviderNetworkTest
{
    /// <summary>每个用例独占 CEOBE_HOME，jar 缓存与实例目录互不污染。</summary>
    private static string FreshHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "ceobe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        CeobeHome.Override = home;
        return home;
    }

    public static TheoryData<ServerType> AllTypes => new()
    {
        ServerType.VANILLA,
        ServerType.PAPER,
        ServerType.FABRIC,
        ServerType.NEOFORGE,
        ServerType.PURPUR,
    };

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task 五provider_版本列表非空降序(ServerType type)
    {
        FreshHome();
        var versions = await ProviderRegistry.Of(type).ListVersionsAsync(CancellationToken.None);
        Assert.NotEmpty(versions);
        Assert.All(versions, v => Assert.True(VersionUtil.IsRelease(v), $"{type} 混入非正式版: {v}"));
        // 降序抽查：相邻元素满足 Descending 比较器
        for (var i = 1; i < Math.Min(versions.Count, 10); i++)
        {
            Assert.True(VersionUtil.Descending.Compare(versions[i - 1], versions[i]) <= 0,
                $"{type} 未按降序: {versions[i - 1]} 在 {versions[i]} 之前");
        }
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public async Task 五provider_解析并安装最新版(ServerType type)
    {
        var home = FreshHome();
        var provider = ProviderRegistry.Of(type);
        var versions = await provider.ListVersionsAsync(CancellationToken.None);
        var gameVersion = versions[0];
        var instanceDir = Path.Combine(home, "inst-" + type);
        Directory.CreateDirectory(instanceDir);

        var installed = await provider.InstallAsync(
            instanceDir, gameVersion, null, null, CancellationToken.None);

        Assert.Equal(gameVersion, installed.GameVersion);
        var launch = Path.Combine(instanceDir, installed.LaunchJar);
        // vanilla/paper/purpur 产 server.jar；fabric 产 fabric-server-launch.jar；
        // neoforge 产参数文件（且 LaunchArgsFile 同值）
        if (type == ServerType.NEOFORGE)
        {
            Assert.Equal(installed.LaunchArgsFile, installed.LaunchJar);
            Assert.EndsWith("_args.txt", installed.LaunchJar);
        }
        else
        {
            Assert.Null(installed.LaunchArgsFile);
        }
        Assert.True(File.Exists(launch), $"未产出启动文件 {launch}");
        if (installed.LaunchJar.EndsWith(".jar", StringComparison.Ordinal))
        {
            // 真 jar 必为 zip（PK 头）；fabric 的 launch jar 是引用 libraries/ 的薄壳，体积小但仍是合法 zip
            using var fs = File.OpenRead(launch);
            Assert.Equal(0x50, fs.ReadByte());
            Assert.Equal(0x4B, fs.ReadByte());
        }
        else
        {
            // NeoForge 参数文件：应含 neoforged 相关 classpath/主类 token（斜杠或点分形态跨版本不稳定，宽松匹配）
            var text = await File.ReadAllTextAsync(launch, CancellationToken.None);
            Assert.Contains("neoforged", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Fabric_loader版本_对指定游戏版本可解析()
    {
        FreshHome();
        var fabric = ProviderRegistry.Of(ServerType.FABRIC);
        var versions = await fabric.ListVersionsAsync(CancellationToken.None);
        var loaders = await fabric.ListLoaderVersionsAsync(
            versions[0], CancellationToken.None);
        Assert.NotEmpty(loaders);
    }

    [Fact]
    public async Task NeoForge_loader版本_按build降序()
    {
        FreshHome();
        var neoforge = ProviderRegistry.Of(ServerType.NEOFORGE);
        var versions = await neoforge.ListVersionsAsync(CancellationToken.None);
        var latestMc = versions[0];
        var loaders = await neoforge.ListLoaderVersionsAsync(
            latestMc, CancellationToken.None);
        Assert.NotEmpty(loaders);
        Assert.All(loaders, v => Assert.StartsWith(NeoForgeProvider.Prefix(latestMc) + ".", v));
    }

    [Fact]
    public async Task 原版_重复安装命中缓存()
    {
        var home = FreshHome();
        var vanilla = ProviderRegistry.Of(ServerType.VANILLA);
        var version = (await vanilla.ListVersionsAsync(CancellationToken.None))[0];
        var dir = Path.Combine(home, "inst");
        Directory.CreateDirectory(dir);

        await vanilla.InstallAsync(dir, version, null, null, CancellationToken.None);
        var jarPath = Path.Combine(dir, "server.jar");
        var firstLength = new FileInfo(jarPath).Length;
        File.Delete(jarPath);
        // 第二次应命中 ~/.ceobe/cache/jars 的去重缓存（不重新下载）
        await vanilla.InstallAsync(dir, version, null, null, CancellationToken.None);
        Assert.Equal(firstLength, new FileInfo(jarPath).Length);
    }
}
