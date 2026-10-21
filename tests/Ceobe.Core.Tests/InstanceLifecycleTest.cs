using Ceobe.Core.Config;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.Core.Tests;

/// <summary>
/// 实例生命周期验收——**离线部分**（不联网、不下载真实服务端）。
///
/// 与 Java 版 <c>E2EVanillaLifecycleTest</c>（@Tag("network")，要下 ~60MB jar 并真启动）分工：
/// 那份搬到 <see cref="E2EVanillaLifecycleNetworkTest"/>；这里用假 jar 安装器验证
/// InstanceManager 的创建流程本身——端口排他、eula/properties 落盘、meta 序列化、
/// 重复创建拒绝、删除语义。这些是最容易在迁移中被静默改坏的部分，且无需网络即可全覆盖。
/// </summary>
[Collection("Jdk statics")] // 写进程级 CeobeHome.Override，必须串行
public class InstanceLifecycleTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-life-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;

    public InstanceLifecycleTest()
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

    private static InstanceManager NewManager() =>
        new(new TestFakes.StubJavaLocator(), TestFakes.OfflineJarInstaller(), null);

    private static InstanceSpec Spec(string name = "e2e", int port = 25565, int mem = 2048,
        ServerType type = ServerType.VANILLA) =>
        new(name, type, "26.3", null, mem, "", port, null);

    [Fact(DisplayName = "创建实例落齐 eula/properties/meta 三件套")]
    public async Task 创建落齐基础文件()
    {
        var manager = NewManager();
        var meta = await manager.CreateAsync(Spec());

        Assert.Equal(ServerType.VANILLA, meta.ServerType);
        var dir = manager.Dir("e2e");
        Assert.True(File.Exists(Path.Combine(dir, "server.jar")), "服务端 jar 应已安装");
        Assert.True(File.Exists(Path.Combine(dir, "eula.txt")), "eula.txt 应已生成");
        Assert.Equal("eula=true", File.ReadAllText(Path.Combine(dir, "eula.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(dir, "instance.json")));

        // 模板起底 + 端口同步：server-port 与 query.port 都要等于实例端口
        var props = ServerPropertiesFile.Load(Path.Combine(dir, "server.properties"));
        Assert.Equal("25565", props.Get("server-port"));
        Assert.Equal("25565", props.Get("query.port"));
    }

    [Fact(DisplayName = "重复创建同名实例被拒")]
    public async Task 重复创建被拒()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec());
        await Assert.ThrowsAsync<CeobeException>(() => manager.CreateAsync(Spec()));
    }

    [Fact(DisplayName = "创建时端口排他先于下载（冲突即失败，不落半成品）")]
    public async Task 创建端口排他()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec("alpha", 25590));
        // 第二个实例撞端口：必须在下载前失败
        var ex = await Assert.ThrowsAsync<CeobeException>(() => manager.CreateAsync(Spec("beta", 25590)));
        Assert.Contains("25590", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_home, "instances", "beta")));
    }

    [Fact(DisplayName = "非法规格被拒（名字/内存/端口）")]
    public async Task 非法规格被拒()
    {
        var manager = NewManager();
        // 内存低于下限
        await Assert.ThrowsAsync<CeobeSpec>(() =>
            manager.CreateAsync(new InstanceSpec("low", ServerType.VANILLA, "26.3", null, 256, "", 25565, null)));
        // 端口越界
        await Assert.ThrowsAsync<CeobeSpec>(() =>
            manager.CreateAsync(new InstanceSpec("bad", ServerType.VANILLA, "26.3", null, 2048, "", 70000, null)));
    }

    [Fact(DisplayName = "非 purge 删除只清元数据、保留世界文件")]
    public async Task 删除保留世界()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec());
        var dir = manager.Dir("e2e");
        // 模拟一个世界文件，删除（purge=false）后必须还在
        Directory.CreateDirectory(Path.Combine(dir, "world"));
        File.WriteAllText(Path.Combine(dir, "world", "level.dat"), "x");

        manager.Remove("e2e", purge: false);

        Assert.False(File.Exists(Path.Combine(dir, "instance.json")));
        Assert.True(File.Exists(Path.Combine(dir, "world", "level.dat")), "非 purge 删除必须保留世界");
        Assert.Null(manager.Find("e2e"));
    }

    [Fact(DisplayName = "purge 删除清空整个目录")]
    public async Task purge删除清空()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec());
        var dir = manager.Dir("e2e");
        Directory.CreateDirectory(Path.Combine(dir, "world"));
        File.WriteAllText(Path.Combine(dir, "world", "level.dat"), "x");

        manager.Remove("e2e", purge: true);

        Assert.False(Directory.Exists(dir), "purge 应删掉整个实例目录");
    }

    [Fact(DisplayName = "重命名同步改目录与 meta（要求已停止）")]
    public async Task 重命名()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec("old"));
        manager.Rename("old", "new");

        Assert.Null(manager.Find("old"));
        Assert.NotNull(manager.Find("new"));
        Assert.Equal("new", manager.Meta("new").Name);
        Assert.True(Directory.Exists(Path.Combine(_home, "instances", "new")));
    }

    [Fact(DisplayName = "重命名撞已有实例被拒")]
    public async Task 重命名撞名被拒()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec("a", 25601));
        await manager.CreateAsync(Spec("b", 25602));
        Assert.Throws<CeobeException>(() => manager.Rename("a", "b"));
    }

    [Fact(DisplayName = "改端口同步 server.properties 并做排他校验")]
    public async Task 改端口同步properties()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec("p1", 25610));
        manager.UpdateInstance("p1", memoryMb: null, port: 25611, jvmArgs: null, authlibInjectorUrl: null);

        Assert.Equal(25611, manager.Meta("p1").Port);
        var props = ServerPropertiesFile.Load(Path.Combine(manager.Dir("p1"), "server.properties"));
        Assert.Equal("25611", props.Get("server-port"));
        Assert.Equal("25611", props.Get("query.port"));
    }

    [Fact(DisplayName = "设置 authlib-injector 自动关闭正版验证")]
    public async Task authlib自动关正版()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec("auth"));
        manager.UpdateInstance("auth", null, null, null, "https://example.test/yggdrasil");

        Assert.Equal("https://example.test/yggdrasil", manager.Meta("auth").AuthlibInjectorUrl);
        var props = ServerPropertiesFile.Load(Path.Combine(manager.Dir("auth"), "server.properties"));
        Assert.Equal("false", props.Get("online-mode")); // ADR-015：三方鉴权需关正版验证
    }

    [Fact(DisplayName = "清空 authlib-injector URL 不强制改回 online-mode")]
    public async Task 清空authlib()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec("auth2"));
        manager.UpdateInstance("auth2", null, null, null, "https://example.test/x");
        manager.UpdateInstance("auth2", null, null, null, ""); // 传空串 = 清除

        Assert.Null(manager.Meta("auth2").AuthlibInjectorUrl);
    }

    [Fact(DisplayName = "未运行的实例启动前校验启动器存在")]
    public async Task 启动前校验启动器()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec());
        // 删掉 jar，启动应报「启动器不存在」而不是静默失败
        File.Delete(Path.Combine(manager.Dir("e2e"), "server.jar"));
        var ex = Assert.Throws<CeobeException>(() => manager.Start("e2e"));
        Assert.Contains("server.jar", ex.Message);
    }

    [Fact(DisplayName = "查询语义：Find 可空、Require 抛异常、新建后为 CREATED")]
    public async Task 查询语义()
    {
        var manager = NewManager();
        await manager.CreateAsync(Spec());

        Assert.NotNull(manager.Find("e2e"));
        Assert.NotNull(manager.Require("e2e"));
        Assert.Throws<CeobeException>(() => manager.Require("不存在"));
        // 新建实例没有 .state 文件 → CREATED（与 Java 版 readStateFile 缺文件时一致），
        // 不是 STOPPED：STOPPED 只由进程真实退出后的 transition 写入
        Assert.Equal(InstanceState.CREATED, manager.Require("e2e").State);
        Assert.Equal(-1, manager.Require("e2e").Pid);
        Assert.False(manager.Require("e2e").HostedHere);
    }

    [Fact(DisplayName = "provider 校正后的版本以返回值为准，而非请求值")]
    public async Task 版本以provider为准()
    {
        var manager = new InstanceManager(new TestFakes.StubJavaLocator(),
            TestFakes.VersionCorrectingInstaller("26.3.1"), null);
        await manager.CreateAsync(Spec("ver"));

        // 请求的是 26.3，provider 说实际装的是 26.3.1 → meta 必须记后者，
        // 否则后续按 26.3 去 Modrinth 筛 mod 会全部对不上
        Assert.Equal("26.3.1", manager.Meta("ver").GameVersion);
    }

    [Fact(DisplayName = "NeoForge 形态的参数文件被记入 meta（ADR-010）")]
    public async Task 参数文件形态()
    {
        var manager = new InstanceManager(new TestFakes.StubJavaLocator(),
            TestFakes.ArgsFileInstaller("libraries/neoforge/win_args.txt"), null);
        var meta = await manager.CreateAsync(Spec("nf", type: ServerType.NEOFORGE));

        Assert.Equal("libraries/neoforge/win_args.txt", meta.LaunchArgsFile);
        Assert.Equal(ServerType.NEOFORGE, meta.ServerType);
        // 丢了这条 ServerProcess 会走 -jar server.jar，NeoForge 直接起不来
        Assert.Equal(meta.LaunchArgsFile, meta.LaunchJar);
    }

    [Fact(DisplayName = "启动 NeoForge 形态实例时用参数文件而非 jar")]
    public async Task 启动走参数文件()
    {
        var manager = new InstanceManager(new TestFakes.StubJavaLocator(),
            TestFakes.ArgsFileInstaller("win_args.txt"), null);
        await manager.CreateAsync(Spec("nf2", type: ServerType.NEOFORGE));

        // 删掉参数文件 → 报错应点名它，而不是点名 server.jar
        File.Delete(Path.Combine(manager.Dir("nf2"), "win_args.txt"));
        var ex = Assert.Throws<CeobeException>(() => manager.Start("nf2"));
        Assert.Contains("win_args.txt", ex.Message);
        Assert.DoesNotContain("server.jar", ex.Message);
    }

    [Fact(DisplayName = "spec 里的 ServerType 具名参数可用（避免位置参数踩错）")]
    public async Task 具名参数()
    {
        var manager = NewManager();
        var meta = await manager.CreateAsync(new InstanceSpec("named", ServerType.FABRIC, "26.3",
            LoaderVersion: "0.16.9", MemoryMb: 4096, JvmArgs: "-XX:+UseG1GC", Port: 25630, JavaHome: null));

        Assert.Equal(ServerType.FABRIC, meta.ServerType);
        Assert.Equal("0.16.9", meta.LoaderVersion);
        Assert.Equal(4096, meta.MemoryMb);
        Assert.Equal("-XX:+UseG1GC", meta.JvmArgs);
    }
}
