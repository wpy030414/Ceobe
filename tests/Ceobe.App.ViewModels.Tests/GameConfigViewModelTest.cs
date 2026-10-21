using Ceobe.App.ViewModels;
using Ceobe.Core.Config;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「游戏配置」页 ViewModel 验收——整改项 5 的守卫测试。
///
/// 训练员的原话是「游戏配置界面稍有杂乱且繁多，可以再次进行分类规划」。整改分三部分，
/// 每部分都要有断言兜住，否则重构完仍是「一坨平铺」：
/// 1. 三级视图（常用 / 分类 / 原始）与搜索；
/// 2. 生效方式（Policy）作为一等公民，改完告诉用户要不要重启；
/// 3. 保存语义：重新加载文件、只写本面板改动的键——两个页签共用 server.properties，
///    不这样做就会互相覆盖（Java 版 PropsPaneBase.doSave 的核心设计）。
/// </summary>
[Collection("CeobeHome serial")]
public class GameConfigViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-cfg-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "cfg";

    public GameConfigViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        SeedInstance();
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    private void SeedInstance(string? properties = null)
    {
        var dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "instance.json"),
            JsonUtil.Write(new InstanceMeta
            {
                Name = _instance, ServerType = ServerType.PAPER, GameVersion = "26.3", Port = 25565,
            }));
        if (properties is not null)
        {
            File.WriteAllText(Path.Combine(dir, "server.properties"), properties);
        }
        else
        {
            File.WriteAllText(Path.Combine(dir, "server.properties"), ServerPropertiesFile.Template());
        }
    }

    private string PropsPath => Path.Combine(CeobeHome.InstanceDir(_instance), "server.properties");

    private GameConfigViewModel NewVm()
    {
        var vm = new GameConfigViewModel(new InstanceManager(new FakeLocator()), _instance);
        vm.Load();
        return vm;
    }

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    private static PropField Field(GameConfigViewModel vm, string key) =>
        vm.Fields.First(f => f.Key == key);

    // ========== 三级视图 ==========

    [Fact(DisplayName = "常用视图只含 ≤12 个开服即用字段")]
    public void 常用视图精简()
    {
        var vm = NewVm();
        Assert.Equal(ConfigView.Common, vm.View);
        // 整改项 5 的核心诉求：一屏能看完，不必在 60+ 键里翻
        Assert.True(vm.Fields.Count <= 12, $"常用视图应 ≤12 项，实际 {vm.Fields.Count}");
        Assert.Contains(vm.Fields, f => f.Key == "motd");
        Assert.Contains(vm.Fields, f => f.Key == "server-port");
        Assert.Contains(vm.Fields, f => f.Key == "white-list");
        // 高级键不该出现在常用视图
        Assert.DoesNotContain(vm.Fields, f => f.Key == "management-server-tls-keystore-password");
        Assert.DoesNotContain(vm.Fields, f => f.Key == "max-chained-neighbor-updates");
    }

    [Fact(DisplayName = "分类视图按选中分组过滤，且导航项带字段数")]
    public void 分类视图过滤()
    {
        var vm = NewVm();
        vm.View = ConfigView.Category;
        vm.SelectedGroup = PropSchema.GNet;

        Assert.NotEmpty(vm.Fields);
        Assert.All(vm.Fields, f => Assert.Equal(PropSchema.GNet, f.Group));
        Assert.Contains(vm.Fields, f => f.Key == "server-port");

        var nav = vm.GroupNavs.First(g => g.Name == PropSchema.GNet);
        Assert.True(nav.Count > 0);
        Assert.Equal(vm.Fields.Count, nav.Count);
    }

    [Fact(DisplayName = "原始视图含全部字段与未收录键")]
    public void 原始视图全量()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        var commonCount = vm.Fields.Count;

        vm.View = ConfigView.Common;
        Assert.True(commonCount > vm.Fields.Count, "原始视图应比常用视图字段多");
    }

    [Fact(DisplayName = "切换视图不丢未保存的改动")]
    public void 切视图保留改动()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        Field(vm, "motd").Value = "改过的名字";
        Assert.True(vm.CanSave);

        vm.View = ConfigView.Common;
        vm.View = ConfigView.Raw;

        Assert.Equal("改过的名字", Field(vm, "motd").Value);
        Assert.True(vm.CanSave);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact(DisplayName = "搜索按 key 名与中文标签模糊匹配，且优先于视图")]
    public void 搜索()
    {
        var vm = NewVm();
        vm.View = ConfigView.Common;

        vm.SearchQuery = "rcon";           // 英文 key
        Assert.Contains(vm.Fields, f => f.Key == "rcon.port");
        Assert.DoesNotContain(vm.Fields, f => f.Key == "motd");

        vm.SearchQuery = "视距";           // 中文标签
        Assert.Contains(vm.Fields, f => f.Key == "view-distance");

        vm.SearchQuery = "网络";           // 分组名
        Assert.Contains(vm.Fields, f => f.Key == "server-port");

        vm.SearchQuery = "";               // 清空回到当前视图
        Assert.True(vm.Fields.Count <= 12);
    }

    [Fact(DisplayName = "搜索无命中时给空列表而非崩溃")]
    public void 搜索无命中()
    {
        var vm = NewVm();
        vm.SearchQuery = "不存在的键名zzz";
        Assert.Empty(vm.Fields);
    }

    // ========== 生效方式（整改项 5 的一等公民）==========

    [Fact(DisplayName = "每个字段都带生效方式文案")]
    public void Policy文案()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        Assert.All(vm.Fields.Where(f => !f.IsUnknown), f => Assert.False(string.IsNullOrEmpty(f.PolicyLabel)));
    }

    [Fact(DisplayName = "server-port 标为需重启，white-list 标为可热重载")]
    public void Policy区分()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;

        var port = Field(vm, "server-port");
        Assert.Equal("需重启", port.PolicyLabel);
        Assert.True(port.RequiresRestart);

        var wl = Field(vm, "white-list");
        Assert.Equal("可热重载", wl.PolicyLabel);
        Assert.False(wl.RequiresRestart);
    }

    // ========== 控件形态 ==========

    [Fact(DisplayName = "字段类型正确投影（Bool/Int/Enum/String）")]
    public void 类型投影()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;

        Assert.Equal(PropType.Bool, Field(vm, "pvp").Type);
        Assert.Equal(PropType.Int, Field(vm, "max-players").Type);
        Assert.Equal(PropType.Enum, Field(vm, "gamemode").Type);
        Assert.Equal(PropType.String, Field(vm, "motd").Type);

        Assert.Equal(["survival", "creative", "adventure", "spectator"], Field(vm, "gamemode").Options);
        Assert.Equal(1, Field(vm, "max-players").Min);
        Assert.Equal(10000, Field(vm, "max-players").Max);
        Assert.Equal("范围 1 – 10000", Field(vm, "max-players").RangeHint);
    }

    [Fact(DisplayName = "Bool/Int 的便捷绑定形态与 Value 双向一致")]
    public void 便捷绑定形态()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;

        var pvp = Field(vm, "pvp");
        Assert.True(pvp.BoolValue);
        pvp.BoolValue = false;
        Assert.Equal("false", pvp.Value);

        var players = Field(vm, "max-players");
        Assert.Equal(20, players.NumberValue);
        players.NumberValue = 42;
        Assert.Equal("42", players.Value);
    }

    [Fact(DisplayName = "非法值置校验错误且不记入 pending（不得写坏 properties）")]
    public void 校验拦截()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;

        var vd = Field(vm, "view-distance");
        vd.Value = "99";                      // 上限 32
        Assert.NotNull(vd.ValidationError);
        Assert.Contains("视距", vd.ValidationError);
        Assert.False(vm.CanSave);
        Assert.Equal(0, vm.PendingCount);

        vd.Value = "12";                      // 合法
        Assert.Null(vd.ValidationError);
        Assert.True(vm.CanSave);
        Assert.Equal(1, vm.PendingCount);
    }

    [Fact(DisplayName = "改回原值后 pending 归零，且 PendingCount 通知")]
    public void 改回原值()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        var field = Field(vm, "motd");
        var original = field.Value;

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        field.Value = original + "-changed";
        Assert.Equal(1, vm.PendingCount);
        Assert.True(vm.CanSave);
        Assert.Contains(nameof(GameConfigViewModel.PendingCount), changed);

        changed.Clear();
        field.Value = original;
        Assert.Equal(0, vm.PendingCount);
        Assert.False(vm.CanSave);
        Assert.Contains(nameof(GameConfigViewModel.PendingCount), changed);
    }

    [Fact(DisplayName = "恢复默认值")]
    public void 恢复默认()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        var motd = Field(vm, "motd");
        motd.Value = "我的服务器";
        Assert.True(motd.IsModified);

        motd.ResetToDefault();
        Assert.Equal("A Minecraft Server", motd.Value);
        Assert.False(motd.IsModified);
    }

    // ========== 保存语义（最关键的守卫）==========

    [Fact(DisplayName = "保存只改本面板动过的键，其余字节保留（含注释与转义）")]
    public async Task 保存最小化()
    {
        // 带注释与 minecraft\:normal 转义的样本：硬契约 1 的现场
        SeedInstance("""
            #Minecraft server properties
            #Thu Oct 15 11:31:44 CST 2026
            motd=原描述
            level-type=minecraft\:normal
            max-players=20
            server-port=25565
            """);
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        Field(vm, "max-players").Value = "50";
        await vm.SaveCommand.ExecuteAsync(null);

        var text = File.ReadAllText(PropsPath);
        Assert.Contains("max-players=50", text);
        Assert.Contains("#Thu Oct 15 11:31:44 CST 2026", text);      // 注释保留
        Assert.Contains(@"level-type=minecraft\:normal", text);      // 转义保留
        Assert.Contains("motd=原描述", text);                        // 未动的键原样
        Assert.False(vm.CanSave);
        Assert.Contains("已保存 1 项", vm.StatusText);
    }

    [Fact(DisplayName = "保存时重新加载文件：不覆盖另一方在磁盘上的改动")]
    public async Task 保存不覆盖他方()
    {
        SeedInstance("""
            motd=初始
            max-players=20
            """);
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        Field(vm, "max-players").Value = "50";

        // 模拟「服务器配置」页签在 vm 加载之后改了 motd 并落盘
        var other = ServerPropertiesFile.Load(PropsPath);
        other.Set("motd", "另一方改的");
        other.Save();

        await vm.SaveCommand.ExecuteAsync(null);

        var props = ServerPropertiesFile.Load(PropsPath);
        Assert.Equal("50", props.Get("max-players"));       // 本面板的改动生效
        Assert.Equal("另一方改的", props.Get("motd"));       // 另一方的改动没被覆盖
    }

    [Fact(DisplayName = "非法值不会因保存被写入磁盘")]
    public async Task 非法值不落盘()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        Field(vm, "view-distance").Value = "99";   // 被校验拦下，未进 pending
        Field(vm, "motd").Value = "合法改动";
        await vm.SaveCommand.ExecuteAsync(null);

        var props = ServerPropertiesFile.Load(PropsPath);
        Assert.Equal("10", props.Get("view-distance"));     // 仍是模板默认值
        Assert.Equal("合法改动", props.Get("motd"));
    }

    [Fact(DisplayName = "丢弃改动后回到磁盘状态")]
    public void 丢弃改动()
    {
        var vm = NewVm();
        vm.View = ConfigView.Raw;
        Field(vm, "motd").Value = "未保存的改动";
        Assert.True(vm.CanSave);

        vm.DiscardCommand.Execute(null);

        Assert.False(vm.CanSave);
        Assert.Equal(0, vm.PendingCount);
        Assert.Equal("A Minecraft Server", Field(vm, "motd").Value);
    }

    [Fact(DisplayName = "无 server.properties 时给引导文案而非空白页")]
    public void 无配置文件()
    {
        var dir = CeobeHome.InstanceDir(_instance);
        File.Delete(Path.Combine(dir, "server.properties"));

        var vm = new GameConfigViewModel(new InstanceManager(new FakeLocator()), _instance);
        vm.Load();

        Assert.False(vm.PropsExist);
        Assert.Empty(vm.Fields);
        Assert.Contains("尚无 server.properties", vm.StatusText);
    }

    [Fact(DisplayName = "未收录键可见但只读")]
    public void 未收录键()
    {
        SeedInstance("""
            motd=x
            some-plugin-custom-key=abc
            """);
        var vm = NewVm();
        vm.View = ConfigView.Raw;

        var unknown = Field(vm, "some-plugin-custom-key");
        Assert.True(unknown.IsUnknown);
        Assert.False(unknown.IsEditable);
        Assert.Equal("abc", unknown.Value);
        Assert.Equal(1, vm.UnknownCount);

        // 只读字段的写入应被静默忽略，不得进 pending
        unknown.Value = "hacked";
        Assert.Equal("abc", unknown.Value);
        Assert.Equal(0, vm.PendingCount);
    }

    // ========== 整改项 5 的复合控件 ==========

    [Fact(DisplayName = "资源包 4 键聚成复合控件（不再散落）")]
    public void 资源包复合()
    {
        var vm = NewVm();
        Assert.Equal(4, vm.ResourcePackFields.Count);
        Assert.Contains(vm.ResourcePackFields, f => f.Key == "resource-pack");
        Assert.Contains(vm.ResourcePackFields, f => f.Key == "resource-pack-sha1");
    }

    [Fact(DisplayName = "数据包逗号串解析成可增删列表并写回")]
    public void 数据包多选()
    {
        SeedInstance("""
            motd=x
            initial-enabled-packs=vanilla,fabric
            initial-disabled-packs=
            """);
        var vm = NewVm();
        Assert.Equal(["vanilla", "fabric"], vm.EnabledPacks);
        Assert.Empty(vm.DisabledPacks);

        vm.TogglePackCommand.Execute("vanilla");
        Assert.Equal(["fabric"], vm.EnabledPacks);
        Assert.Equal(["vanilla"], vm.DisabledPacks);

        vm.View = ConfigView.Raw;
        Assert.Equal("fabric", Field(vm, "initial-enabled-packs").Value);
        Assert.Equal("vanilla", Field(vm, "initial-disabled-packs").Value);
    }

    [Fact(DisplayName = "运行中保存提示需重启")]
    public async Task 运行中提示()
    {
        SeedInstance();
        // 伪造运行态。只写 .state 是不够的：Core 的 statusOf 发现「state 是 live 但没有存活 pid」
        // 会按残留状态修正为 STOPPED（那是正确行为，进程死了不该显示运行中）。
        // 故用当前测试进程的 pid——它确实存活，能通过 PidAlive 校验。
        var dir = CeobeHome.InstanceDir(_instance);
        File.WriteAllText(Path.Combine(dir, ".state"), "RUNNING");
        File.WriteAllText(Path.Combine(dir, ".pid"), Environment.ProcessId.ToString());

        var vm = NewVm();
        Assert.True(vm.ServerRunning);
        vm.View = ConfigView.Raw;
        Field(vm, "motd").Value = "x";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("重启后生效", vm.StatusText);
    }

    [Fact(DisplayName = "残留状态（有 .state 无存活 pid）不得显示为运行中")]
    public void 残留状态修正()
    {
        SeedInstance();
        var dir = CeobeHome.InstanceDir(_instance);
        File.WriteAllText(Path.Combine(dir, ".state"), "RUNNING");
        // 故意不写 .pid：Core 应把它修正为 STOPPED，
        // 否则界面会显示一个早就死掉的服在「运行中」，用户以为还能连
        var vm = NewVm();
        Assert.False(vm.ServerRunning);
    }
}
