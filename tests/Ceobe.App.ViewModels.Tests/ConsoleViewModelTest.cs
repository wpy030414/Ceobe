using Ceobe.App.ViewModels;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「控制台」页签 ViewModel 验收（Java 版 ConsolePane）。
///
/// 重点是**两条日志来源都必须成立**：本进程托管走事件流，外部托管（CLI <c>start -d</c>
/// 起的监督进程）只能按文件偏移增量跟随。漏掉第二条的后果是：用户在 CLI 里起了服，
/// GUI 的控制台永远空白，看起来像坏了——而这恰是 PRD 场景 3（只有终端的玩家）与
/// 场景 2（GUI 管理多实例）会撞上的组合。
///
/// 另外守住 ADR-018 的教训：启动失败必须可见，不能只写进某个文本框就完事。
/// </summary>
[Collection("CeobeHome serial")]
public class ConsoleViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-con-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public ConsoleViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        _dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(_dir);
        WriteMeta();
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    private void WriteMeta(ServerType type = ServerType.PAPER) =>
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = _instance, ServerType = type, GameVersion = "26.3", Port = 25565,
        }));

    private static InstanceManager NewManager(JarInstaller? installer = null) =>
        new(new FakeLocator(), installer);

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    private (ConsoleViewModel Vm, InstanceManager Mgr) NewVm(InstanceManager? manager = null)
    {
        var m = manager ?? NewManager();
        var vm = new ConsoleViewModel(m, _instance);
        vm.LoadHistory();
        return (vm, m);
    }

    private string LogFile => Path.Combine(_dir, "console.log");

    // ========== 历史加载 ==========

    [Fact(DisplayName = "无 console.log 时给空日志而非崩溃")]
    public void 无历史日志()
    {
        var (vm, _) = NewVm();
        Assert.Empty(vm.Lines);
        Assert.False(vm.HasError);
        // 新建实例没有 .state 文件 → CREATED（不是 STOPPED）。
        // STOPPED 只由进程真实退出后的 transition 写入，与 Java 版 readStateFile 一致；
        // 这条语义在 InstanceLifecycleTest.查询语义 里也有守卫。
        Assert.Equal(InstanceState.CREATED, vm.State);
        // CREATED 不属于 live 态，故按钮语义仍正确（可启动、可删除）
        Assert.False(vm.IsLive);
        Assert.True(vm.CanStart);
        Assert.True(vm.CanDelete);
        vm.Detach();
    }

    [Fact(DisplayName = "加载历史日志（外部托管走文件）")]
    public void 加载历史()
    {
        File.WriteAllLines(LogFile, Enumerable.Range(1, 5).Select(i => "line-" + i));
        var (vm, _) = NewVm();

        Assert.Equal(5, vm.Lines.Count);
        Assert.Equal("line-1", vm.Lines[0]);
        Assert.Equal("line-5", vm.Lines[^1]);
        Assert.False(vm.IsHosted);
        vm.Detach();
    }

    [Fact(DisplayName = "历史只取尾部 300 行（长日志不撑爆界面）")]
    public void 历史截断()
    {
        File.WriteAllLines(LogFile, Enumerable.Range(1, 900).Select(i => "l" + i));
        var (vm, _) = NewVm();

        Assert.Equal(300, vm.Lines.Count);
        Assert.Equal("l601", vm.Lines[0]);    // 尾部 300 行的第一行
        Assert.Equal("l900", vm.Lines[^1]);
        vm.Detach();
    }

    // ========== 外部托管：文件增量跟随 ==========

    [Fact(DisplayName = "外部托管时按文件偏移增量跟随")]
    public void 增量跟随()
    {
        File.WriteAllLines(LogFile, ["a", "b"]);
        var (vm, _) = NewVm();
        Assert.Equal(2, vm.Lines.Count);

        // 模拟监督进程继续写日志
        File.AppendAllText(LogFile, "c" + Environment.NewLine + "d" + Environment.NewLine);
        vm.PollExternalLog();

        Assert.Equal(4, vm.Lines.Count);
        Assert.Equal("c", vm.Lines[2]);
        Assert.Equal("d", vm.Lines[3]);

        // 没有新内容时轮询不得重复追加
        vm.PollExternalLog();
        Assert.Equal(4, vm.Lines.Count);
        vm.Detach();
    }

    [Fact(DisplayName = "日志被轮转（文件变短）时从头读，不跳过整段")]
    public void 日志轮转()
    {
        File.WriteAllLines(LogFile, Enumerable.Range(1, 50).Select(i => "old-" + i));
        var (vm, _) = NewVm();
        Assert.Equal(50, vm.Lines.Count);

        // 轮转：新文件比记录的偏移小
        File.WriteAllLines(LogFile, ["new-1", "new-2"]);
        vm.PollExternalLog();

        Assert.Contains("new-1", vm.Lines);
        Assert.Contains("new-2", vm.Lines);
        vm.Detach();
    }

    [Fact(DisplayName = "外部托管时给出说明（不让人以为是坏的）")]
    public void 外部托管说明()
    {
        var (vm, _) = NewVm();
        Assert.Contains("外部进程托管", vm.StatusText);
        Assert.Contains("增量跟随", vm.StatusText);
        vm.Detach();
    }

    // ========== 本进程托管：事件流 ==========

    [Fact(DisplayName = "本进程托管时订阅事件流，日志实时到达")]
    public void 事件流日志()
    {
        // 用一个假 installer 让 CreateAsync 能落一个实例，再由 manager 托管 ServerProcess
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        Assert.False(vm.IsHosted);   // 还没 Start，谈不上托管

        // 直接经测试缝派发事件，验证订阅生效（真实路径是 ServerProcess 的日志泵）
        mgr.PublishForTest(new InstanceLog(_instance, "[server] Starting minecraft server"));
        Assert.Contains("[server] Starting minecraft server", vm.Lines);

        mgr.PublishForTest(new StateChanged(_instance, InstanceState.RUNNING, 0));
        Assert.Equal(InstanceState.RUNNING, vm.State);
        Assert.True(vm.IsLive);
        Assert.Equal("运行中", vm.StateLabel);
        Assert.Equal("RUNNING", vm.StateKey);
        vm.Detach();
    }

    [Fact(DisplayName = "崩溃状态如实报告并标红（不美化成已停止）")]
    public void 崩溃可见()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();

        mgr.PublishForTest(new StateChanged(_instance, InstanceState.CRASHED, 1));

        Assert.Equal(InstanceState.CRASHED, vm.State);
        Assert.Equal("崩溃", vm.StateLabel);
        Assert.True(vm.HasError);
        Assert.Contains("异常退出", vm.StatusText);
        Assert.Contains("退出码 1", vm.StatusText);
        Assert.False(vm.IsLive);
        vm.Detach();
    }

    [Fact(DisplayName = "隧道地址变化贴进日志（方便复制发给朋友）")]
    public void 隧道事件入日志()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();

        mgr.PublishForTest(new TunnelChanged(_instance, "pinggy", "tcp://x.run.pinggy-free.link:35663"));

        Assert.Contains(vm.Lines, l => l.Contains("tcp://x.run.pinggy-free.link:35663"));
        vm.Detach();
    }

    [Fact(DisplayName = "其他实例的事件不得串进来")]
    public void 事件按实例过滤()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();

        mgr.PublishForTest(new InstanceLog("别的实例", "不该出现"));

        Assert.DoesNotContain("不该出现", vm.Lines);
        vm.Detach();
    }

    [Fact(DisplayName = "Detach 后不再响应事件")]
    public void 解绑后不再更新()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        vm.Detach();

        mgr.PublishForTest(new InstanceLog(_instance, "解绑后的行"));

        Assert.DoesNotContain("解绑后的行", vm.Lines);
    }

    // ========== 日志上限 ==========

    [Fact(DisplayName = "日志行数有上限，超出丢最旧的")]
    public void 日志上限()
    {
        var (vm, _) = NewVm();
        for (var i = 0; i < ConsoleViewModel.MaxLines + 500; i++) vm.Append("l" + i);

        Assert.True(vm.Lines.Count <= ConsoleViewModel.MaxLines,
            $"界面日志必须有界，实际 {vm.Lines.Count}");
        // 最新的行必须在（用户最关心的是刚发生的）
        Assert.Equal("l" + (ConsoleViewModel.MaxLines + 499), vm.Lines[^1]);
        vm.Detach();
    }

    // ========== 按钮可用性 ==========

    [Fact(DisplayName = "停止态：可启动可删除，不可停止不可发命令")]
    public void 停止态按钮()
    {
        var (vm, _) = NewVm();
        Assert.True(vm.CanStart);
        Assert.True(vm.CanDelete);
        Assert.False(vm.CanStop);
        Assert.False(vm.CanSend);
        vm.Detach();
    }

    [Fact(DisplayName = "运行态：可停止可发命令，不可启动不可删除")]
    public void 运行态按钮()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        mgr.PublishForTest(new StateChanged(_instance, InstanceState.RUNNING, 0));

        Assert.True(vm.CanStop);
        Assert.True(vm.CanSend);
        Assert.False(vm.CanStart);
        // 运行中绝不能允许删除：删掉正在写的实例目录会毁存档
        Assert.False(vm.CanDelete);
        vm.Detach();
    }

    [Fact(DisplayName = "操作中禁用全部按钮（防重复点击）")]
    public void 忙碌态按钮()
    {
        var (vm, _) = NewVm();
        vm.IsBusy = true;
        Assert.False(vm.CanStart);
        Assert.False(vm.CanStop);
        Assert.False(vm.CanSend);
        Assert.False(vm.CanDelete);
        vm.Detach();
    }

    // ========== 命令发送 ==========

    [Fact(DisplayName = "未运行时发命令被拒并说明")]
    public async Task 未运行发命令()
    {
        var (vm, _) = NewVm();
        vm.Command = "say hi";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("未运行", vm.StatusText);
        vm.Detach();
    }

    [Fact(DisplayName = "空命令直接忽略（不发也不报错）")]
    public async Task 空命令()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        mgr.PublishForTest(new StateChanged(_instance, InstanceState.RUNNING, 0));

        vm.Command = "   ";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        vm.Detach();
    }

    [Fact(DisplayName = "运行中发命令：本进程无托管时走控制通道，通道不可用则报错")]
    public async Task 发命令无通道()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        // 事件说在运行，但既无本进程托管也无 .ctl 通道 → 必须明确报错而非静默丢弃
        mgr.PublishForTest(new StateChanged(_instance, InstanceState.RUNNING, 0));

        vm.Command = "say hi";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.NotEmpty(vm.StatusText);
        vm.Detach();
    }

    // ========== 删除（确认归 View，VM 只接受模式）==========

    [Fact(DisplayName = "仅移除：保留世界数据")]
    public void 删除保留世界()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "world"));
        File.WriteAllText(Path.Combine(_dir, "world", "level.dat"), "存档");
        var (vm, _) = NewVm();

        vm.DeleteCommand.Execute(false);

        Assert.True(vm.Deleted);
        Assert.Contains("世界数据保留", vm.StatusText);
        Assert.True(File.Exists(Path.Combine(_dir, "world", "level.dat")));
        vm.Detach();
    }

    [Fact(DisplayName = "连数据删除：清空整个目录并如实说明")]
    public void 删除含数据()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "world"));
        File.WriteAllText(Path.Combine(_dir, "world", "level.dat"), "存档");
        var (vm, _) = NewVm();

        vm.DeleteCommand.Execute(true);

        Assert.True(vm.Deleted);
        Assert.Contains("含世界数据", vm.StatusText);
        Assert.False(Directory.Exists(_dir));   // 整个实例目录没了
        vm.Detach();
    }

    [Fact(DisplayName = "运行中删除被拒（删正在写的目录会毁存档）")]
    public void 运行中禁止删除()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        mgr.PublishForTest(new StateChanged(_instance, InstanceState.RUNNING, 0));

        vm.DeleteCommand.Execute(true);

        Assert.False(vm.Deleted);
        Assert.True(vm.HasError);
        Assert.Contains("仍在运行", vm.StatusText);
        Assert.True(Directory.Exists(_dir));     // 什么都没删
        vm.Detach();
    }

    // ========== 启停 ==========

    [Fact(DisplayName = "启动失败可见（ADR-018：不得静默失败）")]
    public async Task 启动失败可见()
    {
        // 没有 server.jar → Start 必失败。失败信息必须同时进 StatusText 与日志区，
        // Java 版曾只写进某个文本框，用户看到的是「点了启动什么都没发生」。
        var (vm, _) = NewVm();
        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("启动失败", vm.StatusText);
        Assert.Contains(vm.Lines, l => l.Contains("启动失败"));
        Assert.False(vm.IsBusy);                 // 失败后必须解锁按钮，否则界面卡死
        vm.Detach();
    }

    [Fact(DisplayName = "停止不受控实例时给出可操作说明")]
    public async Task 停止不受控()
    {
        var mgr = NewManager();
        var vm = new ConsoleViewModel(mgr, _instance);
        vm.LoadHistory();
        mgr.PublishForTest(new StateChanged(_instance, InstanceState.RUNNING, 0));

        await vm.StopCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("不受控", vm.StatusText);
        Assert.False(vm.IsBusy);
        vm.Detach();
    }

    // ========== 清空显示 ==========

    [Fact(DisplayName = "清空显示不删磁盘日志")]
    public void 清空显示()
    {
        File.WriteAllLines(LogFile, ["a", "b", "c"]);
        var (vm, _) = NewVm();
        Assert.Equal(3, vm.Lines.Count);

        vm.ClearViewCommand.Execute(null);

        Assert.Empty(vm.Lines);
        Assert.Contains("console.log 未删除", vm.StatusText);
        Assert.True(File.Exists(LogFile));                       // 磁盘日志还在
        Assert.Equal(3, File.ReadAllLines(LogFile).Length);
        vm.Detach();
    }
}
