using Ceobe.App.ViewModels;
using Ceobe.Core.Config;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「服务器配置」页 ViewModel 验收（Java 版 ServerPane）。
///
/// 守的是有产品后果的语义，每条错了都会让用户困惑：
/// - 运行中禁改（否则改了不生效，用户以为已生效）；
/// - 改名同步目录 + 元数据，且触发页签重建（页签标题就是实例名）；
/// - 改端口由 Core 同步写 server.properties（穿透地址依赖它，漏了就穿到错端口）；
/// - 设 authlib-injector 由 Core 自动关 online-mode（三方鉴权需要，ADR-015）。
/// </summary>
[Collection("CeobeHome serial")]
public class ServerViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-srv-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public ServerViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        _dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(_dir);
        WriteMeta();
        // server.properties：端口同步与 online-mode 联动都要落到这里
        File.WriteAllText(Path.Combine(_dir, "server.properties"),
            "server-port=25565\nquery.port=25565\nonline-mode=true\nmotd=hi\n");
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    // 默认值必须是编译期常量，不能用实例字段 _instance；两处都固定为 "srv"
    private void WriteMeta(int port = 25565, int mem = 2048, string name = "srv") =>
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = name, ServerType = ServerType.PAPER, GameVersion = "26.3", Port = port, MemoryMb = mem,
        }));

    private static InstanceManager Mgr() => new(new FakeLocator());
    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    private ServerViewModel NewVm()
    {
        var vm = new ServerViewModel(Mgr(), _instance);
        vm.Load();
        return vm;
    }

    private InstanceMeta ReadMeta(string dir) =>
        JsonUtil.Parse<InstanceMeta>(File.ReadAllText(Path.Combine(dir, "instance.json")));

    // ========== 加载 ==========

    [Fact(DisplayName = "加载当前设置与运行态")]
    public void 加载()
    {
        var vm = NewVm();
        Assert.Equal(_instance, vm.Name);
        Assert.Equal(_instance, vm.OriginalName);
        Assert.Equal(2048, vm.MemoryMb);
        Assert.Equal(25565, vm.Port);
        Assert.False(vm.ServerRunning);
        Assert.True(vm.CanSave);
    }

    [Fact(DisplayName = "运行中禁改：CanSave 为假且给提示")]
    public void 运行中禁改()
    {
        File.WriteAllText(Path.Combine(_dir, ".state"), "RUNNING");
        File.WriteAllText(Path.Combine(_dir, ".pid"), Environment.ProcessId.ToString());

        var vm = NewVm();
        Assert.True(vm.ServerRunning);
        Assert.False(vm.CanSave);
        Assert.Contains("运行中", vm.RunningHint);
        Assert.Contains("停止", vm.RunningHint);
    }

    [Fact(DisplayName = "运行中保存被拒，不落任何改动")]
    public async Task 运行中保存被拒()
    {
        File.WriteAllText(Path.Combine(_dir, ".state"), "RUNNING");
        File.WriteAllText(Path.Combine(_dir, ".pid"), Environment.ProcessId.ToString());

        var vm = NewVm();
        vm.MemoryMb = 4096;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("正在运行", vm.StatusText);
        Assert.Equal(2048, ReadMeta(_dir).MemoryMb);   // 没被改
    }

    // ========== 保存 ==========

    [Fact(DisplayName = "保存内存与端口，端口同步进 server.properties")]
    public async Task 保存并同步端口()
    {
        var vm = NewVm();
        vm.MemoryMb = 4096;
        vm.Port = 25580;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.HasError, vm.StatusText);
        var meta = ReadMeta(_dir);
        Assert.Equal(4096, meta.MemoryMb);
        Assert.Equal(25580, meta.Port);
        // 端口必须同步写 properties，否则穿透会打到旧端口（ADR-009 下游保证）
        var props = ServerPropertiesFile.Load(Path.Combine(_dir, "server.properties"));
        Assert.Equal("25580", props.Get("server-port"));
        Assert.Equal("25580", props.Get("query.port"));
        Assert.Contains("重启后生效", vm.StatusText);
    }

    [Fact(DisplayName = "保存 properties 时保留其余字节（注释/未改键）")]
    public async Task 保存保留其余字节()
    {
        File.AppendAllText(Path.Combine(_dir, "server.properties"), "# 我的注释\nlevel-type=minecraft\\:normal\n");
        var vm = NewVm();
        vm.Port = 25581;
        await vm.SaveCommand.ExecuteAsync(null);

        var text = File.ReadAllText(Path.Combine(_dir, "server.properties"));
        Assert.Contains("server-port=25581", text);
        Assert.Contains("# 我的注释", text);
        Assert.Contains(@"level-type=minecraft\:normal", text);   // 转义保留
        Assert.Contains("motd=hi", text);                          // 未改键原样
    }

    [Fact(DisplayName = "设 authlib-injector 自动关闭正版验证（ADR-015）")]
    public async Task authlib联动()
    {
        var vm = NewVm();
        vm.AuthlibInjectorUrl = "https://auth.example.test/yggdrasil";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("https://auth.example.test/yggdrasil", ReadMeta(_dir).AuthlibInjectorUrl);
        var props = ServerPropertiesFile.Load(Path.Combine(_dir, "server.properties"));
        Assert.Equal("false", props.Get("online-mode"));   // 三方鉴权需关正版验证
    }

    [Fact(DisplayName = "清空 authlib 传空串")]
    public async Task 清空authlib()
    {
        var vm = NewVm();
        vm.AuthlibInjectorUrl = "https://x.test/api";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.AuthlibInjectorUrl = "";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(ReadMeta(_dir).AuthlibInjectorUrl);
    }

    [Fact(DisplayName = "改名同步目录与元数据，并触发 Renamed 事件")]
    public async Task 改名()
    {
        var vm = NewVm();
        string? renamedTo = null;
        vm.Renamed += n => renamedTo = n;

        vm.Name = "renamed";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.HasError, vm.StatusText);
        Assert.Equal("renamed", renamedTo);
        var newDir = CeobeHome.InstanceDir("renamed");
        Assert.True(Directory.Exists(newDir));
        Assert.False(Directory.Exists(_dir));            // 旧目录已改名
        Assert.Equal("renamed", ReadMeta(newDir).Name);
    }

    [Fact(DisplayName = "改名撞已有实例报错")]
    public async Task 改名撞名()
    {
        // 注意：WriteMeta 只会写到 srv 的 _dir，所以这里直接往 other 目录写 instance.json，
        // 造出一个「同名目标已存在」的实例（改名撞名要撞的是别的实例，不是自己）。
        var other = CeobeHome.InstanceDir("other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = "other", ServerType = ServerType.PAPER, GameVersion = "26.3", Port = 25600,
        }));

        var vm = NewVm();
        vm.Name = "other";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("已存在", vm.StatusText);
        // srv 自己没被改动：改名失败不应留下半吊子状态
        Assert.True(Directory.Exists(_dir));
        Assert.Equal(_instance, ReadMeta(_dir).Name);
    }

    [Fact(DisplayName = "端口与别的实例冲突时报错（端口排他 ADR-009）")]
    public async Task 端口冲突()
    {
        var other = CeobeHome.InstanceDir("peer");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = "peer", ServerType = ServerType.PAPER, GameVersion = "26.3", Port = 25600,
        }));

        var vm = NewVm();
        vm.Port = 25600;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("25600", vm.StatusText);
        Assert.Contains("peer", vm.StatusText);          // 点名占用者
    }

    // ========== 放弃 / 脏检查 ==========

    [Fact(DisplayName = "放弃改动回到磁盘状态")]
    public void 放弃()
    {
        var vm = NewVm();
        vm.MemoryMb = 8192;
        vm.Port = 30000;
        Assert.True(vm.IsDirty);

        vm.DiscardCommand.Execute(null);

        Assert.Equal(2048, vm.MemoryMb);
        Assert.Equal(25565, vm.Port);
        Assert.False(vm.IsDirty);
    }

    [Fact(DisplayName = "改动任一字段即标脏")]
    public void 脏检查()
    {
        var vm = NewVm();
        Assert.False(vm.IsDirty);
        vm.MemoryMb = 4096;
        Assert.True(vm.IsDirty);
        vm.MemoryMb = 2048;      // 改回原值
        Assert.False(vm.IsDirty);
    }

    [Fact(DisplayName = "内存滑杆范围暴露给界面")]
    public void 内存范围()
    {
        var vm = NewVm();
        Assert.Equal(512, vm.MemoryMin);
        Assert.Equal(65536, vm.MemoryMax);
    }
}
