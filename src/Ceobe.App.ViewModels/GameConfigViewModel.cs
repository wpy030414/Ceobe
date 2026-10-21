using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Config;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>「游戏配置」页的三级视图（整改项 5）。</summary>
public enum ConfigView
{
    /// <summary>常用：≤12 项，够开服即用。</summary>
    Common,

    /// <summary>分类：左侧分组导航 + 搜索。</summary>
    Category,

    /// <summary>原始：全量 key-value（含未收录键），给高级用户。</summary>
    Raw,
}

/// <summary>
/// 「游戏配置」页 ViewModel（Java 版 <c>GamePane</c> + <c>PropsPaneBase</c>）。
///
/// 整改项 5 的落地：Java 版把 7 组 60+ 键**一次性平铺**，训练员反馈「稍有杂乱且繁多」。
/// 这里改成三级视图（常用 / 分类+搜索 / 原始），并把「改完要不要重启」作为一等公民
/// （<see cref="PropField.PolicyLabel"/>），因为「改了没反应」是配置界面最伤信任的体验。
///
/// 保存语义**逐条照搬 Java 版**，这条不能改：保存时重新加载文件、只写入本面板记录的键，
/// 避免「服务器配置」与「游戏配置」两个页签互相覆盖对方的改动。
/// </summary>
public partial class GameConfigViewModel : ObservableObject
{
    private readonly string _propsFile;
    private readonly InstanceManager _manager;
    private readonly string _instanceName;

    /// <summary>待保存的改动（key → 新值），等价 Java 版 PropsPaneBase.pending。</summary>
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>当前视图下要显示的字段（由 <see cref="Rebuild"/> 按视图/搜索/分组过滤）。</summary>
    public ObservableCollection<PropField> Fields { get; } = [];

    /// <summary>分类视图的左侧导航项。</summary>
    public ObservableCollection<GroupNav> GroupNavs { get; } = [];

    /// <summary>
    /// 资源包复合控件（整改项 5）：4 个 <c>resource-pack*</c> 键在 Java 版是四个孤立文本框，
    /// 人类根本读不懂它们的关系。这里聚成一个语义单元交给 XAML 渲染。
    /// </summary>
    public ObservableCollection<PropField> ResourcePackFields { get; } = [];

    /// <summary>
    /// 数据包多选（整改项 5）：<c>initial-enabled-packs</c> / <c>initial-disabled-packs</c>
    /// 是逗号分隔字符串，Java 版直接丢一个文本框。这里解析成可增删的列表。
    /// </summary>
    public ObservableCollection<string> EnabledPacks { get; } = [];
    public ObservableCollection<string> DisabledPacks { get; } = [];

    [ObservableProperty]
    private ConfigView _view = ConfigView.Common;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string? _selectedGroup;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _canSave;

    /// <summary>保存或校验出错（文案见 <see cref="StatusText"/>）。</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>实例是否在运行——运行中保存要提示「重启后生效」。</summary>
    [ObservableProperty]
    private bool _serverRunning;

    /// <summary>server.properties 是否存在（首次启动前没有，界面要给引导而不是空白页）。</summary>
    [ObservableProperty]
    private bool _propsExist;

    /// <summary>未收录键（旧版本/插件写入）数量，供「原始」视图提示。</summary>
    [ObservableProperty]
    private int _unknownCount;

    /// <summary>全部字段（含未收录），按 schema 顺序 + 未收录键。</summary>
    private readonly List<PropField> _all = [];

    public GameConfigViewModel(InstanceManager manager, string instanceName)
    {
        _manager = manager;
        _instanceName = instanceName;
        _propsFile = Path.Combine(manager.Dir(instanceName), "server.properties");
    }

    /// <summary>待保存项数量。</summary>
    public int PendingCount => _pending.Count;

    partial void OnViewChanged(ConfigView value) => Rebuild();

    partial void OnSearchQueryChanged(string value) => Rebuild();

    partial void OnSelectedGroupChanged(string? value) => Rebuild();

    /// <summary>从磁盘加载 server.properties 并投影成字段。</summary>
    public void Load()
    {
        _all.Clear();
        Fields.Clear();
        GroupNavs.Clear();
        ResourcePackFields.Clear();
        EnabledPacks.Clear();
        DisabledPacks.Clear();
        _pending.Clear();
        CanSave = false;
        HasError = false;

        PropsExist = File.Exists(_propsFile);
        if (!PropsExist)
        {
            StatusText = "尚无 server.properties（先启动一次服务器后即出现）";
            ServerRunning = _manager.Find(_instanceName)?.State.IsLive() ?? false;
            Rebuild();
            return;
        }

        ServerPropertiesFile props;
        try
        {
            props = ServerPropertiesFile.Load(_propsFile);
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "读取配置失败: " + e.Message;
            return;
        }

        ServerRunning = _manager.Find(_instanceName)?.State.IsLive() ?? false;

        // ① schema 收录的键：只显示文件里真实存在的（对齐 Java 版 groupCard 的 contains 过滤），
        //    这样老配置文件不会凭空多出一堆它本来没有的键。
        foreach (var key in PropSchema.All())
        {
            if (key.Legacy) continue;
            if (!props.Contains(key.Key)) continue;
            _all.Add(PropField.FromSchema(key, props.Get(key.Key) ?? key.Def, OnFieldChanged));
        }

        // ② 未收录键（旧版本 / 插件写入）：只读，但必须让用户看见，不能静默丢弃
        foreach (var (k, v) in props.Values)
        {
            if (PropSchema.Key(k) is not null) continue;
            _all.Add(PropField.Unknown(k, v, OnFieldChanged));
        }
        UnknownCount = _all.Count(f => f.IsUnknown);

        // 分组导航（含每组可见字段数，让用户知道点进去有多少东西）
        foreach (var g in PropSchema.Groups())
        {
            var count = _all.Count(f => !f.IsUnknown && f.Group == g);
            if (count > 0) GroupNavs.Add(new GroupNav(g, count));
        }
        SelectedGroup ??= GroupNavs.FirstOrDefault()?.Name;

        // 资源包复合控件（整改项 5）
        foreach (var f in _all.Where(f => ResourcePackCluster.Contains(f.Key)))
        {
            ResourcePackFields.Add(f);
        }

        // 数据包多选（整改项 5）：解析逗号分隔串
        foreach (var p in SplitPacks(_all.FirstOrDefault(f => f.Key == "initial-enabled-packs")?.Value))
        {
            EnabledPacks.Add(p);
        }
        foreach (var p in SplitPacks(_all.FirstOrDefault(f => f.Key == "initial-disabled-packs")?.Value))
        {
            DisabledPacks.Add(p);
        }

        StatusText = ServerRunning ? "实例运行中：改动保存后需重启服务端才生效" : "";
        Rebuild();
    }

    /// <summary>资源包复合控件包含的 4 个键（整改项 5）。</summary>
    private static readonly HashSet<string> ResourcePackCluster = new(StringComparer.Ordinal)
    {
        "resource-pack", "resource-pack-id", "resource-pack-prompt", "resource-pack-sha1",
    };

    private static IEnumerable<string> SplitPacks(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var p in raw.Split(','))
        {
            var t = p.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    /// <summary>字段值变化：校验通过才记入 pending（校验失败的文案由字段自己显示）。</summary>
    private void OnFieldChanged(PropField field, string value)
    {
        if (field.IsUnknown) return; // 未收录键只读，不该有改动
        _pending[field.Key] = value;
        CanSave = true;
        HasError = false;
        StatusText = $"有 {_pending.Count} 项未保存";
    }

    /// <summary>按当前视图 / 搜索词 / 选中分组重建 <see cref="Fields"/>。</summary>
    private void Rebuild()
    {
        Fields.Clear();
        if (!PropsExist) return;

        var query = SearchQuery.Trim();

        // 搜索优先于视图：在任意视图下输入搜索词都进入「全库匹配」模式
        if (query.Length > 0)
        {
            foreach (var f in _all.Where(f => f.Matches(query))) Fields.Add(f);
            return;
        }

        switch (View)
        {
            case ConfigView.Common:
                // 常用视图只收 schema 标记的键，且必须在文件里真实存在
                foreach (var k in PropSchema.Common())
                {
                    var f = _all.FirstOrDefault(x => x.Key == k.Key && !x.IsUnknown);
                    if (f is not null) Fields.Add(f);
                }
                break;

            case ConfigView.Category:
                foreach (var f in _all.Where(f => !f.IsUnknown && f.Group == SelectedGroup))
                {
                    Fields.Add(f);
                }
                break;

            case ConfigView.Raw:
                foreach (var f in _all) Fields.Add(f);
                break;
        }
    }

    /// <summary>
    /// 保存：重新加载文件、只写本面板记录的键、原子落盘。
    /// **这个语义不能改**——两个页签（服务器配置 / 游戏配置）共用同一份 server.properties，
    /// 若不重新加载就直接写，后保存的一方会把先保存一方的改动整体覆盖掉。
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_pending.Count == 0) return;
        var snapshot = new Dictionary<string, string>(_pending, StringComparer.Ordinal);
        try
        {
            var count = await Task.Run(() =>
            {
                var f = ServerPropertiesFile.Load(_propsFile);
                foreach (var (k, v) in snapshot) f.Set(k, v);
                f.Save();
                return snapshot.Count;
            }).ConfigureAwait(true);

            _pending.Clear();
            CanSave = false;
            HasError = false;
            StatusText = "已保存 " + count + " 项" + (ServerRunning ? "（重启后生效）" : "");
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "保存失败: " + e.Message;
        }
    }

    /// <summary>丢弃未保存的改动（重新从磁盘加载）。</summary>
    [RelayCommand]
    private void Discard() => Load();

    /// <summary>
    /// 数据包多选写回：把 <see cref="EnabledPacks"/> / <see cref="DisabledPacks"/> 序列化成
    /// 逗号分隔串并记入 pending。Java 版没有这个能力（只有一个裸文本框）。
    /// </summary>
    [RelayCommand]
    private void ApplyPacks()
    {
        var enabled = _all.FirstOrDefault(f => f.Key == "initial-enabled-packs");
        var disabled = _all.FirstOrDefault(f => f.Key == "initial-disabled-packs");
        if (enabled is not null) enabled.Value = string.Join(",", EnabledPacks);
        if (disabled is not null) disabled.Value = string.Join(",", DisabledPacks);
    }

    /// <summary>把某个数据包从「启用」挪到「禁用」（或反向），并立刻写回 pending。</summary>
    [RelayCommand]
    private void TogglePack(string pack)
    {
        if (EnabledPacks.Remove(pack)) DisabledPacks.Add(pack);
        else if (DisabledPacks.Remove(pack)) EnabledPacks.Add(pack);
        ApplyPacks();
    }
}

/// <summary>分类视图左侧导航项。</summary>
/// <param name="Name">分组名（中文）</param>
/// <param name="Count">该组在当前文件里可见的字段数</param>
public sealed record GroupNav(string Name, int Count);
