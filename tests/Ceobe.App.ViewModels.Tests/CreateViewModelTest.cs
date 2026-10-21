using Ceobe.App.ViewModels;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「新建实例」单页表单的 ViewModel 验收。
/// 创建成功路径会联网下载服务端，这里只测不联网就能确定的行为：
/// 默认值、NumberBox 的 double 投影、类型切换、以及非法名在联网前被拦下。
/// </summary>
[Collection("CeobeHome serial")]
public class CreateViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-create-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;

    public CreateViewModelTest()
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

    private static InstanceManager Mgr() => new(new FakeLocator());

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    [Fact(DisplayName = "默认值能一把过：Paper / 2G / 自动端口 / 白名单关")]
    public void 默认值()
    {
        var vm = new CreateViewModel(Mgr());

        Assert.Equal(ServerType.PAPER, vm.SelectedType);
        Assert.Equal(ServerType.PAPER, vm.SelectedTypeOption.Type);
        Assert.Equal(2048, vm.MemoryMb);
        Assert.True(vm.AutoPort);
        Assert.True(vm.Port > 0);
        Assert.False(vm.WhitelistEnabled);
        Assert.False(vm.NeedsLoaderVersion);
    }

    [Fact(DisplayName = "NumberBox 的 double 投影与类型切换")]
    public void 投影与类型切换()
    {
        var vm = new CreateViewModel(Mgr());

        vm.MemoryMbDouble = 4096.4;
        Assert.Equal(4096, vm.MemoryMb);

        vm.PortDouble = 25599.6;
        Assert.Equal(25600, vm.Port);

        vm.SelectedTypeOption = vm.Types.First(t => t.Type == ServerType.FABRIC);
        Assert.Equal(ServerType.FABRIC, vm.SelectedType);
        Assert.True(vm.NeedsLoaderVersion);
    }

    [Fact(DisplayName = "非法实例名在联网前被拦下")]
    public async Task 名称校验()
    {
        var vm = new CreateViewModel(Mgr());
        vm.Name = "Bad Name!";
        vm.GameVersion = "26.3";   // 固定版本，避免走 ResolveLatestAsync 联网

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));
        Assert.Null(vm.CreatedInstance);
    }
}
