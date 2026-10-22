using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ceobe.Core.Client;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「Mod」分区（设计稿 03）。依赖健康是列级信息，不是详情里的隐藏项：
/// 它的用途是「扫一眼就知道哪个会拖垮启动」，藏起来等于没做。
///
/// 更新检查（Modrinth 版本比对）暂未接入：它需要为每个 jar 反查 project id 与 version id，
/// 属于独立工作量；先保证「装了什么、缺什么、开没开」这三件事准确。
/// </summary>
public partial class ClientModsViewModel : ObservableObject
{
    private readonly ClientMeta _meta;
    private List<ClientModRow> _all = [];

    public ObservableCollection<ClientModRow> Mods { get; } = [];

    [ObservableProperty]
    private ClientModRow? _selected;

    [ObservableProperty]
    private string _filter = "全部";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _allCount;

    [ObservableProperty]
    private int _missingCount;

    [ObservableProperty]
    private int _disabledCount;

    [ObservableProperty]
    private bool _hasHealthBanner;

    [ObservableProperty]
    private string _healthDetail = "";

    [ObservableProperty]
    private string _emptyHint = "";

    public bool SupportsMods => _meta.Loader != ClientLoader.VANILLA;

    public ClientModsViewModel(ClientMeta meta) => _meta = meta;

    public void Reload()
    {
        _all = ClientModScanner.Scan(_meta.Name)
            .Select(m => new ClientModRow(m))
            .ToList();

        AllCount = _all.Count;
        MissingCount = _all.Count(m => m.Missing.Count > 0 && m.Enabled);
        DisabledCount = _all.Count(m => !m.Enabled);

        var broken = _all.Where(m => m.Missing.Count > 0 && m.Enabled).ToList();
        HasHealthBanner = broken.Count > 0;
        HealthDetail = broken.Count == 0
            ? ""
            : $"{broken[0].Title} 缺少前置 {string.Join("、", broken[0].Missing)}"
              + (broken.Count > 1 ? $"；另有 {broken.Count - 1} 个 Mod 也有缺失" : "");

        ApplyFilter();
        EmptyHint = _all.Count == 0 ? "把 .jar 拖到本页即可安装" : "";
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        IEnumerable<ClientModRow> rows = _all;
        rows = Filter switch
        {
            "有缺失" => rows.Where(r => r.Missing.Count > 0 && r.Enabled),
            "已停用" => rows.Where(r => !r.Enabled),
            _ => rows,
        };
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            rows = rows.Where(r =>
                r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.FileName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || (r.Id ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Mods.Clear();
        foreach (var r in rows) Mods.Add(r);
        if (Selected is not null && !Mods.Contains(Selected)) Selected = Mods.FirstOrDefault();
    }

    /// <summary>启用/停用（改名 .jar ↔ .jar.disabled）。失败就地提示，不弹窗。</summary>
    public string? Toggle(ClientModRow? row)
    {
        if (row is null) return null;
        try
        {
            ClientModScanner.SetEnabled(_meta.Name, row.FileName, !row.Enabled);
            Reload();
            return null;
        }
        catch (CeobeException e)
        {
            return e.Message;
        }
    }

    public string? RemoveMod(ClientModRow? row)
    {
        if (row is null) return null;
        try
        {
            ClientModScanner.Remove(_meta.Name, row.FileName, row.Enabled);
            Reload();
            return null;
        }
        catch (CeobeException e)
        {
            return e.Message;
        }
    }

    /// <summary>导入本地 jar（拖放或手填路径都走这里）。</summary>
    public string? Import(string path)
    {
        try
        {
            ClientModScanner.Import(_meta.Name, path);
            Reload();
            return null;
        }
        catch (CeobeException e)
        {
            return e.Message;
        }
    }
}

/// <summary>Mod 表格的一行 + 详情面板所需的全部字段。</summary>
public sealed class ClientModRow
{
    public ClientModRow(ModEntry entry)
    {
        FileName = entry.FileName;
        Id = entry.ModId;
        Title = entry.DisplayName ?? entry.FileName;
        Version = entry.Version ?? "—";
        Enabled = entry.Enabled;
        Health = entry.Health;
        Missing = entry.Missing;
        Satisfied = entry.Satisfied;
        SizeLabel = TextFormat.HumanSize(entry.SizeBytes);
        HasMissing = entry.Missing.Count > 0 && entry.Enabled;
    }

    public string FileName { get; }
    public string? Id { get; }
    public string Title { get; }
    public string Version { get; }
    public bool Enabled { get; }
    public string Health { get; }
    public IReadOnlyList<string> Missing { get; }
    public IReadOnlyList<string> Satisfied { get; }
    public string SizeLabel { get; }
    public bool HasMissing { get; }

    public string StateLabel => Enabled ? "已启用" : "已停用";

    public string ToggleLabel => Enabled ? "停用" : "启用";

    /// <summary>状态点的着色语义：缺失=红、停用=中性、正常=绿。界面用同一个字段双编码（色 + 文字）。</summary>
    public string HealthKey => HasMissing ? "error" : Enabled ? "ok" : "muted";

    public string DependencyDetail
    {
        get
        {
            if (Missing.Count > 0) return "缺少前置：" + string.Join("、", Missing);
            if (Satisfied.Count > 0) return "已满足的前置：" + string.Join("、", Satisfied);
            return "没有声明额外前置";
        }
    }
}
