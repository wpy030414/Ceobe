using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 侧栏实例列表的 ViewModel（原 Java 版 <c>MainView</c> 的侧栏部分）。
///
/// 刻意不引用任何 WinUI 类型：闸门 C（docs/MIGRATION.md「UI 回归」）把 UI 回归重心放在这一层，
/// 因为它能在无窗口、无 UI 线程的环境下单测。WinUI 3 没有 Java 版 <c>-Dceobe.snapshot</c>
/// 那种「启动即导出 Scene 快照」的廉价手段，像素级回归成本高且不可靠，故逻辑一律下沉到此。
///
/// 2026-10-22 重构补充（WinUI Fluent 化）：
/// · <see cref="Refresh"/> 改为**差量更新**：保留行对象与选中实例，避免 NavigationView/ListView
///   在每次扫描后丢选中（旧实现 <c>Rows.Clear()</c> 会让 SelectedValue 两向绑定回写 null）；
/// · Core 事件先经 <see cref="UiThread.Post"/> marshal，再改可观察属性/集合——日志与状态事件
///   来自监督进程线程，直接改 ObservableCollection 会撞 WinUI 的跨线程绑定；
/// · 状态变化就地更新行与状态栏，不再依赖主窗口对每条事件做全量 Refresh。
/// </summary>
public partial class InstanceListViewModel : ObservableObject
{
    private readonly InstanceManager _manager;

    /// <summary>列表行（已投影成界面直接可绑定的形态，避免 XAML 里做多字段拼接）。</summary>
    public ObservableCollection<InstanceRow> Rows { get; } = [];

    /// <summary>状态栏文案（原 Java 版 statusBar.setText）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>当前选中的实例名；null = 未选中。</summary>
    [ObservableProperty]
    private string? _selectedName;

    /// <summary>最近一次刷新是否失败（失败原因见 <see cref="StatusText"/>）。</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>是否只显示运行中的实例（旧版 ps 的 -a 取反）。</summary>
    [ObservableProperty]
    private bool _onlyRunning;

    public InstanceListViewModel(InstanceManager manager)
    {
        _manager = manager;
        // 订阅 Core 事件：实例状态变化（启动/停止/崩溃）时就地刷新对应行，
        // 对齐旧版 addListener → syncLive 的语义。事件来自后台线程，HandleEvent 内部会 marshal。
        _manager.OnEvent += OnInstanceEvent;
    }

    /// <summary>
    /// NavigationView.SelectedItem 的绑定目标。它是 <see cref="SelectedName"/> 的投影：
    /// 控件要求拿到行对象，VM 只保存名字（名字是跨进程稳定标识，行对象会随刷新重建）。
    /// </summary>
    public InstanceRow? SelectedRow
    {
        get => Rows.FirstOrDefault(r => r.Name == SelectedName);
        set => SelectedName = value?.Name;
    }

    /// <summary>选中项变化时通知界面切换工作区。</summary>
    public event Action<string?>? SelectionChanged;

    partial void OnSelectedNameChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedRow));
        SelectionChanged?.Invoke(value);
    }

    partial void OnOnlyRunningChanged(bool value) => Refresh();

    private void OnInstanceEvent(IInstanceEvent evt) => UiThread.Post(() => HandleEvent(evt));

    private void HandleEvent(IInstanceEvent evt)
    {
        if (evt is not StateChanged sc) return;

        var row = Rows.FirstOrDefault(r => r.Name == sc.Instance);
        if (row is null)
        {
            // 只看运行中时，一个本来不在列表里的实例启动了：需要重新扫描才能把它加进来。
            // 状态变化很稀疏（不是每条日志），这次全量扫描可以接受。
            if (OnlyRunning && sc.State.IsLive()) Refresh();
            return;
        }

        row.State = sc.State;
        row.StateLabel = sc.State.ZhLabel();

        if (OnlyRunning && !row.IsLive)
        {
            var wasSelected = string.Equals(SelectedName, row.Name, StringComparison.Ordinal);
            Rows.Remove(row);
            if (wasSelected) SelectedName = null;
        }

        UpdateStatusText();
    }

    /// <summary>
    /// 重新扫描实例目录（Core 无中央索引，列表由扫描 instances/* 得出，ADR-005）。
    /// 差量更新而不是 Clear：行对象与选中实例都要活过刷新，否则界面会跳回空状态。
    /// </summary>
    public void Refresh()
    {
        try
        {
            var statuses = _manager.List();
            if (OnlyRunning) statuses = [.. statuses.Where(s => s.State.IsLive())];

            var wanted = statuses.Select(s => s.Meta.Name).ToHashSet(StringComparer.Ordinal);

            // 1) 删除已经消失的行（从后往前，避免索引漂移）
            for (var i = Rows.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(Rows[i].Name)) Rows.RemoveAt(i);
            }

            // 2) 按扫描顺序更新已有行、插入新行，并保证顺序一致
            for (var i = 0; i < statuses.Count; i++)
            {
                var status = statuses[i];
                var row = Rows.FirstOrDefault(r => r.Name == status.Meta.Name);
                if (row is null)
                {
                    Rows.Insert(i, InstanceRow.From(status));
                    continue;
                }

                row.Apply(status);
                var current = Rows.IndexOf(row);
                if (current != i) Rows.Move(current, i);
            }

            HasError = false;
            UpdateStatusText();

            // 选中的实例如果被过滤/删除，明确清空；否则通知 SelectedRow —— NavigationView
            // 的 SelectedItem 是 TwoWay 绑定，必须在行集合变化后重新求值。
            if (SelectedName is not null && !Rows.Any(r => r.Name == SelectedName))
            {
                SelectedName = null;
            }
            else
            {
                OnPropertyChanged(nameof(SelectedRow));
            }
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "读取实例列表失败: " + e.Message;
        }
    }

    /// <summary>状态栏文案：运行中数量 / 总数。就地更新，不触发目录扫描。</summary>
    private void UpdateStatusText()
    {
        if (Rows.Count == 0)
        {
            StatusText = "暂无实例";
            return;
        }

        var live = Rows.Count(r => r.IsLive);
        StatusText = live == 0
            ? $"{Rows.Count} 个实例（均未运行）"
            : $"{live} / {Rows.Count} 个实例运行中";
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
/// （重建会让 ListView/NavigationView 丢失选中态与滚动位置）。
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

    /// <summary>运行中才显示 PID（对齐旧版 ps 的 isLive 判断）。</summary>
    public bool IsLive => State.IsLive();

    /// <summary>侧栏第二行：类型 + 版本 + 端口 · 状态文字。状态不能只靠颜色传达。</summary>
    public string DetailLine => string.IsNullOrEmpty(StateLabel) ? Detail : $"{Detail} · {StateLabel}";

    partial void OnStateChanged(InstanceState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(DetailLine));
    }

    partial void OnStateLabelChanged(string value) => OnPropertyChanged(nameof(DetailLine));

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(DetailLine));

    public static InstanceRow From(InstanceStatus s) => new(s.Meta.Name)
    {
        DisplayName = s.Meta.Name,
        State = s.State,
        StateLabel = s.State.ZhLabel(),
        // 侧栏第二行：类型 + 版本 + 端口，一眼能认出是哪个服
        Detail = $"{s.Meta.ServerType} {s.Meta.GameVersion} · :{s.Meta.Port}",
    };

    /// <summary>把扫描到的最新状态写回现有行（保留对象身份与选中态）。</summary>
    public void Apply(InstanceStatus s)
    {
        DisplayName = s.Meta.Name;
        State = s.State;
        StateLabel = s.State.ZhLabel();
        Detail = $"{s.Meta.ServerType} {s.Meta.GameVersion} · :{s.Meta.Port}";
    }

    /// <summary>UIA 在没有显式 AutomationProperties.Name 时会退回 ToString，别让读屏器读出类型名。</summary>
    public override string ToString() => DisplayName;
}
