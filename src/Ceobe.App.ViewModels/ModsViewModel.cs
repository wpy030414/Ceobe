using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Model;
using Ceobe.Core.Mods;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「Mod」页签 ViewModel（Java 版 <c>ModPane</c>）：Modrinth 搜索安装 + 已装列表卸载。
///
/// 两条产品原则：
/// 1. **按实例的服务端类型与游戏版本过滤**——原版/Paper 没有 mods 目录，界面要直接说明
///    而不是让用户点了安装才报错；搜索也要带上 gameVersion + loader，否则装进去也跑不起来。
/// 2. **mod 清单写回走 Core 的 AppendMods / RemoveMods**，不自己拼 instance.json——
///    CLI 与 GUI 必须共用一套写回逻辑，否则两处会漂移（CLI 装的 mod 在 GUI 列表里少一条）。
/// </summary>
public partial class ModsViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly ModrinthClient _client = new();
    private readonly string _instance;
    private readonly string _dir;
    private readonly InstanceMeta _meta;

    /// <summary>搜索结果。</summary>
    public ObservableCollection<ModSearchRow> Results { get; } = [];

    /// <summary>已安装列表。</summary>
    public ObservableCollection<ModInstalledRow> Installed { get; } = [];

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ModSearchRow? _selectedResult;

    [ObservableProperty]
    private ModInstalledRow? _selectedInstalled;

    public ModsViewModel(InstanceManager manager, string instance)
    {
        _manager = manager;
        _instance = instance;
        _dir = manager.Dir(instance);
        _meta = manager.Meta(instance);
    }

    /// <summary>该服务端类型是否有 mods 目录（原版/Paper 没有）。</summary>
    public bool SupportsMods => _meta.ServerType.HasModsDir();

    /// <summary>不支持模组时的说明（界面直接展示，不让用户点了才报错）。</summary>
    public string UnsupportedHint =>
        _meta.ServerType.ZhLabel() + " 没有 mods 目录，不支持安装模组。\n" +
        "如需模组，请创建 Fabric 或 NeoForge 类型的实例。";

    /// <summary>搜索框占位文案：告诉用户会按什么过滤。</summary>
    public string SearchHint =>
        $"搜索模组（将按 {_meta.GameVersion} + {_meta.ServerType.LoaderId() ?? "无"} 过滤）";

    public bool CanInstall => SelectedResult is not null && !IsBusy;
    public bool CanUninstall => SelectedInstalled is not null && !IsBusy;

    partial void OnSelectedResultChanged(ModSearchRow? value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnSelectedInstalledChanged(ModInstalledRow? value) => OnPropertyChanged(nameof(CanUninstall));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
    }

    /// <summary>刷新已安装列表（对照磁盘，标出「缺失」的记录）。</summary>
    public void RefreshInstalled()
    {
        Installed.Clear();
        // 重新读 meta：安装/卸载后清单会变，不能持有旧快照
        var meta = _manager.Meta(_instance);
        var modsDir = Path.Combine(_dir, "mods");
        foreach (var m in meta.Mods)
        {
            var present = File.Exists(Path.Combine(modsDir, m.FileName));
            Installed.Add(new ModInstalledRow(
                m.FileName, m.Title, m.VersionNumber, EnvLabel(m.EnvServer),
                present ? "存在" : "缺失", m.ProjectId));
        }
    }

    /// <summary>env.server 的人话标签（界面不该直接显示 required/optional 原文）。</summary>
    private static string EnvLabel(string env) => env switch
    {
        "required" => "必需",
        "optional" => "可选",
        "unsupported" => "不支持服务端",
        "" => "未知",
        _ => env,
    };

    [RelayCommand]
    private async Task SearchAsync(CancellationToken ct)
    {
        if (!SupportsMods)
        {
            HasError = true;
            StatusText = UnsupportedHint;
            return;
        }
        IsBusy = true;
        HasError = false;
        StatusText = "搜索中…";
        try
        {
            var hits = await _client
                .Search(Query.Trim(), _meta.GameVersion, _meta.ServerType, 30, ct)
                .ConfigureAwait(true);
            Results.Clear();
            foreach (var h in hits)
            {
                Results.Add(new ModSearchRow(
                    h.Slug, h.Title, h.Author, h.Downloads,
                    string.Join(",", h.Categories.Take(3)), h.ProjectId));
            }
            StatusText = $"找到 {hits.Count} 个结果";
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
        }
    }

    [RelayCommand]
    private async Task InstallAsync(CancellationToken ct)
    {
        var row = SelectedResult;
        if (row is null) return;
        IsBusy = true;
        HasError = false;
        StatusText = $"安装 {row.Slug} …";
        try
        {
            var entry = await _client
                .Install(_dir, _meta.ServerType, row.Slug, _meta.GameVersion,
                    new VmProgress(this), ct)
                .ConfigureAwait(true);
            // 写回走 Core：与 CLI 共用，同名文件替换旧记录
            _manager.AppendMods(_instance, [entry]);
            RefreshInstalled();
            StatusText = $"已安装 {entry.Title} → mods/{entry.FileName}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "安装失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(CancellationToken ct)
    {
        var row = SelectedInstalled;
        if (row is null) return;
        IsBusy = true;
        HasError = false;
        StatusText = $"卸载 {row.FileName} …";
        try
        {
            // UninstallAsync 需要完整 ModEntry（含 ProjectId 等），从 meta 取而不是只靠行数据
            var entry = _manager.Meta(_instance).Mods.FirstOrDefault(m => m.FileName == row.FileName)
                        ?? throw CeobeException.Of("元数据中找不到 {0}", row.FileName);
            await _client.UninstallAsync(_dir, entry, ct).ConfigureAwait(true);
            _manager.RemoveMods(_instance, m => m.FileName == row.FileName);
            RefreshInstalled();
            StatusText = $"已卸载 {row.Title}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "卸载失败: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>下载进度按阶段节流，避免字节级进度刷爆 UI 线程。</summary>
    private sealed class VmProgress(ModsViewModel vm) : IProgressSink
    {
        private string? _last;
        public void Update(string stage, long done, long total)
        {
            if (stage == _last) return;
            _last = stage;
            vm.StatusText = stage;
        }
    }
}

/// <summary>搜索结果行。</summary>
public sealed record ModSearchRow(
    string Slug, string Title, string Author, long Downloads, string Categories, string ProjectId)
{
    /// <summary>下载量的人类可读形态（界面用）。</summary>
    public string DownloadsLabel => Downloads switch
    {
        >= 1_000_000 => (Downloads / 1_000_000d).ToString("0.#") + "M",
        >= 1_000 => (Downloads / 1_000d).ToString("0.#") + "K",
        _ => Downloads.ToString(),
    };
}

/// <summary>已安装行。<paramref name="Present"/> 为「存在/缺失」，对照磁盘标出被手动删掉的记录。</summary>
public sealed record ModInstalledRow(
    string FileName, string Title, string Version, string EnvLabel, string Present, string ProjectId);
