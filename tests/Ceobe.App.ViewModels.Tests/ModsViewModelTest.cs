using Ceobe.App.ViewModels;
using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「Mod」页签 ViewModel 验收（Java 版 ModPane）。
///
/// 网络路径（搜索/安装/卸载）不在离线测试范围——那需要真实 Modrinth 或 mock HTTP，
/// 归到 network 用例。这里守的是**不需要网络也会错**的部分：
/// - 服务端类型能力判断（原版/Paper 无 mods 目录，必须拦在 UI 而不是等用户点了才报错）；
/// - 已装列表与磁盘对照（标出被手动删掉的「缺失」记录，否则会显示一个已不存在的 mod）；
/// - env.server 的人话翻译（界面不该直接甩 required/optional 原文）；
/// - 按钮可用性由选中态派生。
/// </summary>
[Collection("CeobeHome serial")]
public class ModsViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-mods-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public ModsViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        _dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    private void Seed(ServerType type, string version = "26.3", IReadOnlyList<ModEntry>? mods = null) =>
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = _instance, ServerType = type, GameVersion = version, Port = 25565,
            Mods = mods?.ToList() ?? [],
        }));

    private static ModEntry Mod(string fileName, string title = "T", string env = "required",
        string version = "1.0", string projectId = "P") =>
        new() { FileName = fileName, Title = title, EnvServer = env, VersionNumber = version, ProjectId = projectId, Slug = fileName };

    private ModsViewModel NewVm()
    {
        var vm = new ModsViewModel(new InstanceManager(new FakeLocator()), _instance);
        vm.RefreshInstalled();
        return vm;
    }

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    // ========== 类型能力判断 ==========

    [Theory(DisplayName = "Fabric/NeoForge/Purpur 有 mods 目录")]
    [InlineData(ServerType.FABRIC)]
    [InlineData(ServerType.NEOFORGE)]
    [InlineData(ServerType.PURPUR)]
    public void 支持模组的类型(ServerType type)
    {
        Seed(type);
        var vm = NewVm();
        Assert.True(vm.SupportsMods);
    }

    [Theory(DisplayName = "原版/Paper 无 mods 目录，UI 直接说明")]
    [InlineData(ServerType.VANILLA)]
    [InlineData(ServerType.PAPER)]
    public void 不支持模组的类型(ServerType type)
    {
        Seed(type);
        var vm = NewVm();
        Assert.False(vm.SupportsMods);
        Assert.Contains("没有 mods 目录", vm.UnsupportedHint);
        Assert.Contains("Fabric", vm.UnsupportedHint);   // 给出可执行替代
    }

    [Fact(DisplayName = "不支持模组时搜索直接报错，不发网络请求")]
    public async Task 不支持时搜索拦截()
    {
        Seed(ServerType.VANILLA);
        var vm = NewVm();
        vm.Query = "sodium";
        await vm.SearchCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("没有 mods 目录", vm.StatusText);
        Assert.Empty(vm.Results);
    }

    [Fact(DisplayName = "搜索框提示含游戏版本与加载器")]
    public void 搜索提示()
    {
        Seed(ServerType.FABRIC, "26.3");
        var vm = NewVm();
        Assert.Contains("26.3", vm.SearchHint);
        Assert.Contains("fabric", vm.SearchHint);
    }

    // ========== 已安装列表 ==========

    [Fact(DisplayName = "已装列表读回并对照磁盘标「存在/缺失」")]
    public void 已装列表对照磁盘()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "mods"));
        File.WriteAllText(Path.Combine(_dir, "mods", "present.jar"), "x");   // 磁盘有
        // absent.jar 只在 meta 里，磁盘没有 → 应标「缺失」
        Seed(ServerType.FABRIC, mods: [Mod("present.jar", "在的"), Mod("absent.jar", "没了")]);

        var vm = NewVm();
        Assert.Equal(2, vm.Installed.Count);

        var present = vm.Installed.First(r => r.FileName == "present.jar");
        Assert.Equal("存在", present.Present);
        Assert.Equal("在的", present.Title);

        var absent = vm.Installed.First(r => r.FileName == "absent.jar");
        Assert.Equal("缺失", absent.Present);   // 被手动删掉的记录如实标出
    }

    [Fact(DisplayName = "env.server 翻译成人话，不甩原文")]
    public void env翻译()
    {
        Seed(ServerType.FABRIC, mods:
        [
            Mod("a.jar", env: "required"), Mod("b.jar", env: "optional"),
            Mod("c.jar", env: "unsupported"), Mod("d.jar", env: ""),
        ]);
        var vm = NewVm();

        Assert.Equal("必需", vm.Installed.First(r => r.FileName == "a.jar").EnvLabel);
        Assert.Equal("可选", vm.Installed.First(r => r.FileName == "b.jar").EnvLabel);
        Assert.Equal("不支持服务端", vm.Installed.First(r => r.FileName == "c.jar").EnvLabel);
        Assert.Equal("未知", vm.Installed.First(r => r.FileName == "d.jar").EnvLabel);
    }

    [Fact(DisplayName = "空 mods 列表显示空")]
    public void 空列表()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        Assert.Empty(vm.Installed);
    }

    [Fact(DisplayName = "重新读 meta：安装后 RefreshInstalled 能反映最新清单")]
    public void 刷新读最新meta()
    {
        Seed(ServerType.FABRIC, mods: []);
        var vm = NewVm();
        Assert.Empty(vm.Installed);

        // 模拟别处（CLI）往 instance.json 加了一个 mod
        var meta = JsonUtil.Parse<InstanceMeta>(File.ReadAllText(Path.Combine(_dir, "instance.json")));
        meta.Mods.Add(Mod("new.jar"));
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(meta));

        vm.RefreshInstalled();
        Assert.Single(vm.Installed);   // VM 不持有旧快照，能读到新记录
        Assert.Equal("new.jar", vm.Installed[0].FileName);
    }

    // ========== 按钮可用性 ==========

    [Fact(DisplayName = "未选中时安装/卸载按钮禁用")]
    public void 按钮默认禁用()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        Assert.False(vm.CanInstall);
        Assert.False(vm.CanUninstall);
    }

    [Fact(DisplayName = "选中结果后可安装，选中已装后可卸载")]
    public void 选中启用按钮()
    {
        Seed(ServerType.FABRIC, mods: [Mod("x.jar")]);
        var vm = NewVm();

        vm.SelectedResult = new ModSearchRow("slug", "T", "A", 100, "cat", "P");
        Assert.True(vm.CanInstall);

        vm.SelectedInstalled = vm.Installed[0];
        Assert.True(vm.CanUninstall);
    }

    [Fact(DisplayName = "忙碌时两按钮都禁用（防重复点击）")]
    public void 忙碌禁用()
    {
        Seed(ServerType.FABRIC, mods: [Mod("x.jar")]);
        var vm = NewVm();
        vm.SelectedResult = new ModSearchRow("slug", "T", "A", 100, "cat", "P");
        vm.SelectedInstalled = vm.Installed[0];

        vm.IsBusy = true;
        Assert.False(vm.CanInstall);
        Assert.False(vm.CanUninstall);
    }

    // ========== 下载量格式化 ==========

    [Theory(DisplayName = "下载量人类可读")]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5K")]
    [InlineData(2_500_000, "2.5M")]
    public void 下载量格式(long downloads, string expected)
    {
        var row = new ModSearchRow("s", "t", "a", downloads, "", "p");
        Assert.Equal(expected, row.DownloadsLabel);
    }
}
