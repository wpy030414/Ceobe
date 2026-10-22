using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Client;
using Ceobe.Core.Model;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 侧栏「客户端实例」列表。与服务端的 <see cref="InstanceListViewModel"/> 平行，
/// 但刻意更简单：客户端没有端口/穿透/外部托管这些概念，进程状态只在本进程内。
///
/// 仍然是零 WinUI 引用（闸门 C）：State → DotState 的映射在这里做，
/// 界面只负责把 <see cref="ClientRow.DotState"/> 交给状态点控件。
/// </summary>
public partial class ClientListViewModel : ObservableObject
{
    private readonly ClientRuntime _runtime;

    public ObservableCollection<ClientRow> Rows { get; } = [];

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _selectedName;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>选中项变化 → 主窗口切换右侧工作区。</summary>
    public event Action<string?>? SelectionChanged;

    public ClientListViewModel(ClientRuntime runtime)
    {
        _runtime = runtime;
        _runtime.OnEvent += OnClientEvent;
    }

    /// <summary>NavigationView.SelectedItem 的绑定目标（与实例列表同构）。</summary>
    public ClientRow? SelectedRow
    {
        get => Rows.FirstOrDefault(r => r.Name == SelectedName);
        set => SelectedName = value?.Name;
    }

    partial void OnSelectedNameChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedRow));
        SelectionChanged?.Invoke(value);
    }

    private void OnClientEvent(IClientEvent evt) => UiThread.Post(() => Handle(evt));

    private void Handle(IClientEvent evt)
    {
        if (evt is not ClientStateChanged sc) return;
        var row = Rows.FirstOrDefault(r => r.Name == sc.Instance);
        if (row is null) return;
        row.State = sc.State;
        row.StateLabel = sc.State.ZhLabel();
        UpdateStatusText();
    }

    /// <summary>差量刷新（保留行对象与选中态，避免 NavigationView 丢选中）。</summary>
    public void Refresh()
    {
        try
        {
            var metas = ClientStore.List();
            var wanted = metas.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

            for (var i = Rows.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(Rows[i].Name)) Rows.RemoveAt(i);
            }

            for (var i = 0; i < metas.Count; i++)
            {
                var meta = metas[i];
                var row = Rows.FirstOrDefault(r => r.Name == meta.Name);
                if (row is null)
                {
                    Rows.Insert(i, ClientRow.From(meta, _runtime.StateOf(meta.Name)));
                    continue;
                }
                row.Apply(meta, _runtime.StateOf(meta.Name));
                var current = Rows.IndexOf(row);
                if (current != i) Rows.Move(current, i);
            }

            HasError = false;
            UpdateStatusText();
            if (SelectedName is not null && !Rows.Any(r => r.Name == SelectedName)) SelectedName = null;
            else OnPropertyChanged(nameof(SelectedRow));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            HasError = true;
            StatusText = "读取客户端实例失败: " + e.Message;
        }
    }

    private void UpdateStatusText()
    {
        if (Rows.Count == 0)
        {
            StatusText = "暂无客户端实例";
            return;
        }
        var live = Rows.Count(r => r.IsLive);
        StatusText = live == 0 ? $"{Rows.Count} 个客户端实例" : $"{live} / {Rows.Count} 个游戏运行中";
    }

    [RelayCommand]
    private void Remove(string name)
    {
        ClientStore.Remove(name, purge: false);
        Refresh();
    }

    public void Detach() => _runtime.OnEvent -= OnClientEvent;
}

/// <summary>侧栏一行的可绑定投影。字段与 <see cref="InstanceRow"/> 保持同样的语义，
/// 这样 <c>NavListItem</c> 的模板心智不用重学。</summary>
public partial class ClientRow : ObservableObject, INavRow
{
    public ClientRow(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private ClientState _state;

    [ObservableProperty]
    private string _stateLabel = "";

    public bool IsLive => State.IsLive();

    /// <summary>
    /// 状态点控件目前以服务端的 <see cref="InstanceState"/> 为准（四态：运行/过渡/崩溃/空闲）。
    /// 客户端状态与它一一对应，直接映射比再造一个控件更划算。
    /// </summary>
    public InstanceState DotState => State switch
    {
        ClientState.RUNNING => InstanceState.RUNNING,
        ClientState.STARTING => InstanceState.STARTING,
        ClientState.STOPPING => InstanceState.STOPPING,
        ClientState.CRASHED => InstanceState.CRASHED,
        _ => InstanceState.STOPPED,
    };

    public string DetailLine => string.IsNullOrEmpty(StateLabel) ? Detail : $"{Detail} · {StateLabel}";

    partial void OnStateChanged(ClientState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(DotState));
        OnPropertyChanged(nameof(DetailLine));
    }

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(DetailLine));

    partial void OnStateLabelChanged(string value) => OnPropertyChanged(nameof(DetailLine));

    public static ClientRow From(ClientMeta meta, ClientState state) => new(meta.Name)
    {
        DisplayName = meta.Name,
        Detail = meta.DetailLine,
        State = state,
        StateLabel = state.ZhLabel(),
    };

    public void Apply(ClientMeta meta, ClientState state)
    {
        DisplayName = meta.Name;
        Detail = meta.DetailLine;
        State = state;
        StateLabel = state.ZhLabel();
    }

    public override string ToString() => DisplayName;
}
