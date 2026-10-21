using Ceobe.Core.Jdk;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests.Runtime;

/// <summary>
/// 端口排他性：实例间不得共用端口（否则玩家的穿透地址会指向错误实例，ADR-009）。
/// 移植自 Java 版 PortExclusivityTest（@TempDir home + ceobe.home 系统属性 → CeobeHome.Override）。
/// 因写进程级全局 Override，归入 Jdk 的串行 collection，避免与其他测试串台。
/// </summary>
[Collection("Jdk statics")]
public class PortExclusivityTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-port-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;

    public PortExclusivityTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    private static void WriteInstance(string name, int port)
    {
        var dir = CeobeHome.InstanceDir(name);
        Directory.CreateDirectory(dir);
        var meta = new InstanceMeta
        {
            Name = name,
            ServerType = ServerType.PAPER,
            GameVersion = "26.3",
            Port = port,
        };
        File.WriteAllText(Path.Combine(dir, "instance.json"), JsonUtil.Write(meta));
    }

    private static InstanceManager NewManager() =>
        new(new TestFakes.StubJavaLocator(), installJar: null);

    [Fact(DisplayName = "端口冲突被拒绝而自身端口不算冲突")]
    public void 端口冲突被拒绝而自身端口不算冲突()
    {
        WriteInstance("alpha", 25565);
        var manager = NewManager();

        Assert.Throws<CeobeException>(() => manager.AssertPortExclusive("beta", 25565));
        // 自身端口不应算冲突
        manager.AssertPortExclusive("alpha", 25565);
        manager.AssertPortExclusive("beta", 25566);
    }

    [Fact(DisplayName = "自动选取避开已登记端口且可独占绑定")]
    public void 自动选取避开已登记端口且可独占绑定()
    {
        WriteInstance("gamma", 25570);
        var manager = NewManager();

        var free = manager.SuggestFreePort(25570);
        Assert.NotEqual(25570, free);           // 应避开已登记实例占用的端口
        Assert.True(PortProbe.IsFree(free));    // 选出的端口应可独占绑定
    }

    [Fact(DisplayName = "非法端口视为不可用")]
    public void 非法端口视为不可用()
    {
        Assert.False(PortProbe.IsFree(0));
        Assert.False(PortProbe.IsFree(70000));
        Assert.False(PortProbe.IsFree(-1));
    }

    [Fact(DisplayName = "实例列表按名排序且损坏元数据不炸整表")]
    public void 列表容错()
    {
        WriteInstance("beta", 25580);
        WriteInstance("alpha", 25581);
        // 一个损坏的实例目录：有 instance.json 但内容非法
        var bad = CeobeHome.InstanceDir("broken");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "instance.json"), "{ not json");

        var manager = NewManager();
        var list = manager.List();

        // 损坏实例被跳过，不影响其余两个；按名升序
        Assert.Equal(2, list.Count);
        Assert.Equal("alpha", list[0].Name);
        Assert.Equal("beta", list[1].Name);
    }

    [Fact(DisplayName = "实例名校验（小写字母/数字/_/-，长度 1-32）")]
    public void 实例名校验()
    {
        InstanceManager.ValidateName("alpha");
        InstanceManager.ValidateName("a_b-c1");
        Assert.Throws<CeobeException>(() => InstanceManager.ValidateName("Alpha"));   // 大写
        Assert.Throws<CeobeException>(() => InstanceManager.ValidateName("a b"));      // 空格
        Assert.Throws<CeobeException>(() => InstanceManager.ValidateName(""));         // 空
        Assert.Throws<CeobeException>(() => InstanceManager.ValidateName(new string('a', 33))); // 超长
    }
}
