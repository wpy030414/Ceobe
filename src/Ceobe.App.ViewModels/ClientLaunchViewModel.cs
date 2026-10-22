using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Client;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「启动」分区（设计稿 01）。这一屏只有一个主行动，其余信息都是为它服务的：
/// 版本装了什么、账户是谁、上次崩没崩、有没有缺前置、配对的服务器在不在。
///
/// 设计取舍：内存分配放在这里而不是「设置」里，因为它是**实例级**参数，
/// 且调它有即时反馈（改完立刻能启动验证）；账户与 Java 是全局的，留在设置里。
/// </summary>
public partial class ClientLaunchViewModel : ObservableObject
{
    private readonly ClientRuntime _runtime;
    private readonly Action<IClientEvent> _onEvent;

    public ClientLaunchViewModel(ClientRuntime runtime, ClientMeta meta)
    {
        _runtime = runtime;
        Meta = meta;
        _onEvent = evt => UiThread.Post(() => Handle(evt));
        _runtime.OnEvent += _onEvent;
    }

    public ClientMeta Meta { get; }

    public string InstanceName => Meta.Name;

    // —— 版本装配卡 ——

    [ObservableProperty]
    private string _versionTitle = "";

    [ObservableProperty]
    private string _loaderChip = "";

    [ObservableProperty]
    private string _summaryLine = "";

    [ObservableProperty]
    private string _javaLine = "";

    [ObservableProperty]
    private string _statusLine = "";

    [ObservableProperty]
    private int _memoryMb;

    [ObservableProperty]
    private string _memoryLabel = "";

    /// <summary>内存占比（0–1），用于卡片里的进度条；上限取 16 GB 的固定刻度。</summary>
    [ObservableProperty]
    private double _memoryFraction;

    public int MemoryMax => 16384;

    // —— 账户 ——

    [ObservableProperty]
    private string _accountName = "";

    [ObservableProperty]
    private string _accountSub = "";

    // —— 启动按钮 ——

    [ObservableProperty]
    private ClientState _state = ClientState.STOPPED;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _launchLabel = "启动游戏";

    [ObservableProperty]
    private string _launchHint = "";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private bool _hasProgress;

    [ObservableProperty]
    private double _progressValue;

    public bool IsLive => State.IsLive();

    /// <summary>
    /// 启动页那颗按钮的文案。它**只负责启动**——运行中的停止入口在实例头，
    /// 一屏只有一个主行动，不能出现两颗都能「启停」的按钮抢焦点。
    /// </summary>
    /// <summary>按钮文案固定：状态变化只影响可用性，不改变文字（训练员要求）。</summary>
    public string PrimaryActionLabel => "启动游戏";

    public bool CanLaunch => !IsLive && !IsBusy;

    public bool CanStop => IsLive && !IsBusy;

    /// <summary>启动页不提供强制终止（危险动作放在实例溢出菜单里），故只有优雅停止。</summary>
    public string StartStopLabel => State switch
    {
        ClientState.STARTING => "启动中…",
        ClientState.RUNNING => "停止游戏",
        ClientState.STOPPING => "停止中…",
        _ => "启动游戏",
    };

    partial void OnStateChanged(ClientState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(StartStopLabel));
        LaunchLabel = StartStopLabel;
        RefreshStatusLine();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanStop));
    }

    partial void OnMemoryMbChanged(int value)
    {
        MemoryLabel = $"{value} MB";
        MemoryFraction = Math.Clamp(value / (double)MemoryMax, 0, 1);
        OnPropertyChanged(nameof(MemoryValue));
    }

    /// <summary>Slider 绑定用（控件是 double，模型是 int）。</summary>
    public double MemoryValue
    {
        get => MemoryMb;
        set
        {
            var v = (int)Math.Round(value);
            if (v != MemoryMb) MemoryMb = v;
        }
    }

    // —— 诊断横幅 ——

    [ObservableProperty]
    private bool _hasBanner;

    [ObservableProperty]
    private bool _isBannerError;

    /// <summary>黄色警告条：有横幅且不是错误（错误条自己有一条）。</summary>
    public bool IsBannerWarning => HasBanner && !IsBannerError;

    [ObservableProperty]
    private string _bannerTitle = "";

    [ObservableProperty]
    private string _bannerDetail = "";

    [ObservableProperty]
    private string _bannerAction = "";

    partial void OnHasBannerChanged(bool value) => OnPropertyChanged(nameof(IsBannerWarning));

    partial void OnIsBannerErrorChanged(bool value) => OnPropertyChanged(nameof(IsBannerWarning));

    // —— 右栏 ——

    public ObservableCollection<ClientSaveRow> RecentSaves { get; } = [];
    public ObservableCollection<ClientServerShortcut> Servers { get; } = [];

    [ObservableProperty]
    private string _lastSessionLabel = "尚无会话记录";

    [ObservableProperty]
    private string _lastExitLabel = "";

    [ObservableProperty]
    private string _lastExitBrushKey = "TextFillColorSecondaryBrush";

    /// <summary>由页面在切回本分区时调用：磁盘/进程状态都可能被别处改过。</summary>
    public void Reload()
    {
        try
        {
            var latest = ClientStore.Find(Meta.Name);
            if (latest is not null)
            {
                Meta.MemoryMb = latest.MemoryMb;
                Meta.PlayerName = latest.PlayerName;
                Meta.PairedServer = latest.PairedServer;
                Meta.LastLaunchedAt = latest.LastLaunchedAt;
                Meta.LastSessionSeconds = latest.LastSessionSeconds;
                Meta.LastExit = latest.LastExit;
            }
        }
        catch (Exception)
        {
            // 元数据读失败时沿用内存里的副本，界面仍可用
        }

        State = _runtime.StateOf(Meta.Name);
        MemoryMb = Meta.MemoryMb;

        VersionTitle = $"Minecraft {Meta.GameVersion}";
        LoaderChip = Meta.Loader == ClientLoader.VANILLA
            ? "原版"
            : $"{Meta.Loader.Label()} {Meta.LoaderVersion}";

        ReloadAccount();
        ReloadSummary();
        ReloadDiagnostics();
        ReloadSaves();
        ReloadServers();
        ReloadLastSession();
        RefreshStatusLine();
    }

    private void ReloadAccount()
    {
        var settings = ClientSettings.Load();
        var account = settings.ActiveOrDefault();
        // 实例可以覆盖玩家名（同一台机器上不同存档想用不同名字的情况真实存在）
        if (!string.IsNullOrWhiteSpace(Meta.PlayerName)
            && !string.Equals(Meta.PlayerName, account.Name, StringComparison.OrdinalIgnoreCase))
        {
            account = new ClientAccount { Name = Meta.PlayerName, Kind = account.Kind, AccessToken = account.AccessToken, Uuid = account.Uuid };
        }
        AccountName = account.Name;
        AccountSub = account.KindLabel;
    }

    private void ReloadSummary()
    {
        var mods = Meta.Loader == ClientLoader.VANILLA ? 0 : ClientModScanner.Scan(Meta.Name).Count;
        var packs = PackCatalog.List(Meta.Name).Count(p => p.Enabled);
        var shaders = PackCatalog.ListShaders(Meta.Name).Count;
        var java = "Java ?";
        try
        {
            java = $"Java {ClientVersion.Resolve(Meta).JavaMajor}";
        }
        catch (CeobeException)
        {
        }
        SummaryLine = $"{mods} 个 Mod · {packs} 个资源包 · {shaders} 套光影 · {java}";
        JavaLine = java;
    }

    private void ReloadDiagnostics()
    {
        var list = ClientDiagnostics.Check(Meta);
        var top = list.FirstOrDefault();
        if (top is null)
        {
            HasBanner = false;
            return;
        }
        HasBanner = true;
        IsBannerError = top.Level == DiagnosticLevel.Error;
        BannerTitle = top.Title;
        BannerDetail = top.Detail;
        BannerAction = top.Action ?? "";
    }

    /// <summary>没有存档时显示一行说明（ItemsControl 空着会让人以为加载失败）。</summary>
    public bool NoSaves => RecentSaves.Count == 0;

    private void ReloadSaves()
    {
        RecentSaves.Clear();
        foreach (var s in SaveCatalog.List(Meta.Name).Take(3))
        {
            RecentSaves.Add(new ClientSaveRow(s.Folder, s.LevelName, s.GameMode,
                s.LastPlayed is { } t ? t.ToString("MM-dd HH:mm") : "—",
                TextFormat.HumanSize(s.SizeBytes)));
        }
        OnPropertyChanged(nameof(NoSaves));
    }

    private void ReloadServers()
    {
        Servers.Clear();
        var entries = ClientServerLinks.Collect(Meta);
        if (entries.Count == 0)
        {
            Servers.Add(new ClientServerShortcut("尚未配对服务端", "在「服务器」分区可以选择", "", false, false));
            return;
        }
        foreach (var e in entries.Take(3))
        {
            var live = e.Name == Meta.PairedServer;
            Servers.Add(new ClientServerShortcut(e.Name, e.Ip, live ? "已配对" : "", true, live));
        }
    }

    private void ReloadLastSession()
    {
        if (Meta.LastLaunchedAt is null)
        {
            LastSessionLabel = "尚无会话记录";
            LastExitLabel = "";
            LastExitBrushKey = "TextFillColorSecondaryBrush";
            return;
        }
        var when = Meta.LastLaunchedAt.Value.ToLocalTime();
        var duration = Meta.LastSessionSeconds is { } sec
            ? $"{sec / 60} 分 {sec % 60} 秒"
            : "未知时长";
        LastSessionLabel = $"{when:yyyy-MM-dd HH:mm} · {duration}";
        if (Meta.LastExit == "crashed")
        {
            LastExitLabel = "上次异常退出";
            LastExitBrushKey = "SystemFillColorCriticalBrush";
        }
        else
        {
            LastExitLabel = "上次正常退出";
            LastExitBrushKey = "SystemFillColorSuccessBrush";
        }
    }

    private void RefreshStatusLine()
    {
        if (IsLive)
        {
            var pid = _runtime.PidOf(Meta.Name);
            StatusLine = pid is { } p ? $"运行中 · PID {p}" : "运行中";
            LaunchHint = "游戏窗口已交给 Java 进程";
            return;
        }
        if (Meta.LastLaunchedAt is { } last)
        {
            StatusLine = $"就绪 · 上次启动 {last.ToLocalTime():MM-dd HH:mm}";
            LaunchHint = Meta.LastExit == "crashed" ? "上次是崩溃退出，先看下方的诊断" : "点击启动，进入游戏";
        }
        else
        {
            StatusLine = "就绪 · 还没有启动过";
            LaunchHint = "首次启动会解压与校验文件，可能多花十几秒";
        }
    }

    private void Handle(IClientEvent evt)
    {
        if (evt.Instance != Meta.Name) return;
        switch (evt)
        {
            case ClientStateChanged sc:
                State = sc.State;
                IsBusy = false;
                if (sc.State is ClientState.STOPPED or ClientState.CRASHED)
                {
                    HasProgress = false;
                    Reload();
                }
                break;
            case ClientLogLine log:
                // 启动阶段的关键行直接显示在按钮下方：用户不必切到日志页就能看到「正在下载 assets」
                if (log.Line.StartsWith("[Ceobe]", StringComparison.Ordinal)) ProgressText = log.Line[7..].Trim();
                break;
        }
    }

    /// <summary>启动。<paramref name="serverAddress"/> 非空时直接带 --server 进服（服务器分区的「加入」）。</summary>
    [RelayCommand]
    private async Task LaunchAsync(string? serverAddress)
    {
        if (IsBusy || IsLive) return;
        IsBusy = true;
        HasProgress = true;
        ProgressText = "正在准备启动…";
        ProgressValue = 0;
        try
        {
            var settings = ClientSettings.Load();
            var account = settings.ActiveOrDefault();
            if (!string.IsNullOrWhiteSpace(Meta.PlayerName)
                && !string.Equals(Meta.PlayerName, account.Name, StringComparison.OrdinalIgnoreCase))
            {
                account = new ClientAccount
                {
                    Name = Meta.PlayerName,
                    Kind = account.Kind,
                    AccessToken = account.AccessToken,
                    Uuid = account.Uuid,
                };
            }

            var plan = await Task.Run(() => _runtime.Start(Meta, account, serverAddress)).ConfigureAwait(true);
            ProgressText = "已启动：" + plan.Version.MainClass;
            HasProgress = false;
            ReloadLastSession();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            IsBannerError = true;
            HasBanner = true;
            BannerTitle = "启动失败";
            BannerDetail = e.Message;
            BannerAction = "修复游戏文件";
            ProgressText = "启动失败：" + e.Message.Split('\n')[0];
        }
        catch (Exception e)
        {
            HasBanner = true;
            IsBannerError = true;
            BannerTitle = "启动失败";
            BannerDetail = e.Message;
            ProgressText = "启动失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsLive || IsBusy) return;
        IsBusy = true;
        ProgressText = "正在请求游戏退出（会先让游戏自己存盘）…";
        try
        {
            await _runtime.StopAsync(Meta.Name).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 修复游戏文件：重新执行装配（幂等）。这是把「重装启动器」这种建议变成一键动作——
    /// 缺 jar / 缺 natives / 缺加载器 profile 的诊断都应该指向这里。
    /// </summary>
    [RelayCommand]
    private async Task RepairAsync()
    {
        if (IsBusy || IsLive) return;
        IsBusy = true;
        HasProgress = true;
        ProgressValue = 0;
        try
        {
            var sink = new ProgressSink(this);
            await Task.Run(() => ClientInstaller.InstallAsync(Meta, sink)).ConfigureAwait(true);
            ProgressText = "游戏文件已修复";
            Reload();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasBanner = true;
            IsBannerError = true;
            BannerTitle = "修复失败";
            BannerDetail = e.Message;
            ProgressText = "修复失败";
        }
        finally
        {
            HasProgress = false;
            IsBusy = false;
        }
    }

    /// <summary>内存改动即时落盘：这是实例级参数，用户改完通常立刻启动验证。</summary>
    [RelayCommand]
    private void SaveMemory()
    {
        try
        {
            var latest = ClientStore.Find(Meta.Name) ?? Meta;
            latest.MemoryMb = MemoryMb;
            ClientStore.Save(latest);
            ReloadDiagnostics();
        }
        catch (CeobeException e)
        {
            HasBanner = true;
            IsBannerError = true;
            BannerTitle = "保存失败";
            BannerDetail = e.Message;
        }
    }

    public void Detach() => _runtime.OnEvent -= _onEvent;

    /// <summary>把 Core 的进度回调 marshal 回 UI 线程（IProgressSink 不做上下文捕获）。</summary>
    private sealed class ProgressSink(ClientLaunchViewModel owner) : IProgressSink
    {
        public void Update(string stage, long done, long total) => UiThread.Post(() =>
        {
            owner.ProgressText = stage;
            owner.HasProgress = true;
            owner.ProgressValue = total > 0 ? Math.Clamp(done / (double)total * 100, 0, 100) : 0;
        });
    }
}

/// <summary>最近存档行。</summary>
public sealed record ClientSaveRow(string Folder, string Name, string GameMode, string Date, string Size);

/// <summary>启动页右栏的服务器快捷入口。</summary>
public sealed record ClientServerShortcut(string Name, string Address, string Tag, bool CanJoin, bool Primary);
