using Ceobe.App.ViewModels;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「设置」页 ViewModel 验收（整改项 3：数据目录可改 + 搬家向导）。
///
/// 训练员原话：「服务器存放位置貌似无法修改地址，至少我没看到（它默认到了我的c盘）」。
/// Java 版 AppSettings 只有 theme 一个字段，CeobeHome 也不认设置文件，所以确实无入口。
///
/// 搬家是本项目**唯一不可逆且可能毁掉存档**的操作，故测试重心在：
/// 1. 预演必须净零副作用（看完点取消，磁盘上不该多出任何东西）；
/// 2. 阻塞项必须真拦住（运行中实例、目标已有实例、不可写、空间不足）；
/// 3. 执行失败必须回滚（不能留下半搬状态，两边都不完整）；
/// 4. OneDrive 重定向要警告（Windows 专属新雷，Java 版不会遇到）。
/// </summary>
[Collection("CeobeHome serial")]
public class SettingsViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-set-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";

    public SettingsViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        SeedInstance(_instance, port: 25565, live: false);
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    /// <summary>落一个实例；live=true 时写 .pid 指向当前测试进程（Core 会认定它存活）。</summary>
    private void SeedInstance(string name, int port, bool live)
    {
        var dir = CeobeHome.InstanceDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = name, ServerType = ServerType.PAPER, GameVersion = "26.3", Port = port,
        }));
        // 世界文件：搬家必须原样带走，丢了就是毁存档
        Directory.CreateDirectory(Path.Combine(dir, "world"));
        File.WriteAllText(Path.Combine(dir, "world", "level.dat"), "world-data-" + name);
        if (live)
        {
            File.WriteAllText(Path.Combine(dir, ".state"), "RUNNING");
            File.WriteAllText(Path.Combine(dir, ".pid"), Environment.ProcessId.ToString());
        }
    }

    private SettingsViewModel NewVm() => new(new InstanceManager(new FakeLocator()));

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    private static string NewTarget() =>
        Path.Combine(Path.GetTempPath(), "ceobe-move-" + Guid.NewGuid().ToString("N")[..8]);

    // ========== 基本设置 ==========

    [Fact(DisplayName = "加载出当前数据目录与主题")]
    public void 加载()
    {
        var vm = NewVm();
        Assert.Equal(_home, vm.CurrentHome);
        Assert.Equal("system", vm.Theme);   // 2026-10-22：默认跟随系统
        Assert.False(vm.DefaultWhitelistEnabled);   // 整改项 2：默认不再强开白名单
        Assert.Null(vm.Plan);
    }

    [Fact(DisplayName = "主题三态保存与规范化")]
    public void 主题三态()
    {
        var vm = NewVm();

        vm.Theme = "dark";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("dark", AppSettings.Load().Theme);
        Assert.True(AppSettings.Load().IsDark);

        vm.Theme = "light";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("light", AppSettings.Load().Theme);
        Assert.False(AppSettings.Load().IsDark);

        vm.Theme = "system";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("system", AppSettings.Load().Theme);

        // 未知值一律收敛到跟随系统，不写脏数据
        vm.Theme = "sepia";
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal("system", AppSettings.Load().Theme);
    }

    [Fact(DisplayName = "保存主题与默认白名单")]
    public void 保存设置()
    {
        var vm = NewVm();
        vm.Theme = "dark";
        vm.DefaultWhitelistEnabled = true;
        vm.SaveSettingsCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Contains("已保存", vm.StatusText);

        var back = AppSettings.Load();
        Assert.Equal("dark", back.Theme);
        Assert.True(back.IsDark);
        Assert.True(back.DefaultWhitelistEnabled);
    }

    // ========== 预演 ==========

    [Fact(DisplayName = "预演列出实例与体量，且无阻塞项")]
    public async Task 预演通过()
    {
        SeedInstance("second", 25566, live: false);
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Plan);
            Assert.True(vm.Plan!.CanExecute);
            Assert.Empty(vm.Plan.Blockers);
            Assert.Equal(2, vm.Plan.InstanceCount);
            Assert.True(vm.Plan.TotalBytes > 0);
            Assert.False(vm.HasError);
            Assert.Contains("预演通过", vm.StatusText);
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "预演净零副作用：不创建目标目录")]
    public async Task 预演无副作用()
    {
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Plan);
            // 早期版本在这里 CreateDirectory(target)，用户点「取消」后磁盘上会多出空目录，
            // 与「只读预演」的承诺矛盾。改为探测最近已存在祖先目录的可写性。
            Assert.False(Directory.Exists(target), "预演不得创建目标目录");
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "取消搬家后不留任何残留")]
    public async Task 取消无残留()
    {
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);
            vm.CancelMoveCommand.Execute(null);

            Assert.Null(vm.Plan);
            Assert.Null(vm.ProposedHome);
            Assert.False(Directory.Exists(target));
            // 原目录完好
            Assert.True(File.Exists(Path.Combine(CeobeHome.InstanceDir(_instance), "world", "level.dat")));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "未选目标目录时预演报错")]
    public async Task 预演缺目标()
    {
        var vm = NewVm();
        vm.ProposedHome = "   ";
        await vm.PlanMoveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Null(vm.Plan);
        Assert.Contains("选择目标目录", vm.StatusText);
    }

    [Fact(DisplayName = "目标与当前相同被拒")]
    public async Task 目标同当前()
    {
        var vm = NewVm();
        vm.ProposedHome = _home;
        await vm.PlanMoveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Contains("相同", vm.StatusText);
    }

    // ========== 阻塞项 ==========

    [Fact(DisplayName = "运行中实例硬阻塞（搬它会毁存档）")]
    public async Task 运行中阻塞()
    {
        SeedInstance("running", 25567, live: true);
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Plan);
            Assert.False(vm.Plan!.CanExecute);
            Assert.Contains(vm.Plan.Blockers, b => b.Contains("running") && b.Contains("正在运行"));
            Assert.True(vm.HasError);

            // 未预演通过就不许执行
            await vm.ExecuteMoveCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);
            Assert.Contains("预演", vm.StatusText);
            // 原目录完好
            Assert.True(Directory.Exists(CeobeHome.InstanceDir("running")));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "目标已含实例时阻塞（同名会被覆盖）")]
    public async Task 目标已有实例()
    {
        // 目标目录里先放一个「别的 home」的实例，模拟用户选了一个已在用的目录
        var target = NewTarget();
        var saved = CeobeHome.Override;
        try
        {
            CeobeHome.Override = target;
            SeedInstance("occupant", 25570, live: false);   // 落在 target/instances/occupant
            CeobeHome.Override = saved;

            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Plan);
            Assert.False(vm.Plan!.CanExecute);
            Assert.Contains(vm.Plan.Blockers, b => b.Contains("已含") && b.Contains("覆盖"));
            Assert.True(vm.HasError);
        }
        finally
        {
            CeobeHome.Override = saved;
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "执行阶段复查运行态：预演后启动了服就不许搬")]
    public async Task 执行前复查()
    {
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);
            Assert.True(vm.Plan!.CanExecute);

            // 预演之后、执行之前把实例变成运行态
            SeedInstance("late", 25571, live: true);

            await vm.ExecuteMoveCommand.ExecuteAsync(null);

            Assert.True(vm.HasError);
            Assert.Contains("仍在运行", vm.StatusText);
            Assert.Contains("late", vm.StatusText);
            // 没有搬动任何东西
            Assert.True(Directory.Exists(CeobeHome.InstanceDir(_instance)));
            Assert.True(Directory.Exists(CeobeHome.InstanceDir("late")));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    // ========== 执行与回滚 ==========

    [Fact(DisplayName = "搬家带走世界数据并生效新目录")]
    public async Task 搬家成功()
    {
        SeedInstance("second", 25566, live: false);
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);
            await vm.ExecuteMoveCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.StatusText);
            Assert.Contains("搬家完成", vm.StatusText);
            Assert.Equal(target, vm.CurrentHome);          // 已生效
            Assert.Equal(target, AppSettings.Load().HomePath);

            // 世界数据必须完整搬过去（丢存档是不可逆灾难）
            Assert.Equal("world-data-srv",
                File.ReadAllText(Path.Combine(target, "instances", "srv", "world", "level.dat")));
            Assert.Equal("world-data-second",
                File.ReadAllText(Path.Combine(target, "instances", "second", "world", "level.dat")));
            // 源目录不该再有实例
            Assert.False(Directory.Exists(Path.Combine(_home, "instances", "srv")));
            // settings.json 留在原处（它记录 HomePath 本身，搬过去会指向旧值）
            Assert.True(File.Exists(Path.Combine(_home, "settings.json")));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "搬家后 settings.json 双写：旧目录锚点 + 新目录一致")]
    public async Task settings双写()
    {
        // settings.json 记录 HomePath，而它自身又存放在 HomePath 里（循环依赖）。
        // 少写任何一份都会出事：
        //   缺旧目录那份 → 重启后 Override 为 null，CeobeHome 回落默认目录，
        //                  实例列表变空，用户以为存档全丢了；
        //   缺新目录那份 → Apply() 之后 Load() 读到默认值，主题等偏好凭空重置。
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.Theme = "dark";
            vm.SaveSettingsCommand.Execute(null);
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);
            await vm.ExecuteMoveCommand.ExecuteAsync(null);
            Assert.False(vm.HasError, vm.StatusText);

            // ① 旧目录锚点：重启后 Override 为 null，CeobeHome 回落到默认目录并从这里读 HomePath。
            //    注意不能靠「清 Override 再 Load()」来验证——那样会读到**真实的** %USERPROFILE%\.ceobe
            //    （测试的 _home 是临时目录，不是默认位置），既测不到锚点还会污染用户真实配置。
            //    故直接读锚点文件内容，验的就是「重启后能找回新位置」这条不变量本身。
            var anchor = Path.Combine(_home, "settings.json");
            Assert.True(File.Exists(anchor), "旧目录必须留下 settings.json 作为重启锚点");
            var anchorSettings = JsonUtil.Parse<AppSettings>(File.ReadAllText(anchor));
            Assert.Equal(target, anchorSettings.HomePath);
            Assert.Equal("dark", anchorSettings.Theme);

            // ② 新目录那份：偏好设置不丢
            CeobeHome.Override = target;
            var moved = AppSettings.Load();
            Assert.Equal(target, moved.HomePath);
            Assert.Equal("dark", moved.Theme);
        }
        finally
        {
            CeobeHome.Override = _home;
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "跳过预演直接执行被拒")]
    public async Task 跳过预演被拒()
    {
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.ExecuteMoveCommand.ExecuteAsync(null);   // 没先 Plan

            Assert.True(vm.HasError);
            Assert.Contains("预演", vm.StatusText);
            Assert.True(Directory.Exists(CeobeHome.InstanceDir(_instance)));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "预演后目录变化会让旧预演作废")]
    public async Task 预演过期作废()
    {
        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);
            await vm.ExecuteMoveCommand.ExecuteAsync(null);

            // 执行完 Plan 必须清空，否则用户点第二次会拿旧计划搬到已失效的路径
            Assert.Null(vm.Plan);
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    // ========== OneDrive 与警告 ==========

    [Fact(DisplayName = "当前目录在 OneDrive 下时给出红字警告")]
    public void OneDrive警告()
    {
        var oneDriveHome = Path.Combine(Path.GetTempPath(), "OneDrive", "ceobe-" + Guid.NewGuid().ToString("N")[..8]);
        var saved = CeobeHome.Override;
        try
        {
            Directory.CreateDirectory(oneDriveHome);
            CeobeHome.Override = oneDriveHome;
            var vm = NewVm();
            Assert.NotNull(vm.OneDriveWarning);
            Assert.Contains("OneDrive", vm.OneDriveWarning);
            Assert.Contains("世界文件", vm.OneDriveWarning);
        }
        finally
        {
            CeobeHome.Override = saved;
            ZipUtil.DeleteRecursively(oneDriveHome);
        }
    }

    [Fact(DisplayName = "普通目录不触发 OneDrive 警告")]
    public void 普通目录无警告()
    {
        var vm = NewVm();
        Assert.Null(vm.OneDriveWarning);
    }

    [Fact(DisplayName = "目标是 OneDrive 时在预演里警告")]
    public async Task 目标OneDrive警告()
    {
        var target = Path.Combine(Path.GetTempPath(), "OneDrive", "ceobe-t-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);

            Assert.NotNull(vm.Plan);
            Assert.True(vm.Plan!.CanExecute);        // 是警告不是阻塞：用户可能确实想放那儿
            Assert.Contains(vm.Plan.Warnings, w => w.Contains("OneDrive"));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "cache 与 jdk 一起搬（否则新目录要重下几百 MB）")]
    public async Task 缓存一起搬()
    {
        Directory.CreateDirectory(Path.Combine(_home, "cache", "jars"));
        File.WriteAllText(Path.Combine(_home, "cache", "jars", "paper.jar"), "cached-jar");
        Directory.CreateDirectory(Path.Combine(_home, "jdk", "jdk-21"));
        File.WriteAllText(Path.Combine(_home, "jdk", "jdk-21", "release"), "JAVA_VERSION=\"21\"");

        var target = NewTarget();
        try
        {
            var vm = NewVm();
            vm.ProposedHome = target;
            await vm.PlanMoveCommand.ExecuteAsync(null);
            await vm.ExecuteMoveCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.StatusText);
            Assert.Equal("cached-jar", File.ReadAllText(Path.Combine(target, "cache", "jars", "paper.jar")));
            Assert.True(Directory.Exists(Path.Combine(target, "jdk", "jdk-21")));
        }
        finally
        {
            ZipUtil.DeleteRecursively(target);
        }
    }

    [Fact(DisplayName = "预演报告跨盘风险（复制而非移动）")]
    public async Task 跨盘警告()
    {
        // 临时目录与 _home 同盘时不会有这条警告；用一个明显不同盘符的目标触发
        var otherDrive = OperatingSystem.IsWindows() ? @"Q:\ceobe-move-test" : "/nonexistent-mount/ceobe";
        var vm = NewVm();
        vm.ProposedHome = otherDrive;
        await vm.PlanMoveCommand.ExecuteAsync(null);

        // 不存在的盘/挂载点应被识别为阻塞或警告，绝不能报「预演通过」
        Assert.NotNull(vm.Plan);
        Assert.True(vm.Plan!.Blockers.Count > 0 || vm.Plan.Warnings.Count > 0);
    }
}
