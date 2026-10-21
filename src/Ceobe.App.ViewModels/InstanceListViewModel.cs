using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 侧栏实例列表的 ViewModel（Java 版 <c>MainView</c> 的侧栏部分）。
///
/// 刻意不引用任何 WinUI 类型：闸门 C（docs/MIGRATION.md「UI 回归」）把 UI 回归重心放在这一层，
/// 因为它能在无窗口、无 UI 线程的环境下单测。WinUI 3 没有 Java 版 <c>-Dceobe.snapshot</c>
/// 那种「启动即导出 Scene 快照」的廉价手段，像素级回归成本高且不可靠，故逻辑一律下沉到此。
/// </summary>
public partial class InstanceListViewModel : ObservableObject
{
    private readonly InstanceManager _manager;

    /// <summary>列表行（已投影成界面直接可绑定的形态，避免 XAML 里做多字段拼接）。</summary>
    public ObservableCollection<InstanceRow> Rows { get; } = [];

    /// <summary>状态栏文案（Java 版 statusBar.setText）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>当前选中的实例名；null = 未选中。</summary>
    [ObservableProperty]
    private string? _selectedName;

    /// <summary>最近一次刷新是否失败（失败原因见 <see cref="StatusText"/>）。</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>是否只显示运行中的实例（Java 版 ps 的 -a 取反）。</summary>
    [ObservableProperty]
    private bool _onlyRunning;

    public InstanceListViewModel(InstanceManager manager)
    {
        _manager = manager;
        // 订阅 Core 事件：实例状态变化（启动/停止/崩溃）时自动刷新对应行，
        // 对齐 Java 版 addListener → syncLive 的语义
        _manager.OnEvent += OnInstanceEvent;
    }

    /// <summary>选中项变化时通知界面切换页签内容。</summary>
    public event Action<string?>? SelectionChanged;

    partial void OnSelectedNameChanged(string? value) => SelectionChanged?.Invoke(value);

    partial void OnOnlyRunningChanged(bool value) => Refresh();

    private void OnInstanceEvent(IInstanceEvent evt)
    {
        // 事件来自日志泵/看门狗线程，不能直接碰 ObservableCollection（UI 线程亲和）。
        // 这里只更新行的值对象（线程安全的属性写），由界面侧的 DispatcherQueue 决定是否重建列表。
        if (evt is StateChanged sc)
        {
            var row = Rows.FirstOrDefault(r => r.Name == sc.Instance);
            if (row is not null)
            {
                row.State = sc.State;
                row.StateLabel = sc.State.ZhLabel();
            }
        }
    }

    /// <summary>重新扫描实例目录（Core 无中央索引，列表由扫描 instances/* 得出，ADR-005）。</summary>
    public void Refresh()
    {
        try
        {
            var statuses = _manager.List();
            if (OnlyRunning) statuses = [.. statuses.Where(s => s.State.IsLive())];

            Rows.Clear();
            foreach (var s in statuses)
            {
                Rows.Add(InstanceRow.From(s));
            }
            HasError = false;
            StatusText = BuildStatusText(statuses);
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "读取实例列表失败: " + e.Message;
        }
    }

    /// <summary>状态栏文案：运行中数量 / 总数。</summary>
    private static string BuildStatusText(List<InstanceStatus> statuses)
    {
        if (statuses.Count == 0) return "暂无实例";
        var live = statuses.Count(s => s.State.IsLive());
        return live == 0
            ? $"{statuses.Count} 个实例（均未运行）"
            : $"{live} / {statuses.Count} 个实例运行中";
    }

    [RelayCommand]
    private void Remove(string name)
    {
        // 只移除注册，不动世界数据（--purge 是另一个显式动作，界面走确认对话框）
        _manager.Remove(name, purge: false);
        Refresh();
    }

    /// <summary>供界面在关闭时取消订阅，避免 Core 事件持有已释放的 ViewModel。</summary>
    public void Detach() => _manager.OnEvent -= OnInstanceEvent;
}

/// <summary>
/// 列表单行的可绑定投影。
/// 用 <see cref="ObservableObject"/> 而非 record：状态变化时要能就地更新而不重建整行
/// （重建会让 ListView 丢失选中态与滚动位置）。
/// </summary>
public partial class InstanceRow : ObservableObject
{
    /// <summary>
    /// 用构造器而非 <c>required</c>：WinUI 的 x:Bind 会为 <c>x:DataType</c> 生成类型元数据，
    /// 其中需要能实例化该类型；<c>required</c> 成员会触发 CS9035（生成代码里的对象初始化器
    /// 未设必填成员）。构造器注入同样保证 Name 非空，且不阻碍元数据生成。
    /// </summary>
    public InstanceRow(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private InstanceState _state;

    [ObservableProperty]
    private string _stateLabel = "";

    [ObservableProperty]
    private string _detail = "";

    /// <summary>运行中才显示 PID（对齐 Java 版 ps 的 isLive 判断）。</summary>
    public bool IsLive => State.IsLive();

    partial void OnStateChanged(InstanceState value) => OnPropertyChanged(nameof(IsLive));

    public static InstanceRow From(InstanceStatus s) => new(s.Meta.Name)
    {
        DisplayName = s.Meta.Name,
        State = s.State,
        StateLabel = s.State.ZhLabel(),
        // 侧栏第二行：类型 + 版本 + 端口，一眼能认出是哪个服
        Detail = $"{s.Meta.ServerType} {s.Meta.GameVersion} · :{s.Meta.Port}",
    };
}
