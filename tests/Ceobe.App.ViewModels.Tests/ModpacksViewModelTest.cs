using Ceobe.App.ViewModels;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「整合包」页签 ViewModel 验收（Java 版 ModpackPane）。
///
/// 搜索/下载/安装走真实 Modrinth，属 network 用例；这里守**不联网也会错**的部分：
/// - 类型能力判断（原版/Paper 无加载器，「装到本实例」必须拦下并给替代路径）；
/// - 选中态与按钮可用性派生；
/// - 选中整合包时把新实例名预填为其标识（对齐 Java）；
/// - 运行中禁止装到本实例（改文件不生效且可能撞锁）。
/// </summary>
[Collection("CeobeHome serial")]
public class ModpacksViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-mp-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public ModpacksViewModelTest()
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

    private void Seed(ServerType type, string version = "26.3", string? loader = null) =>
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = _instance, ServerType = type, GameVersion = version, Port = 25565, LoaderVersion = loader,
        }));

    private ModpacksViewModel NewVm() => new(new InstanceManager(new FakeLocator()), _instance);

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    // ========== 类型能力 ==========

    [Theory(DisplayName = "有加载器的类型可装整合包")]
    [InlineData(ServerType.FABRIC)]
    [InlineData(ServerType.NEOFORGE)]
    public void 支持整合包(ServerType type)
    {
        Seed(type);
        var vm = NewVm();
        Assert.True(vm.SupportsModpacks);
        Assert.False(vm.CanCreateInstall);   // 类型支持，但未选中整合包时仍不可安装
    }

    [Theory(DisplayName = "原版/Paper 无加载器：装到本实例被拦，但给替代路径")]
    [InlineData(ServerType.VANILLA)]
    [InlineData(ServerType.PAPER)]
    public void 不支持装到本实例(ServerType type)
    {
        Seed(type);
        var vm = NewVm();
        Assert.False(vm.SupportsModpacks);
        Assert.False(vm.CanInstallHere);
        // 必须给出「新建实例并安装」这条替代路径，而不是只说"不支持"
        Assert.Contains("新建实例并安装", vm.UnsupportedHint);
    }

    // ========== 选中态与按钮 ==========

    [Fact(DisplayName = "未选中时两个安装按钮都禁用")]
    public void 未选中禁用()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        Assert.Null(vm.Selected);
        Assert.False(vm.CanInstallHere);
        Assert.False(vm.CanCreateInstall);
    }

    [Fact(DisplayName = "选中后可装到本实例（Fabric 且未运行）")]
    public void 选中启用()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        vm.Selected = new ModpackRow("all-of-fabric", "All of Fabric", "author", 5000, "modpack", "proj");

        Assert.True(vm.CanInstallHere);
        Assert.True(vm.CanCreateInstall);
    }

    [Fact(DisplayName = "选中整合包时把新实例名预填为其标识")]
    public void 预填新实例名()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        Assert.Equal("", vm.NewName);
        vm.Selected = new ModpackRow("better-mc", "Better MC", "a", 1, "", "p");
        Assert.Equal("better-mc", vm.NewName);   // 对齐 Java：选中即预填，用户可改
    }

    [Fact(DisplayName = "已手改过新实例名则不被预填覆盖")]
    public void 预填不覆盖手输()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        vm.NewName = "我的服";
        vm.Selected = new ModpackRow("pack-x", "X", "a", 1, "", "p");
        Assert.Equal("我的服", vm.NewName);   // 用户已输入，尊重之
    }

    [Fact(DisplayName = "运行中禁止装到本实例并给提示")]
    public void 运行中禁装()
    {
        Seed(ServerType.FABRIC);
        File.WriteAllText(Path.Combine(_dir, ".state"), "RUNNING");
        File.WriteAllText(Path.Combine(_dir, ".pid"), Environment.ProcessId.ToString());

        var vm = NewVm();
        vm.Refresh();
        Assert.True(vm.ServerRunning);

        vm.Selected = new ModpackRow("p", "P", "a", 1, "", "p");
        Assert.False(vm.CanInstallHere);       // 装到本实例被禁
        Assert.True(vm.CanCreateInstall);      // 但新建实例不受影响（那是建另一个）
        Assert.Contains("运行中", vm.InstallHereBlockedHint);
    }

    [Fact(DisplayName = "忙碌时两按钮禁用（防重复点击）")]
    public void 忙碌禁用()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        vm.Selected = new ModpackRow("p", "P", "a", 1, "", "p");
        vm.IsBusy = true;
        Assert.False(vm.CanInstallHere);
        Assert.False(vm.CanCreateInstall);
    }

    // ========== 校验拦截（不联网也要拦的路径）==========

    [Fact(DisplayName = "未选中就点装到本实例：提示先选择")]
    public async Task 未选中装本实例()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        await vm.InstallHereCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Contains("选择一个整合包", vm.StatusText);
    }

    [Fact(DisplayName = "未选中就点新建安装：提示先选择")]
    public async Task 未选中新建()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        await vm.CreateAndInstallCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Contains("选择一个整合包", vm.StatusText);
    }

    [Fact(DisplayName = "原版实例点装到本实例：提示不支持（不发网络请求）")]
    public async Task 原版装本实例被拦()
    {
        Seed(ServerType.VANILLA);
        var vm = NewVm();
        vm.Selected = new ModpackRow("p", "P", "a", 1, "", "p");
        await vm.InstallHereCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("不支持安装整合包", vm.StatusText);
    }

    [Fact(DisplayName = "运行中点装到本实例：提示先停止（不发网络请求）")]
    public async Task 运行中装本实例被拦()
    {
        Seed(ServerType.FABRIC);
        File.WriteAllText(Path.Combine(_dir, ".state"), "RUNNING");
        File.WriteAllText(Path.Combine(_dir, ".pid"), Environment.ProcessId.ToString());

        var vm = NewVm();
        vm.Refresh();
        vm.Selected = new ModpackRow("p", "P", "a", 1, "", "p");
        await vm.InstallHereCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("运行中", vm.StatusText);
    }

    // ========== 默认值 ==========

    [Fact(DisplayName = "新建实例默认 4G 内存、仅适配默认开启")]
    public void 默认值()
    {
        Seed(ServerType.FABRIC);
        var vm = NewVm();
        Assert.Equal(4096, vm.NewMemoryMb);
        Assert.True(vm.OnlyCompatible);   // 默认只搜适配本实例的，避免装个跑不起来的
    }

    [Fact(DisplayName = "下载量人类可读")]
    public void 下载量格式()
    {
        Assert.Equal("1.5M", new ModpackRow("s", "t", "a", 1_500_000, "", "p").DownloadsLabel);
        Assert.Equal("2K", new ModpackRow("s", "t", "a", 2000, "", "p").DownloadsLabel);
        Assert.Equal("500", new ModpackRow("s", "t", "a", 500, "", "p").DownloadsLabel);
    }
}
