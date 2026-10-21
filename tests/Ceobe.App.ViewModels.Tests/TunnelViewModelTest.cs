using Ceobe.App.ViewModels;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// 「穿透」页签 ViewModel 验收（整改项 1）。
///
/// 训练员原话：「PinggyTCP只有本地连接正常」。这份测试守住三条产品语义，
/// 它们都是「代码能跑但用户被误导」的失败模式：
///
/// 1. **可达性三态不得混淆**（整改项 1b）。null = 该通道不做 MC 探测（Cloudflare），
///    false = 探测未通过但不等于坏了，true = 实测可达。把 null 显示成「失败」
///    会让人去修一个没坏的东西，把 false 显示成「成功」会让人以为通了。
/// 2. **绑定冲突要在开启之前就警告**（整改项 1a）。server-ip 绑了具体网卡地址时，
///    回环隧道必然打不通——症状正是训练员描述的那句「只有本地连接正常」。
/// 3. **地址轮换必须显眼提示**。Pinggy 续期后地址变了，玩家存的旧地址失效，
///    界面若静默更新就等于让玩家白等。
///
/// 全部离线：不启动真隧道、不连公网。跨进程路径用假报告注入。
/// </summary>
[Collection("CeobeHome serial")]
public class TunnelViewModelTest : IDisposable
{
    private readonly string _home =
        Path.Combine(Path.GetTempPath(), "ceobe-tunnel-vm-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _savedHome;
    private readonly string _instance = "srv";
    private readonly string _dir;

    public TunnelViewModelTest()
    {
        _savedHome = CeobeHome.Override;
        Directory.CreateDirectory(_home);
        CeobeHome.Override = _home;
        _dir = CeobeHome.InstanceDir(_instance);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "instance.json"), JsonUtil.Write(new InstanceMeta
        {
            Name = _instance, ServerType = ServerType.PAPER, GameVersion = "26.3", Port = 25565,
        }));
        // server-ip 留空 = 监听全部地址（与回环隧道兼容），这是默认且推荐形态
        File.WriteAllText(Path.Combine(_dir, "server.properties"),
            "server-ip=\nserver-port=25565\nonline-mode=true\n");
    }

    public void Dispose()
    {
        CeobeHome.Override = _savedHome;
        ZipUtil.DeleteRecursively(_home);
    }

    private void WriteServerIp(string value) =>
        File.WriteAllText(Path.Combine(_dir, "server.properties"),
            $"server-ip={value}\nserver-port=25565\nonline-mode=true\n");

    private TunnelViewModel NewVm()
    {
        var vm = new TunnelViewModel(new InstanceManager(new FakeLocator()), _instance);
        vm.Refresh();
        return vm;
    }

    private sealed class FakeLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion) => "/bin/java";
    }

    private static TunnelCard Card(TunnelViewModel vm, TunnelKind kind) => vm.Cards.First(c => c.Kind == kind);

    // ========== 卡片结构 ==========

    [Fact(DisplayName = "四条通道齐全，玩家通道排最前")]
    public void 卡片顺序()
    {
        var vm = NewVm();
        Assert.Equal(4, vm.Cards.Count);
        // PINGGY 是玩家连接的唯一公网 TCP 通道，必须排第一（用户最常用）
        Assert.Equal(TunnelKind.PINGGY, vm.Cards[0].Kind);
        Assert.Equal(TunnelKind.TAILSCALE, vm.Cards[1].Kind);
        Assert.Equal(TunnelKind.LAN, vm.Cards[2].Kind);
        Assert.Equal(TunnelKind.CLOUDFLARE, vm.Cards[3].Kind);
    }

    [Fact(DisplayName = "标签取自 Core 的唯一权威（避免两处文案漂移）")]
    public void 标签来自Core()
    {
        var vm = NewVm();
        foreach (var card in vm.Cards)
        {
            Assert.Equal(card.Kind.ZhLabel(), card.Label);
        }
    }

    [Fact(DisplayName = "局域网卡片无需开启：按钮不可点且标为可用")]
    public void 局域网无开关()
    {
        var vm = NewVm();
        var lan = Card(vm, TunnelKind.LAN);
        Assert.False(lan.CanToggle);
        Assert.Equal("局域网", lan.ToggleLabel);
        // 本机有 IPv4 时直接给出地址；没有时给出可执行说明（两种都不是错误）
        Assert.True(!string.IsNullOrEmpty(lan.Address) || lan.Note.Contains("局域网 IPv4"));
    }

    [Fact(DisplayName = "非局域网卡片默认可开启")]
    public void 默认按钮态()
    {
        var vm = NewVm();
        var pinggy = Card(vm, TunnelKind.PINGGY);
        Assert.False(pinggy.IsUp);
        Assert.True(pinggy.CanToggle);
        Assert.Equal("开启", pinggy.ToggleLabel);
        Assert.Equal("未开启", pinggy.StateLabel);
    }

    // ========== 整改项 1(a)：绑定冲突警告 ==========

    [Fact(DisplayName = "server-ip 绑具体地址时警告（正是「只有本地能连」的成因）")]
    public void 绑定冲突警告()
    {
        WriteServerIp("192.168.1.50");
        var vm = NewVm();

        Assert.NotNull(vm.BindingWarning);
        Assert.Contains("192.168.1.50", vm.BindingWarning);
        Assert.Contains("127.0.0.1", vm.BindingWarning);   // 说明隧道目标
        Assert.Contains("只有本地能连", vm.BindingWarning);   // 用训练员的原话对上症状
        Assert.Contains("留空", vm.BindingWarning);          // 给出可执行修正
    }

    [Theory(DisplayName = "兼容形态不告警：空 / 0.0.0.0 / :: / 回环字面量")]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::0")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void 兼容绑定不告警(string serverIp)
    {
        WriteServerIp(serverIp);
        var vm = NewVm();
        Assert.Null(vm.BindingWarning);
    }

    [Fact(DisplayName = "大写与空白的 server-ip 也能识别（不得因大小写漏判）")]
    public void 大小写与空白()
    {
        WriteServerIp("  LOCALHOST  ");
        Assert.Null(NewVm().BindingWarning);

        WriteServerIp("10.0.0.7");
        Assert.NotNull(NewVm().BindingWarning);
    }

    [Fact(DisplayName = "无 server.properties 时不告警也不崩")]
    public void 无配置文件()
    {
        File.Delete(Path.Combine(_dir, "server.properties"));
        var vm = NewVm();
        Assert.Null(vm.BindingWarning);
        Assert.False(vm.HasError);
    }

    // ========== 整改项 1(b)：可达性三态 ==========

    [Fact(DisplayName = "true = 已实测可达")]
    public void 三态_可达()
    {
        var card = new TunnelCard(TunnelKind.PINGGY);
        card.Apply(new TunnelReport("tcp://h:1", null, true, "已取得 MC status 响应"));

        Assert.Equal("已实测可达 ✓", card.VerdictLabel);
        Assert.True(card.VerdictIsOk);
        Assert.False(card.VerdictIsWarning);
        Assert.True(card.IsUp);
    }

    [Fact(DisplayName = "false = 无法验证，措辞不得说成「失败」")]
    public void 三态_无法验证()
    {
        var card = new TunnelCard(TunnelKind.PINGGY);
        card.Apply(new TunnelReport("tcp://h:1", null, false, "探测超时（可能有防火墙）"));

        Assert.Equal("无法验证", card.VerdictLabel);
        Assert.True(card.VerdictIsWarning);
        Assert.False(card.VerdictIsOk);
        // 关键：通道地址仍然给出，IsUp 仍为 true——「无法验证」不等于「没开起来」
        Assert.True(card.IsUp);
        Assert.Equal("tcp://h:1", card.Address);
        Assert.DoesNotContain("失败", card.VerdictLabel);
    }

    [Fact(DisplayName = "null = 未探测（Cloudflare 这类 HTTP 通道），不得显示为失败")]
    public void 三态_未探测()
    {
        var card = new TunnelCard(TunnelKind.CLOUDFLARE);
        card.Apply(new TunnelReport("https://x.trycloudflare.com", null, null, null));

        Assert.Equal("未探测", card.VerdictLabel);
        Assert.False(card.VerdictIsWarning);
        Assert.False(card.VerdictIsOk);
        Assert.True(card.IsUp);
    }

    [Fact(DisplayName = "三态文案两两不同（混淆即产品缺陷）")]
    public void 三态互不混淆()
    {
        var ok = new TunnelCard(TunnelKind.PINGGY);
        ok.Apply(new TunnelReport("u", null, true, null));
        var unknown = new TunnelCard(TunnelKind.PINGGY);
        unknown.Apply(new TunnelReport("u", null, false, null));
        var none = new TunnelCard(TunnelKind.PINGGY);
        none.Apply(new TunnelReport("u", null, null, null));

        var labels = new[] { ok.VerdictLabel, unknown.VerdictLabel, none.VerdictLabel };
        Assert.Equal(3, labels.Distinct().Count());
    }

    [Fact(DisplayName = "诊断原文可展开查看（不得只给结论）")]
    public void 诊断可查()
    {
        var card = new TunnelCard(TunnelKind.PINGGY);
        const string diag = "探测未取得 MC status 响应：连接在 5 秒内未建立（可能被防火墙拦截）";
        card.Apply(new TunnelReport("tcp://h:1", null, false, diag));

        Assert.Equal(diag, card.Diagnostic);
        var shown = Assert.IsType<string>(card.Diagnostic);   // 显式收窄可空，再断言长度
        Assert.True(shown.Length > 20, "诊断应含可据以排查的细节");
    }

    // ========== 地址轮换 ==========

    [Fact(DisplayName = "地址轮换显眼提示重新分享")]
    public void 地址轮换()
    {
        var vm = NewVm();
        var card = Card(vm, TunnelKind.PINGGY);
        card.Apply(new TunnelReport("tcp://old:1", null, true, null));
        Assert.False(card.AddressRotated);

        vm.OnAddressChanged("pinggy", "tcp://new:2");

        Assert.Equal("tcp://new:2", card.Address);
        Assert.True(card.AddressRotated);
        Assert.Contains("轮换", card.Note);
        Assert.Contains("重新分享", card.Note);
        Assert.Contains("轮换", vm.StatusText);
        Assert.False(vm.HasError);
    }

    [Fact(DisplayName = "重新 Apply 后清除轮换标记（用户已看到新地址）")]
    public void 轮换标记复位()
    {
        var card = new TunnelCard(TunnelKind.PINGGY);
        card.Apply(new TunnelReport("tcp://a:1", null, true, null));
        card.AddressRotated = true;

        card.Apply(new TunnelReport("tcp://b:2", null, true, null));

        Assert.False(card.AddressRotated);
    }

    [Fact(DisplayName = "轮换事件只作用于对应实例（多实例互不串台）")]
    public void 事件按实例过滤()
    {
        var vm = NewVm();
        var card = Card(vm, TunnelKind.PINGGY);

        vm.HandleEvent(new TunnelChanged("别的实例", "pinggy", "tcp://x:1"));

        Assert.Equal("", card.Address);          // 不是本实例的事件，不该改卡片
        Assert.False(card.AddressRotated);
    }

    // ========== 事件联动 ==========

    [Fact(DisplayName = "实例停止时非局域网卡片复位并说明原因")]
    public void 实例停止联动()
    {
        var vm = NewVm();
        var pinggy = Card(vm, TunnelKind.PINGGY);
        pinggy.Apply(new TunnelReport("tcp://h:1", null, true, null));

        vm.HandleEvent(new StateChanged(_instance, InstanceState.STOPPED, 0));

        Assert.False(vm.ServerRunning);
        Assert.False(pinggy.IsUp);
        Assert.Contains("已停止", vm.StatusText);
    }

    [Fact(DisplayName = "实例启动事件更新 ServerRunning")]
    public void 实例启动联动()
    {
        var vm = NewVm();
        Assert.False(vm.ServerRunning);

        vm.HandleEvent(new StateChanged(_instance, InstanceState.RUNNING, 0));

        Assert.True(vm.ServerRunning);
    }

    // ========== 局域网展示 ==========

    [Fact(DisplayName = "局域网卡片带上实例端口")]
    public void 局域网端口()
    {
        var vm = NewVm();
        var lan = Card(vm, TunnelKind.LAN);
        if (string.IsNullOrEmpty(lan.Address)) return;   // 本机无 IPv4 时跳过
        Assert.EndsWith(":25565", lan.Address);
    }

    [Fact(DisplayName = "Reset 清空地址但保留排查线索")]
    public void Reset语义()
    {
        var card = new TunnelCard(TunnelKind.PINGGY);
        card.Apply(new TunnelReport("tcp://h:1", DateTimeOffset.UtcNow.AddHours(1), false, "探测超时"));

        card.Reset();

        Assert.Equal("", card.Address);
        Assert.False(card.IsUp);
        Assert.Null(card.ExpiresAt);
        Assert.Null(card.Verified);
        Assert.Equal("未开启", card.StateLabel);
        Assert.Equal("开启", card.ToggleLabel);
    }

    [Fact(DisplayName = "过期时间可展示（Pinggy 约 55 分钟）")]
    public void 过期时间()
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(55);
        var card = new TunnelCard(TunnelKind.PINGGY);
        card.Apply(new TunnelReport("tcp://h:1", expiry, true, null));

        Assert.NotNull(card.ExpiresAt);
        Assert.Equal(expiry, card.ExpiresAt);
    }
}
