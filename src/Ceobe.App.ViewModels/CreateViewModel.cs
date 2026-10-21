using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Config;
using Ceobe.Core.Model;
using Ceobe.Core.Providers;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「新建实例」向导 ViewModel（Java 版 CreateDialog）。
///
/// 承载 PRD 的核心承诺：「让一个普通玩家在 10 分钟内开出第一个可被公网朋友直连的 mod 服」。
/// 故默认值要能一把过——paper / 最新版 / 2G / 自动端口，小白不改任何字段也能创建成功。
///
/// 整改项 2 在此落地：白名单不再默认开启，而是**显式问一次**（Java 版默认 true 又没入口，
/// 导致新手开服后朋友进不来）。
/// </summary>
public partial class CreateViewModel : ObservableObject
{
    private readonly InstanceManager _manager;

    /// <summary>可选服务端类型。顺序即界面的下拉顺序（主人指定：原版/Paper/Purpur/Fabric/NeoForge）。</summary>
    public IReadOnlyList<TypeOption> Types { get; } =
    [
        new(ServerType.VANILLA, "原版"),
        new(ServerType.PAPER, "Paper"),
        new(ServerType.PURPUR, "Purpur"),
        new(ServerType.FABRIC, "Fabric"),
        new(ServerType.NEOFORGE, "NeoForge"),
    ];

    [ObservableProperty]
    private ServerType _selectedType = ServerType.PAPER;

    /// <summary>实例名（小写字母/数字/_/-，1-32）。</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>游戏版本；空 = 最新正式版。</summary>
    [ObservableProperty]
    private string _gameVersion = "";

    /// <summary>版本下拉的可选项（版本号 + 发布日期）。</summary>
    public ObservableCollection<GameVersionOption> GameVersions { get; } = [];

    /// <summary>版本下拉的当前选中项。</summary>
    [ObservableProperty]
    private GameVersionOption? _selectedGameVersion;

    /// <summary>正在从镜像加载版本清单。</summary>
    [ObservableProperty]
    private bool _versionsLoading;

    /// <summary>版本清单加载失败的原因；null = 正常。</summary>
    [ObservableProperty]
    private string? _versionsError;

    private int _versionLoadGeneration;

    private static readonly ConcurrentDictionary<ServerType, (DateTimeOffset At, IReadOnlyList<string> Ids)>
        ProviderVersionCache = new();
    private static readonly TimeSpan ProviderVersionCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>加载器版本（Fabric/NeoForge）；空 = 最新 stable。</summary>
    [ObservableProperty]
    private string? _loaderVersion;

    /// <summary>内存上限（MB），默认 2G。</summary>
    [ObservableProperty]
    private int _memoryMb = 2048;

    /// <summary>服务器端口；0 或空 = 自动选空闲端口。</summary>
    [ObservableProperty]
    private int _port;

    [ObservableProperty]
    private bool _autoPort = true;

    /// <summary>整改项 2：白名单默认**关闭**，由用户显式决定。</summary>
    [ObservableProperty]
    private bool _whitelistEnabled;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>创建进行中（下载服务端可能几十秒，须禁用按钮）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>创建成功后置位，界面据此关闭对话框并选中新实例。</summary>
    [ObservableProperty]
    private string? _createdInstance;

    public CreateViewModel(InstanceManager manager, bool defaultWhitelist = false)
    {
        _manager = manager;
        _whitelistEnabled = defaultWhitelist;
        // 自动端口：先探一个空闲端口填进去，用户能看见实际会用哪个
        try
        {
            _port = manager.SuggestFreePort(25565);
        }
        catch (CeobeException)
        {
            _port = 25565;
        }
    }

    /// <summary>选中类型变化时，模组类加载器版本框才有意义。</summary>
    public bool NeedsLoaderVersion => SelectedType is ServerType.FABRIC or ServerType.NEOFORGE;

    /// <summary>
    /// ComboBox 的绑定目标。直接绑 <see cref="SelectedType"/> 时，ItemsSource 晚于
    /// SelectedValue 赋值会让下拉框显示为空；用选项对象本身做 TwoWay 绑定最稳。
    /// </summary>
    public TypeOption SelectedTypeOption
    {
        get => Types.FirstOrDefault(t => t.Type == SelectedType) ?? Types[0];
        set
        {
            if (value is not null && value.Type != SelectedType) SelectedType = value.Type;
        }
    }

    partial void OnSelectedTypeChanged(ServerType value)
    {
        OnPropertyChanged(nameof(NeedsLoaderVersion));
        OnPropertyChanged(nameof(SelectedTypeOption));
    }

    /// <summary>创建按钮是否可点：不在加载/创建中，且已经选出一个游戏版本。</summary>
    public bool CanSubmit => !IsBusy && !VersionsLoading && !string.IsNullOrWhiteSpace(GameVersion);

    public bool HasVersionsError => VersionsError is { Length: > 0 };

    partial void OnGameVersionChanged(string value) => OnPropertyChanged(nameof(CanSubmit));

    partial void OnSelectedGameVersionChanged(GameVersionOption? value)
    {
        GameVersion = value?.Id ?? "";
        OnPropertyChanged(nameof(CanSubmit));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    partial void OnVersionsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));

    partial void OnVersionsErrorChanged(string? value) => OnPropertyChanged(nameof(HasVersionsError));

    /// <summary>
    /// 从镜像加载「版本号 + 发布日期」并默认选中最新正式版。
    /// 只展示该服务端类型实际支持的版本：Paper/Purpur/Fabric/NeoForge 的 provider 列表与
    /// Mojang 正式版清单取交集，日期来自 Mojang 的 releaseTime。
    /// </summary>
    public async Task LoadVersionsAsync(CancellationToken ct = default)
    {
        var generation = ++_versionLoadGeneration;
        VersionsLoading = true;
        VersionsError = null;

        try
        {
            // manifest 与 provider 版本列表互不依赖：并行取，省掉一半等待。
            var catalogTask = MojangProvider.ListReleasesAsync(ct);
            var idsTask = SelectedType == ServerType.VANILLA
                ? null
                : GetProviderVersionsAsync(SelectedType, ct);

            var catalog = await catalogTask.ConfigureAwait(true);
            var byId = catalog.ToDictionary(v => v.Id, StringComparer.Ordinal);

            IReadOnlyList<string> ids = idsTask is null
                ? catalog.Select(v => v.Id).ToList()
                : await idsTask.ConfigureAwait(true);

            // 更慢的请求可能后返回；旧结果直接丢弃，避免把类型切回去时被覆盖。
            if (generation != _versionLoadGeneration) return;

            var options = new List<GameVersionOption>();
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var info))
                {
                    options.Add(new GameVersionOption(id, info.ReleaseTime));
                }
            }

            GameVersions.Clear();
            foreach (var option in options) GameVersions.Add(option);

            SelectedGameVersion = GameVersions.FirstOrDefault();
            if (GameVersions.Count == 0)
            {
                VersionsError = $"没有可用的 {SelectedType} 正式版";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            if (generation == _versionLoadGeneration)
            {
                GameVersions.Clear();
                SelectedGameVersion = null;
                VersionsError = "版本列表加载失败: " + e.Message;
            }
        }
        finally
        {
            if (generation == _versionLoadGeneration) VersionsLoading = false;
        }
    }

    /// <summary>provider 的版本 ID 列表（Paper/Purpur/Fabric/NeoForge），10 分钟内复用。</summary>
    private static async Task<IReadOnlyList<string>> GetProviderVersionsAsync(
        ServerType type, CancellationToken ct)
    {
        if (ProviderVersionCache.TryGetValue(type, out var cached)
            && DateTimeOffset.UtcNow - cached.At < ProviderVersionCacheTtl)
        {
            return cached.Ids;
        }

        var ids = await ProviderRegistry.Of(type).ListVersionsAsync(ct).ConfigureAwait(false);
        ProviderVersionCache[type] = (DateTimeOffset.UtcNow, ids);
        return ids;
    }

    /// <summary>
    /// NumberBox.Value 是 double，VM 的 MemoryMb 是 int；直接 x:Bind 会撞 WMC1121。
    /// 与 ModpacksPage.NewMemoryMbDouble / PropField.NumberValueDouble 同一个原因。
    /// </summary>
    public double MemoryMbDouble
    {
        get => MemoryMb;
        set
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) MemoryMb = (int)Math.Round(value);
        }
    }

    public double PortDouble
    {
        get => Port;
        set
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) Port = (int)Math.Round(value);
        }
    }

    partial void OnMemoryMbChanged(int value) => OnPropertyChanged(nameof(MemoryMbDouble));

    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(PortDouble));

    /// <summary>创建实例。成功置 <see cref="CreatedInstance"/>，失败给可执行提示。</summary>
    [RelayCommand]
    private async Task CreateAsync(CancellationToken ct)
    {
        HasError = false;
        CreatedInstance = null;

        // 纯本地校验先行：名字/内存/端口写错时立刻失败，不必联网查版本
        var effectivePort = AutoPort ? _manager.SuggestFreePort(25565) : Port;
        InstanceSpec spec;
        try
        {
            spec = new InstanceSpec(
                Name.Trim(), SelectedType,
                string.IsNullOrWhiteSpace(GameVersion) ? LatestPlaceholder : GameVersion.Trim(),
                string.IsNullOrWhiteSpace(LoaderVersion) ? null : LoaderVersion.Trim(),
                MemoryMb, "", effectivePort, null);
            spec.Validate();
        }
        catch (CeobeSpec e)
        {
            HasError = true;
            StatusText = e.Message;
            return;
        }

        IsBusy = true;
        try
        {
            var progress = new VmProgress(this);
            // 版本为空时由 Core 的 provider 选最新正式版（CreateAsync 内部处理）
            var version = string.IsNullOrWhiteSpace(GameVersion) ? null : GameVersion.Trim();
            var realSpec = version is null
                ? spec with { GameVersion = await ResolveLatestAsync(SelectedType, ct).ConfigureAwait(true) }
                : spec;

            var meta = await _manager.CreateAsync(realSpec, progress, ct).ConfigureAwait(true);

            // 整改项 2：把用户在向导里的白名单选择写进 server.properties
            ApplyWhitelistChoice(meta.Name);

            CreatedInstance = meta.Name;
            HasError = false;
            StatusText = $"实例 {meta.Name} 创建完成";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户真的取消：交给上层（上层是 async void 事件，不再从这里抛）
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient 超时同样表现为 TaskCanceledException，但 ct 并未被取消。
            // 早期版本无条件 rethrow，于是「下载超时」从 async void 事件里直接冒到
            // 未处理异常处理器：界面看起来只是卡着，日志里却是一整段崩溃堆栈。
            HasError = true;
            StatusText = "创建超时：查询版本或下载服务端长时间无响应，请检查网络后重试";
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException
                                     or System.IO.IOException
                                     or System.Net.Sockets.SocketException)
        {
            HasError = true;
            StatusText = "创建失败（网络）: " + e.Message + "；可检查网络/镜像后重试";
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "创建失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>占位版本：仅用于触发本地校验，真正版本在 CreateAsync 前用最新正式版替换。</summary>
    private const string LatestPlaceholder = "latest";

    private static async Task<string> ResolveLatestAsync(ServerType type, CancellationToken ct)
    {
        var versions = await ProviderRegistry.Of(type).ListVersionsAsync(ct).ConfigureAwait(true);
        return versions.FirstOrDefault()
               ?? throw new CeobeException($"获取 {type} 版本列表失败（检查网络或改用镜像）");
    }

    /// <summary>把向导里的白名单选择写进新建实例的 server.properties。</summary>
    private void ApplyWhitelistChoice(string instanceName)
    {
        try
        {
            var path = Path.Combine(_manager.Dir(instanceName), "server.properties");
            if (!File.Exists(path)) return;
            var f = ServerPropertiesFile.Load(path);
            f.Set("white-list", WhitelistEnabled ? "true" : "false");
            // 白名单开着才需要强制，否则留着 false
            f.Set("enforce-whitelist", "false");
            f.Save();
        }
        catch (CeobeException)
        {
            // 写失败不致命：实例已建好，白名单可稍后在「玩家」页签调
        }
    }

    /// <summary>把 VM 进度接到界面文本（下载服务端时必须有反馈）。</summary>
    private sealed class VmProgress(CreateViewModel vm) : IProgressSink
    {
        private string? _last;

        // 回调来自下载线程：必须 marshal 回 UI 线程再改属性，否则 x:Bind 会跨线程碰控件
        public void Update(string stage, long done, long total) => UiThread.Post(() =>
        {
            if (stage == _last) return;
            _last = stage;
            vm.StatusText = stage;
        });
    }
}

/// <summary>服务端类型选项（界面下拉用）。</summary>
public sealed record TypeOption(ServerType Type, string Label);

/// <summary>游戏版本下拉项：标题为版本号，副标题为发布日期（Mojang manifest 的 releaseTime）。</summary>
public sealed record GameVersionOption(string Id, DateTimeOffset? ReleaseTime)
{
    public string ReleasedLabel =>
        ReleaseTime?.UtcDateTime.ToString("yyyy-MM-dd") ?? "发布日期未知";
}
