using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「穿透」页签 ViewModel（整改项 1 的界面落地）。
///
/// 训练员原话：「PinggyTCP只有本地连接正常」。本页把原来只有「已开启 / 未开启」两态的卡片
/// 升级为带**可达性三态**的卡片：
/// <list type="bullet">
/// <item>已实测可达 ✓ —— 从公网/组网侧打了一次 MC status ping 并拿到合法响应</item>
/// <item>无法验证 —— 探测没通过，但**不等于通道坏了**（可能有防火墙/频率限制）</item>
/// <item>未探测 —— 该通道类型不做 MC 探测（Cloudflare 是 HTTP 通道）</item>
/// </list>
/// 三态必须分开措辞：把「未探测」显示成「失败」会让用户以为一切正常其实是没测，
/// 把「无法验证」显示成「失败」会让人去修一个没坏的东西。
///
/// 另外新增 Tailscale 卡片作为长期联机首选——Pinggy 免费通道会话短且地址每次变化，
/// MC 客户端的服务器地址不能频繁改，这是结构性不合拍（见 ADR-019 / MIGRATION 整改项 1c）。
/// </summary>
public partial class TunnelViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly string _instance;
    private readonly string _dir;

    /// <summary>
    /// 本进程托管时的隧道管理器（GUI 进程本身就是监督进程，对齐 Java 版 CeobeGui）。
    /// 为 null 表示实例由别的进程托管，此时只能走控制通道。
    /// </summary>
    private readonly TunnelManager? _localTunnels;

    /// <summary>四条通道卡片，顺序即界面顺序（玩家通道排最前）。</summary>
    public ObservableCollection<TunnelCard> Cards { get; } = [];

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>是否有操作正在进行（建链可达 45 秒，必须禁用按钮防止重复点击）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>实例是否在运行——玩家通道要求服务端就绪，否则只会得到指向空端口的死通道。</summary>
    [ObservableProperty]
    private bool _serverRunning;

    /// <summary>
    /// server-ip 与隧道目标是否冲突（整改项 1a）。非 null 时页面顶部显示红字警告，
    /// 因为这种配置下 Pinggy 必然连不上——正是训练员描述的症状之一。
    /// </summary>
    [ObservableProperty]
    private string? _bindingWarning;

    public TunnelViewModel(InstanceManager manager, string instance, TunnelManager? localTunnels = null)
    {
        _manager = manager;
        _instance = instance;
        _dir = manager.Dir(instance);
        _localTunnels = localTunnels;

        foreach (var kind in CardOrder)
        {
            Cards.Add(new TunnelCard(kind));
        }
    }

    /// <summary>玩家通道优先，Web 控制台其次，最后是两种「稳定地址」方案。</summary>
    private static readonly TunnelKind[] CardOrder =
    [
        TunnelKind.PINGGY, TunnelKind.TAILSCALE, TunnelKind.LAN, TunnelKind.CLOUDFLARE,
    ];

    /// <summary>刷新：读取实例状态、绑定冲突、各通道现状。</summary>
    public void Refresh()
    {
        try
        {
            var status = _manager.Find(_instance);
            ServerRunning = status?.State.IsLive() ?? false;
            BindingWarning = DetectBindingConflict();
            HasError = false;

            // QueryActive 会走文件 I/O 或控制通道往返，只查一次后复用
            var active = QueryActive();
            var activeKinds = active.Select(a => a.Kind).ToHashSet();
            foreach (var (kind, report) in active)
            {
                FindCard(kind)?.Apply(report);
            }
            // 不在活动列表里的卡片复位（通道已被关掉或从未开启）
            foreach (var card in Cards.Where(c => !activeKinds.Contains(c.Kind)))
            {
                card.Reset();
            }
            // 局域网地址无需开启：即使没有活动会话也直接展示本机地址
            EnsureLanCard(status?.Port ?? 25565);
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "读取穿透状态失败: " + e.Message;
        }
    }

    /// <summary>
    /// 整改项 1(a)：检测 server-ip 与回环隧道目标是否冲突。
    /// 隧道恒打 127.0.0.1，而服务端一旦绑到具体网卡地址就不再监听回环——
    /// 结果就是「本地正常、外部连不上」。这条警告必须在开启通道之前就显示出来。
    /// </summary>
    private string? DetectBindingConflict()
    {
        var path = Path.Combine(_dir, "server.properties");
        if (!File.Exists(path)) return null;
        try
        {
            var f = Core.Config.ServerPropertiesFile.Load(path);
            var raw = f.Get("server-ip");
            if (string.IsNullOrWhiteSpace(raw)) return null;   // 空 = 监听全部地址，兼容
            var v = raw.Trim().ToLowerInvariant();
            // 全部地址与回环字面量都兼容回环隧道
            if (v is "" or "0.0.0.0" or "::" or "::0" or "localhost" or "127.0.0.1" or "::1") return null;
            return $"server-ip = {raw}：服务端只监听该地址，而穿透通道打到 127.0.0.1，"
                   + "玩家从公网连进来必然失败（表现为「只有本地能连」）。"
                   + "请把 server-ip 留空（监听全部地址）后重启服务端。";
        }
        catch (CeobeException)
        {
            return null;
        }
    }

    /// <summary>查询当前活动通道（本进程托管走 TunnelManager，否则走控制通道）。</summary>
    private List<(TunnelKind Kind, TunnelReport Report)> QueryActive()
    {
        var outList = new List<(TunnelKind, TunnelReport)>();
        if (_localTunnels is not null)
        {
            foreach (var s in _localTunnels.ActiveSessions(_instance))
            {
                outList.Add((s.Kind, new TunnelReport(s.PublicUrl, s.ExpiresAt, s.Verified, s.Diagnostic)));
            }
            return outList;
        }
        if (!InstanceControl.IsAvailable(_dir)) return outList;
        try
        {
            foreach (var (kindId, report) in InstanceControl.TunnelStatusReports(_dir))
            {
                try
                {
                    outList.Add((TunnelKindInfo.FromId(kindId), report));
                }
                catch (CeobeException)
                {
                    // 未知通道 id：跳过，不影响其余卡片
                }
            }
        }
        catch (CeobeException)
        {
            // 控制通道刚失效：当作无活动通道
        }
        return outList;
    }

    private void EnsureLanCard(int port)
    {
        var card = FindCard(TunnelKind.LAN);
        if (card is null || !string.IsNullOrEmpty(card.Address)) return;
        var addrs = LanDirect.LocalAddresses();
        if (addrs.Count == 0)
        {
            card.Note = LanDirectUnavailable;
            return;
        }
        // 局域网地址无需「开启」：直接展示，用户复制即可
        card.Apply(new TunnelReport(addrs[0] + ":" + port, null, null, null));
        card.AlwaysAvailable = true;
        card.Note = "同一局域网内的朋友直接填这个地址即可（无需任何工具）。";
    }

    private const string LanDirectUnavailable = "未检测到可用的局域网 IPv4 地址。";

    private TunnelCard? FindCard(TunnelKind kind) => Cards.FirstOrDefault(c => c.Kind == kind);

    /// <summary>开启或关闭一条通道。</summary>
    [RelayCommand]
    private async Task ToggleAsync(string kindId, CancellationToken ct)
    {
        var card = FindCard(TunnelKindInfo.FromId(kindId));
        if (card is null) return;

        // 局域网是纯展示，没有开关语义
        if (card.Kind == TunnelKind.LAN)
        {
            StatusText = "局域网地址无需开启，直接复制给朋友即可。";
            return;
        }

        var isUp = card.IsUp;
        IsBusy = true;
        card.IsBusy = true;
        try
        {
            if (isUp)
            {
                await CloseAsync(card, ct).ConfigureAwait(true);
            }
            else
            {
                await OpenAsync(card, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            // 绑定冲突（整改项 1a）会从这里抛出：消息本身就含两个可执行选项，原样展示
            HasError = true;
            card.Note = "失败: " + e.Message;
            StatusText = "操作失败";
        }
        finally
        {
            IsBusy = false;
            card.IsBusy = false;
        }
    }

    private async Task OpenAsync(TunnelCard card, CancellationToken ct)
    {
        TunnelReport report;
        if (_localTunnels is not null)
        {
            var s = await _localTunnels.UpAsync(_instance, card.Kind, ct).ConfigureAwait(true);
            report = new TunnelReport(s.PublicUrl, s.ExpiresAt, s.Verified, s.Diagnostic);
        }
        else
        {
            if (!InstanceControl.IsAvailable(_dir))
            {
                throw new CeobeException("实例未在运行（需先启动，且由 ceobe 托管）");
            }
            // 跨进程：走控制通道，诊断经 TunnelReport 线格式传回（整改项 1b 的链路）
            report = await Task.Run(() => InstanceControl.TunnelUpReport(_dir, card.Kind.Id()), ct)
                .ConfigureAwait(true);
        }
        card.Apply(report);
        HasError = false;
        StatusText = DescribeVerdict(card);
    }

    private async Task CloseAsync(TunnelCard card, CancellationToken ct)
    {
        if (_localTunnels is not null)
        {
            _localTunnels.Down(_instance, card.Kind);
        }
        else if (InstanceControl.IsAvailable(_dir))
        {
            await Task.Run(() => InstanceControl.TunnelDown(_dir, card.Kind.Id()), ct).ConfigureAwait(true);
        }
        card.Reset();
        StatusText = $"{card.Label} 已关闭";
    }

    /// <summary>
    /// 把可达性三态翻译成人话。这是整改项 1 的产品核心：
    /// 过去界面只说「已开启 + 地址」，用户没法区分「地址给了但连不上」和「真的通了」。
    /// </summary>
    private static string DescribeVerdict(TunnelCard card)
    {
        return card.Verified switch
        {
            true => $"{card.Label} 已开启并实测可达：{card.Address}",
            false => $"{card.Label} 已开启，但无法验证可达性——地址已给出，若朋友连不上请展开诊断详情",
            null => $"{card.Label} 已开启：{card.Address}",
        };
    }

    /// <summary>地址轮换通知（Pinggy 约 55 分钟续期一次，地址会变）。</summary>
    public void OnAddressChanged(string kindId, string? publicUrl)
    {
        var card = FindCard(TunnelKindInfo.FromId(kindId));
        if (card is null) return;
        card.Address = publicUrl ?? "";
        // 轮换必须显眼提示：玩家那边存的是旧地址，不重新分享就连不上
        card.AddressRotated = true;
        card.Note = "地址已轮换，请重新分享给朋友（他们保存的旧地址已失效）。";
        HasError = false;
        StatusText = $"{card.Label} 地址已轮换，请重新分享";
    }

    /// <summary>把 Core 的实例事件接到卡片上（供界面订阅 manager.OnEvent 后转发）。</summary>
    public void HandleEvent(IInstanceEvent evt) => UiThread.Post(() => HandleEventCore(evt));

    private void HandleEventCore(IInstanceEvent evt)
    {
        if (evt.Instance != _instance) return;
        if (evt is TunnelChanged tc) OnAddressChanged(tc.Kind, tc.PublicUrl);
        else if (evt is StateChanged sc)
        {
            ServerRunning = sc.State.IsLive();
            if (!sc.State.IsLive())
            {
                // 实例停了，所有通道也随之失效
                foreach (var card in Cards.Where(c => c.Kind != TunnelKind.LAN)) card.Reset();
                StatusText = "实例已停止，通道随之关闭。";
            }
        }
    }
}

/// <summary>单条通道卡片（可绑定投影）。</summary>
public partial class TunnelCard : ObservableObject
{
    public TunnelCard(TunnelKind kind) => Kind = kind;

    public TunnelKind Kind { get; }

    /// <summary>中文标签（取自 Core 的唯一权威，避免两处文案漂移）。</summary>
    public string Label => Kind.ZhLabel();

    [ObservableProperty]
    private string _address = "";

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private bool _isUp;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>局域网这类「无需开启」的通道：开关按钮应隐藏而非可点。</summary>
    [ObservableProperty]
    private bool _alwaysAvailable;

    /// <summary>地址是否刚轮换过（界面用醒目色提示重新分享）。</summary>
    [ObservableProperty]
    private bool _addressRotated;

    /// <summary>到期时间；null = 不过期。</summary>
    [ObservableProperty]
    private DateTimeOffset? _expiresAt;

    /// <summary>可达性三态（整改项 1b）。</summary>
    [ObservableProperty]
    private bool? _verified;

    /// <summary>探测诊断原文（界面可展开查看）。</summary>
    [ObservableProperty]
    private string? _diagnostic;

    /// <summary>
    /// 可达性文案。三态严格区分：
    /// null 是「该通道类型不做 MC 探测」，不能说成失败。
    /// </summary>
    public string VerdictLabel => Verified switch
    {
        true => "已实测可达 ✓",
        false => "无法验证",
        null => "未探测",
    };

    /// <summary>是否该把可达性显示为警告色（只有明确 false 才是）。</summary>
    public bool VerdictIsWarning => Verified == false;

    /// <summary>是否该显示为成功色。</summary>
    public bool VerdictIsOk => Verified == true;

    /// <summary>状态徽标文案。</summary>
    public string StateLabel => IsUp ? "已开启" : (AlwaysAvailable ? "可用" : "未开启");

    /// <summary>按钮文案（局域网无开关语义）。</summary>
    public string ToggleLabel => Kind == TunnelKind.LAN ? "局域网" : (IsUp ? "关闭" : "开启");

    /// <summary>按钮是否可点（局域网纯展示、操作中禁用）。</summary>
    public bool CanToggle => Kind != TunnelKind.LAN && !IsBusy;

    partial void OnIsUpChanged(bool value)
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(CanToggle));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));

    partial void OnAlwaysAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(CanToggle));
    }

    partial void OnVerifiedChanged(bool? value)
    {
        OnPropertyChanged(nameof(VerdictLabel));
        OnPropertyChanged(nameof(VerdictIsWarning));
        OnPropertyChanged(nameof(VerdictIsOk));
    }

    /// <summary>套用一份报告（本进程会话或跨进程线格式）。</summary>
    public void Apply(TunnelReport report)
    {
        Address = report.Url ?? "";
        ExpiresAt = report.ExpiresAt;
        Verified = report.Verified;
        Diagnostic = report.Diagnostic;
        IsUp = !string.IsNullOrEmpty(report.Url);
        if (IsUp) AddressRotated = false;
        OnPropertyChanged(nameof(StateLabel));
    }

    /// <summary>通道关闭后复位，但保留诊断原文供排查。</summary>
    public void Reset()
    {
        Address = "";
        IsUp = false;
        ExpiresAt = null;
        Verified = null;
        AddressRotated = false;
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
    }
}
