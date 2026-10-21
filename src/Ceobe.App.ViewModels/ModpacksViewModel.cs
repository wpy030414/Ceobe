using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「整合包」页签 ViewModel（Java 版 <c>ModpackPane</c>）：搜索 Modrinth 整合包（.mrpack），
/// 既可装到本实例，也可一键新建匹配实例再装。
///
/// 训练员反馈「整合包下载我没看懂怎么用」，本页把两条路径拆得很直白：
/// 「装到当前这个实例」与「按整合包新建一个实例」——后者的价值在于整合包自带
/// MC 版本与加载器要求，新建实例能自动匹配，用户不必自己搞清该选 Fabric 还是 NeoForge。
///
/// 校验语义逐条照搬 Java（每条错了都会让用户装出一个起不来的服）：
/// - 装到本实例：本实例必须有模组加载器（原版/Paper 没有）；
///   整合包声明的 mcVersion / loader 必须与本实例一致，否则拦下并说清差异；
/// - 新建并装：整合包**必须声明 mcVersion**（否则不知道建什么版本的实例），
///   服务端类型由 loader 反查（ServerType.FromLoaderId，不支持的 loader 直接报错）。
/// </summary>
public partial class ModpacksViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly ModrinthClient _client = new();
    private readonly string _instance;
    private readonly string _dir;
    private readonly InstanceMeta _meta;

    /// <summary>搜索结果。</summary>
    public ObservableCollection<ModpackRow> Results { get; } = [];

    [ObservableProperty]
    private string _query = "";

    /// <summary>仅显示适配本实例的整合包（按 gameVersion + loader 过滤）。</summary>
    [ObservableProperty]
    private bool _onlyCompatible = true;

    [ObservableProperty]
    private ModpackRow? _selected;

    /// <summary>新建实例用的名字（默认取整合包标识）。</summary>
    [ObservableProperty]
    private string _newName = "";

    /// <summary>新建实例用的内存（MB）。</summary>
    [ObservableProperty]
    private int _newMemoryMb = 4096;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>进度 0..1；-1 = 未知（界面显示不定进度条）。</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _serverRunning;

    public ModpacksViewModel(InstanceManager manager, string instance)
    {
        _manager = manager;
        _instance = instance;
        _dir = manager.Dir(instance);
        _meta = manager.Meta(instance);
    }

    /// <summary>本实例是否有模组加载器（原版/Paper 没有，装不了整合包）。</summary>
    public bool SupportsModpacks => _meta.ServerType.HasModsDir() && _meta.ServerType.LoaderId() is not null;

    /// <summary>不支持时的说明（界面直接展示，不让用户点了才报错）。</summary>
    public string UnsupportedHint =>
        _meta.ServerType.ZhLabel() + " 不支持安装整合包（没有模组加载器）。\n" +
        "可以用下面的「新建实例并安装」——它会按整合包要求自动建一个 Fabric/NeoForge 实例。";

    public bool CanInstallHere => Selected is not null && !IsBusy && SupportsModpacks && !ServerRunning;
    public bool CanCreateInstall => Selected is not null && !IsBusy;

    /// <summary>运行中禁止装到本实例（改文件不生效且可能撞文件锁）。</summary>
    public string InstallHereBlockedHint => ServerRunning ? "实例运行中；停止后方可安装到本实例。" : "";

    partial void OnSelectedChanged(ModpackRow? value)
    {
        OnPropertyChanged(nameof(CanInstallHere));
        OnPropertyChanged(nameof(CanCreateInstall));
        // 选中即把新实例名预填为整合包标识（对齐 Java 版），用户可直接改
        if (value is not null && string.IsNullOrWhiteSpace(NewName)) NewName = value.Slug;
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallHere));
        OnPropertyChanged(nameof(CanCreateInstall));
    }

    partial void OnServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallHere));
        OnPropertyChanged(nameof(InstallHereBlockedHint));
    }

    /// <summary>刷新运行态（页签每次显示时调用）。</summary>
    public void Refresh() => ServerRunning = _manager.Find(_instance)?.State.IsLive() ?? false;

    [RelayCommand]
    private async Task SearchAsync(CancellationToken ct)
    {
        IsBusy = true;
        HasError = false;
        Progress = -1;
        StatusText = "搜索中…";
        try
        {
            // onlyCompatible：按本实例的 gameVersion + loader 过滤，避免装一个跑不起来的包
            var gv = OnlyCompatible ? _meta.GameVersion : null;
            var loader = OnlyCompatible ? _meta.ServerType.LoaderId() : null;
            var hits = await _client.SearchPacks(Query.Trim(), gv, loader, 25, ct).ConfigureAwait(true);
            Results.Clear();
            foreach (var h in hits)
            {
                Results.Add(new ModpackRow(h.Slug, h.Title, h.Author, h.Downloads,
                    string.Join(",", h.Categories.Take(3)), h.ProjectId));
            }
            StatusText = hits.Count == 0 ? "没有匹配的整合包" : $"共 {hits.Count} 个结果";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "搜索失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    /// <summary>安装到本实例。校验 loader 与 mcVersion 必须与本实例一致。</summary>
    [RelayCommand]
    private async Task InstallHereAsync(CancellationToken ct)
    {
        var hit = Selected;
        if (hit is null)
        {
            HasError = true;
            StatusText = "请先选择一个整合包";
            return;
        }
        if (!SupportsModpacks)
        {
            HasError = true;
            StatusText = UnsupportedHint;
            return;
        }
        if (ServerRunning)
        {
            HasError = true;
            StatusText = InstallHereBlockedHint;
            return;
        }

        IsBusy = true;
        HasError = false;
        Progress = -1;
        var loader = _meta.ServerType.LoaderId()!;
        try
        {
            var pv = await _client.ResolvePack(hit.Slug, _meta.GameVersion, loader, ct).ConfigureAwait(true);
            var zip = await MrpackInstaller.FetchAsync(pv, ProgressSink(), ct).ConfigureAwait(true);
            var idx = MrpackInstaller.ReadIndex(zip);

            // 版本/加载器一致性校验：不一致就装出一个起不来的服，必须拦下并说清差异
            if (idx.McVersion is not null && idx.McVersion != _meta.GameVersion)
            {
                throw CeobeException.Of("本实例是 MC {0}，整合包要求 MC {1}", _meta.GameVersion, idx.McVersion);
            }
            if (idx.Loader is not null && idx.Loader != loader)
            {
                throw CeobeException.Of("本实例加载器是 {0}，整合包要求 {1}", loader, idx.Loader);
            }

            var r = await MrpackInstaller.InstallAsync(_dir, zip, ProgressSink(), ct).ConfigureAwait(true);
            Progress = 1;
            StatusText = $"已安装「{r.Name}」：{r.Files} 个文件，跳过 {r.Skipped}，覆盖 {r.Overrides}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            Progress = 0;
            StatusText = "安装失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>新建实例并安装。实例的 MC 版本/加载器由整合包索引决定，用户不必自己选。</summary>
    [RelayCommand]
    private async Task CreateAndInstallAsync(CancellationToken ct)
    {
        var hit = Selected;
        if (hit is null)
        {
            HasError = true;
            StatusText = "请先选择一个整合包";
            return;
        }

        IsBusy = true;
        HasError = false;
        Progress = -1;
        try
        {
            // 新建路径不限版本/加载器（要建什么由整合包说了算）
            var pv = await _client.ResolvePack(hit.Slug, null, null, ct).ConfigureAwait(true);
            var zip = await MrpackInstaller.FetchAsync(pv, ProgressSink(), ct).ConfigureAwait(true);
            var idx = MrpackInstaller.ReadIndex(zip);

            // 没有 mcVersion 就不知道建什么版本的实例，必须拦下
            if (idx.McVersion is null) throw new CeobeException("整合包未声明 MC 版本，无法自动新建实例");
            var type = ServerTypeInfo.FromLoaderId(idx.Loader);   // 不支持的 loader 会抛业务异常

            var name = string.IsNullOrWhiteSpace(NewName) ? hit.Slug : NewName.Trim();
            var spec = new InstanceSpec(name, type, idx.McVersion, idx.LoaderVersion,
                NewMemoryMb, "", _manager.SuggestFreePort(25565), null);

            await _manager.CreateAsync(spec, ProgressSink(), ct).ConfigureAwait(true);
            var r = await MrpackInstaller.InstallAsync(_manager.Dir(name), zip, ProgressSink(), ct)
                .ConfigureAwait(true);

            Progress = 1;
            StatusText = $"已新建实例 {name} 并安装「{r.Name}」：{r.Files} 个文件";
            CreatedInstance = name;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            Progress = 0;
            StatusText = "新建失败: " + e.Message;
        }
        catch (CeobeSpec e)
        {
            // 实例名非法等用法错误（与系统故障区分开）
            HasError = true;
            Progress = 0;
            StatusText = "新建失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>新建成功的实例名（界面据此刷新侧栏并选中它）。</summary>
    public string? CreatedInstance { get; private set; }

    private IProgressSink ProgressSink() => new VmProgress(this);

    /// <summary>进度按阶段节流；同时把字节进度折算成 0..1 供进度条。</summary>
    private sealed class VmProgress(ModpacksViewModel vm) : IProgressSink
    {
        private string? _last;
        public void Update(string stage, long done, long total)
        {
            if (total > 0) vm.Progress = Math.Clamp(done / (double)total, 0, 1);
            if (stage == _last) return;
            _last = stage;
            vm.StatusText = stage;
        }
    }
}

/// <summary>整合包搜索结果行。</summary>
public sealed record ModpackRow(
    string Slug, string Title, string Author, long Downloads, string Categories, string ProjectId)
{
    public string DownloadsLabel => Downloads switch
    {
        >= 1_000_000 => (Downloads / 1_000_000d).ToString("0.#") + "M",
        >= 1_000 => (Downloads / 1_000d).ToString("0.#") + "K",
        _ => Downloads.ToString(),
    };
}
