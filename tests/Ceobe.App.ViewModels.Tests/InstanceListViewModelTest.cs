using Ceobe.App.ViewModels;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 实例列表 ViewModel 的行为验收（闸门 C 的主战场）。
///
/// 这些断言之所以重要：WinUI 3 没有 Java 版 <c>-Dceobe.snapshot</c> 那种「启动即导出 Scene 快照」
/// 的廉价回归手段，所以界面逻辑必须下沉到 ViewModel 并在这里验证。
/// 侧栏最容易坏的是「刷新后丢选中态」「状态变化不反映」「空列表时显示错文案」——
/// 这三类 bug 在像素截图里根本看不出来，只有断言能抓住。
/// </summary>
[Collection("CeobeHome serial")] // 写进程级 CeobeHome.Override，必须串行
public class InstanceListViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;

    public InstanceListViewModelTest()
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

    /// <summary>直接落一份 instance.json（不走 provider，避免联网）。</summary>
    private static void Seed(string name, int port, InstanceState state,
        ServerType type = ServerType.PAPER, string version = "26.3")
    {
        var dir = CeobeHome.InstanceDir(name);
        Directory.CreateDirectory(dir);
        var meta = new InstanceMeta
        {
            Name = name,
            ServerType = type,
            GameVersion = version,
            Port = port,
        };
        File.WriteAllText(Path.Combine(dir, "instance.json"), JsonUtil.Write(meta));
        // .state 文件决定非本进程托管实例的显示状态；live 态还必须有存活的 .pid，
        // 否则 Core 会把它当残留状态修正为 STOPPED。
        File.WriteAllText(Path.Combine(dir, ".state"), state.ToString());
        if (state.IsLive())
        {
            File.WriteAllText(Path.Combine(dir, ".pid"), Environment.ProcessId.ToString());
        }
    }

    private static InstanceManager NewManager() => new(new FakeJavaLocator());

    private sealed class FakeJavaLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    [Fact(DisplayName = "空列表给出引导文案而非空白")]
    public void 空列表()
    {
        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            Assert.Empty(vm.Rows);
            Assert.Equal("暂无实例", vm.StatusText);
            Assert.False(vm.HasError);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "刷新后按名称排序，且 Detail 拼出类型/版本/端口")]
    public void 刷新与投影()
    {
        Seed("zeta", 25567, InstanceState.STOPPED);
        Seed("alpha", 25565, InstanceState.STOPPED, ServerType.FABRIC, "26.3");
        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            Assert.Equal(2, vm.Rows.Count);
            // Core 的 List() 已按名排序，ViewModel 不得再打乱
            Assert.Equal("alpha", vm.Rows[0].Name);
            Assert.Equal("zeta", vm.Rows[1].Name);
            Assert.Equal("fabric · 26.3 · 25565", vm.Rows[0].Detail);
            Assert.Equal("paper · 26.3 · 25567", vm.Rows[1].Detail);
            Assert.Equal("已停止", vm.Rows[0].StateLabel);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "刷新保留选中实例与行对象（NavigationView 不丢工作区）")]
    public void 刷新保留选中()
    {
        Seed("alpha", 25565, InstanceState.STOPPED);
        Seed("beta", 25566, InstanceState.STOPPED);
        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            var row = vm.Rows[0];
            vm.SelectedName = "alpha";

            // 刷新（例如打开设置后重扫）不得清空选中、不得换掉行对象
            vm.Refresh();

            Assert.Same(row, vm.Rows[0]);
            Assert.Same(row, vm.SelectedRow);
            Assert.Equal("alpha", vm.SelectedName);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "只看运行中：实例停止时移出列表并清空选中")]
    public void 只看运行中状态变化()
    {
        Seed("a", 25565, InstanceState.RUNNING);
        Seed("b", 25566, InstanceState.RUNNING);
        var manager = NewManager();
        var vm = new InstanceListViewModel(manager);
        try
        {
            vm.OnlyRunning = true;
            vm.Refresh();
            Assert.Equal(2, vm.Rows.Count);
            vm.SelectedName = "a";

            RaiseStateChanged(manager, "a", InstanceState.STOPPED);

            Assert.Equal("b", Assert.Single(vm.Rows).Name);
            Assert.Null(vm.SelectedName);
            Assert.Equal("1 / 1 个实例运行中", vm.StatusText);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "状态事件就地更新状态栏与 DetailLine")]
    public void 事件更新状态栏()
    {
        Seed("live", 25565, InstanceState.STOPPED);
        var manager = NewManager();
        var vm = new InstanceListViewModel(manager);
        try
        {
            vm.Refresh();
            RaiseStateChanged(manager, "live", InstanceState.RUNNING);

            Assert.Equal("1 / 1 个实例运行中", vm.StatusText);
            Assert.Equal("paper · 26.3 · 25565 · 运行中", vm.Rows[0].DetailLine);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "状态栏区分「运行中数量 / 总数」")]
    public void 状态栏计数()
    {
        Seed("a", 25565, InstanceState.STOPPED);
        Seed("b", 25566, InstanceState.STOPPED);
        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            Assert.Equal("2 个实例（均未运行）", vm.StatusText);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "只看运行中：过滤后列表与文案一致")]
    public void 只看运行中()
    {
        Seed("idle", 25565, InstanceState.STOPPED);
        Seed("busy", 25566, InstanceState.STOPPED);
        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            Assert.Equal(2, vm.Rows.Count);

            vm.OnlyRunning = true;   // 属性变化应自动触发 Refresh
            Assert.Empty(vm.Rows);
            Assert.Equal("暂无实例", vm.StatusText);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "选中项变化通过事件通知界面")]
    public void 选中事件()
    {
        Seed("alpha", 25565, InstanceState.STOPPED);
        var vm = new InstanceListViewModel(NewManager());
        try
        {
            var fired = new List<string?>();
            vm.SelectionChanged += n => fired.Add(n);

            vm.SelectedName = "alpha";
            Assert.Equal(["alpha"], fired);

            vm.SelectedName = null;
            Assert.Equal(["alpha", null], fired);

            // 重复设同一个值不应重复通知（ObservableProperty 的语义）
            vm.SelectedName = null;
            Assert.Equal(2, fired.Count);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "删除只移除注册，保留世界数据")]
    public void 删除保留世界()
    {
        Seed("victim", 25565, InstanceState.STOPPED);
        var dir = CeobeHome.InstanceDir("victim");
        Directory.CreateDirectory(Path.Combine(dir, "world"));
        File.WriteAllText(Path.Combine(dir, "world", "level.dat"), "x");

        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            Assert.Single(vm.Rows);

            vm.RemoveCommand.Execute("victim");

            Assert.Empty(vm.Rows);
            Assert.Equal("暂无实例", vm.StatusText);
            // 世界数据必须还在：误删存档是不可逆灾难
            Assert.True(File.Exists(Path.Combine(dir, "world", "level.dat")));
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "损坏的实例元数据不炸整个列表")]
    public void 损坏元数据容错()
    {
        Seed("good", 25565, InstanceState.STOPPED);
        var bad = CeobeHome.InstanceDir("bad");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "instance.json"), "{ not json");

        var vm = new InstanceListViewModel(NewManager());
        try
        {
            vm.Refresh();
            Assert.Single(vm.Rows);
            Assert.Equal("good", vm.Rows[0].Name);
            Assert.False(vm.HasError);
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "状态变化事件就地更新行，不重建列表（保住选中态与滚动位置）")]
    public void 事件就地更新()
    {
        Seed("live", 25565, InstanceState.STOPPED);
        var manager = NewManager();
        var vm = new InstanceListViewModel(manager);
        try
        {
            vm.Refresh();
            var row = Assert.Single(vm.Rows);
            Assert.Equal("已停止", row.StateLabel);
            Assert.False(row.IsLive);

            // 模拟 Core 事件：状态推进到 RUNNING
            RaiseStateChanged(manager, "live", InstanceState.RUNNING);

            Assert.Same(row, vm.Rows[0]);               // 行对象没被换掉
            Assert.Equal(InstanceState.RUNNING, row.State);
            Assert.Equal("运行中", row.StateLabel);
            Assert.True(row.IsLive);                     // 派生属性同步失效并刷新
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "CRASHED 状态如实显示（不得美化成已停止）")]
    public void 崩溃状态显示()
    {
        Seed("crashed", 25565, InstanceState.STOPPED);
        var manager = NewManager();
        var vm = new InstanceListViewModel(manager);
        try
        {
            vm.Refresh();
            RaiseStateChanged(manager, "crashed", InstanceState.CRASHED);

            Assert.Equal("崩溃", vm.Rows[0].StateLabel);
            Assert.False(vm.Rows[0].IsLive);   // CRASHED 不属于 live 态
        }
        finally
        {
            vm.Detach();
        }
    }

    [Fact(DisplayName = "Detached 后不再响应事件（避免持有已释放的 VM）")]
    public void 解绑后不再更新()
    {
        Seed("x", 25565, InstanceState.STOPPED);
        var manager = NewManager();
        var vm = new InstanceListViewModel(manager);
        vm.Refresh();
        var row = vm.Rows[0];
        vm.Detach();

        RaiseStateChanged(manager, "x", InstanceState.RUNNING);

        // 解绑后该行不应再被更新——否则 VM 已被界面丢弃却仍被 Core 事件持有
        Assert.Equal("已停止", row.StateLabel);
    }

    /// <summary>
    /// 派发一次状态变化（模拟 ServerProcess 的 transition）。
    /// 走 Core 开的 internal 测试缝 <c>InstanceManager.PublishForTest</c>：
    /// OnEvent 是 public event，外部只能订阅不能触发；而真实触发源是日志泵/看门狗，
    /// 要动它就得拉起真的 MC 服务端——那条路测不了 ViewModel 的界面逻辑。
    /// </summary>
    private static void RaiseStateChanged(InstanceManager manager, string instance, InstanceState state) =>
        manager.PublishForTest(new StateChanged(instance, state, 0));
}
