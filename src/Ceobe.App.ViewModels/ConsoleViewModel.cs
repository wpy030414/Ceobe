using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ceobe.Core.Event;
using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「控制台」页签 ViewModel（Java 版 <c>ConsolePane</c>）：实时日志 + 命令下发 + 启停 + 删除。
///
/// 两条日志来源必须都支持（这是 Java 版的核心设计，不能只实现一半）：
/// 1. **本进程托管**（GUI 自己启动的实例）：订阅 <see cref="InstanceManager.OnEvent"/> 拿实时行；
/// 2. **外部托管**（CLI <c>start -d</c> 起的监督进程）：GUI 只是旁观者，
///    只能按文件偏移增量跟随 console.log，见 <see cref="PollExternalLog"/>。
/// 漏掉第 2 条的后果是：用户在 CLI 里起了服，GUI 的控制台永远空白，看起来像坏了。
/// </summary>
public partial class ConsoleViewModel : ObservableObject
{
    private readonly InstanceManager _manager;
    private readonly Action<IInstanceEvent> _onEvent;
    private readonly string _instance;
    private readonly string _dir;
    private readonly string _logFile;
    private readonly bool _hosted;
    private long _logOffset;

    /// <summary>日志行（界面绑定）。有上限，见 <see cref="MaxLines"/>。</summary>
    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>命令输入框绑定。</summary>
    [ObservableProperty]
    private string _command = "";

    /// <summary>跟随：新日志到达时界面应滚到底部（Java 版的 follow 复选框）。</summary>
    [ObservableProperty]
    private bool _follow = true;

    [ObservableProperty]
    private InstanceState _state = InstanceState.STOPPED;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>是否有操作进行中（启停都是秒级到分钟级，必须禁用按钮防重复点击）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>实例是否已删除（界面据此关闭页签）。</summary>
    [ObservableProperty]
    private bool _deleted;

    /// <summary>
    /// 日志行上限。Java 版用 TextArea 无上限，长时间运行的服会把内存吃光
    /// （一个繁忙服一天能产几十万行）。这里按环缓冲语义截断——
    /// 与 <c>ServerProcess</c> 的 800 行内存环不同，界面留更多便于回溯，但仍需有界。
    /// </summary>
    public const int MaxLines = 5000;

    /// <summary>初次加载读取的历史行数（对齐 Java 版 loadHistory 的 300）。</summary>
    private const int HistoryLines = 300;

    public ConsoleViewModel(InstanceManager manager, string instance)
    {
        _manager = manager;
        _instance = instance;
        _dir = manager.Dir(instance);
        _logFile = Path.Combine(_dir, "console.log");
        _hosted = manager.ProcessOf(instance) is not null;
        // Core 事件来自监督进程/日志泵线程；ObservableCollection 与绑定属性必须在 UI 线程改。
        _onEvent = evt => UiThread.Post(() => OnEvent(evt));
        _manager.OnEvent += _onEvent;
    }

    public string InstanceName => _instance;

    /// <summary>是否本进程托管（决定日志来源与命令通道）。</summary>
    public bool IsHosted => _hosted;

    public bool IsLive => State.IsLive();

    /// <summary>状态徽标文案。</summary>
    public string StateLabel => State.ZhLabel();

    /// <summary>状态徽标样式键（界面据此着色，对齐 Java 的 state-RUNNING 之类）。</summary>
    public string StateKey => State.ToString();

    // 按钮可用性：全部由 live 派生，避免界面各处自己判断而漏改
    public bool CanStart => !IsLive && !IsBusy && !Deleted;
    public bool CanStop => IsLive && !IsBusy;
    public bool CanSend => IsLive && !IsBusy;
    public bool CanDelete => !IsLive && !IsBusy && !Deleted;

    partial void OnStateChanged(InstanceState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateKey));
        RaiseButtonStates();
    }

    partial void OnIsBusyChanged(bool value) => RaiseButtonStates();

    partial void OnDeletedChanged(bool value) => RaiseButtonStates();

    private void RaiseButtonStates()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanDelete));
    }

    /// <summary>加载历史日志并记录文件偏移（外部托管时后续按此增量跟随）。</summary>
    public void LoadHistory()
    {
        Lines.Clear();
        IReadOnlyList<string> lines;
        if (_hosted)
        {
            lines = _manager.ProcessOf(_instance)?.RecentLogs(HistoryLines) ?? ReadFileTail(HistoryLines);
        }
        else
        {
            lines = ReadFileTail(HistoryLines);
        }
        foreach (var l in lines) Append(l);

        try
        {
            _logOffset = File.Exists(_logFile) ? new FileInfo(_logFile).Length : 0;
        }
        catch (IOException)
        {
            _logOffset = 0;
        }

        var status = _manager.Find(_instance);
        State = status?.State ?? InstanceState.STOPPED;
        HasError = false;
        StatusText = _hosted ? "" : "该实例由外部进程托管（CLI 启动），日志按文件增量跟随";
    }

    private IReadOnlyList<string> ReadFileTail(int tail)
    {
        if (!File.Exists(_logFile)) return [];
        try
        {
            var all = File.ReadAllLines(_logFile, new System.Text.UTF8Encoding(false));
            return all.Length <= tail ? all : all[^tail..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 外部托管实例的增量跟随：按文件偏移读新增行。
    /// 由界面的定时器周期调用（Java 版同名方法，语义一致）。
    /// </summary>
    public void PollExternalLog()
    {
        if (_hosted) return;   // 本进程托管走事件流，不轮询文件
        try
        {
            if (!File.Exists(_logFile)) return;
            var size = new FileInfo(_logFile).Length;
            // 日志被轮转/重建（新文件比记录的偏移小）→ 从头读，否则会跳过整段
            if (size < _logOffset) _logOffset = 0;
            if (size == _logOffset) return;

            using var fs = new FileStream(_logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_logOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, new System.Text.UTF8Encoding(false));
            string? line;
            while ((line = reader.ReadLine()) is not null) Append(line);
            _logOffset = size;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 轮询失败不打扰用户：下一轮再试
        }
    }

    /// <summary>追加一行；超出上限时丢最旧的（环缓冲语义，界面内存必须有界）。</summary>
    public void Append(string line)
    {
        if (Lines.Count >= MaxLines)
        {
            // 一次丢掉 10% 而不是每来一行丢一行：频繁 RemoveAt(0) 会让 ListView 反复重排
            var drop = MaxLines / 10;
            for (var i = 0; i < drop && Lines.Count > 0; i++) Lines.RemoveAt(0);
        }
        Lines.Add(line);
    }

    private void OnEvent(IInstanceEvent evt)
    {
        if (evt.Instance != _instance) return;
        switch (evt)
        {
            case InstanceLog log:
                Append(log.Line);
                break;
            case StateChanged sc:
                State = sc.State;
                if (sc.State == InstanceState.CRASHED)
                {
                    HasError = true;
                    StatusText = $"实例异常退出（退出码 {sc.ExitCode}），请查看上面的日志";
                }
                break;
            case TunnelChanged tc:
                // 隧道地址变化对控制台也有用：直接贴出来方便复制发给朋友
                Append($"[ceobe] {tc.Kind} 地址: {tc.PublicUrl}");
                break;
        }
    }

    [RelayCommand]
    private async Task StartAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _manager.Start(_instance), ct).ConfigureAwait(true);
            HasError = false;
            StatusText = "已启动（本界面即监督进程）";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            // 启动失败必须可见：Java 版曾因异常只写进文本框而表现为「静默失败」（ADR-018）
            HasError = true;
            StatusText = "启动失败: " + e.Message;
            Append("[ceobe] 启动失败: " + e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                if (_manager.ProcessOf(_instance) is not null)
                {
                    _manager.Stop(_instance);
                }
                else if (InstanceControl.IsAvailable(_dir))
                {
                    InstanceControl.Stop(_dir);
                }
                else
                {
                    throw new CeobeException("实例不受控，无法停止（既不由本界面托管，也没有控制通道）");
                }
            }, ct).ConfigureAwait(true);
            HasError = false;
            StatusText = "已请求停止";
            Append("[ceobe] 已请求停止");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "停止失败: " + e.Message;
            Append("[ceobe] 停止失败: " + e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>发送命令。本进程托管走 stdin，外部托管走控制通道。</summary>
    [RelayCommand]
    private async Task SendAsync(CancellationToken ct)
    {
        var cmd = Command.Trim();
        if (cmd.Length == 0) return;
        if (!IsLive)
        {
            HasError = true;
            StatusText = "实例未运行，无法发送命令";
            return;
        }
        Command = "";   // 先清空，避免发送失败时用户重复提交同一条

        try
        {
            await Task.Run(() =>
            {
                var p = _manager.ProcessOf(_instance);
                if (p is { IsAlive: true }) p.SendCommand(cmd);
                else InstanceControl.Exec(_dir, cmd);
            }, ct).ConfigureAwait(true);
            HasError = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "命令发送失败: " + e.Message;
            Append("[ceobe] 命令发送失败: " + e.Message);
            Command = cmd;   // 发送失败把命令还回去，用户可以改了重发
        }
    }

    /// <summary>
    /// 删除实例。<paramref name="purge"/>=true 连世界数据一起删（永久、不可逆）。
    ///
    /// 确认对话框归 View 层（ContentDialog 需要 UI 线程与 XamlRoot），
    /// VM 只接受**已经决定好的**模式——这样删除逻辑能被单测覆盖，
    /// 不必为了测它而拉起 UI。View 必须提供三选：仅移除 / 连数据删除 / 取消。
    /// </summary>
    [RelayCommand]
    private void Delete(bool purge)
    {
        if (IsLive)
        {
            HasError = true;
            StatusText = "实例仍在运行，请先停止再删除";
            return;
        }
        try
        {
            _manager.Remove(_instance, purge);
            Deleted = true;
            HasError = false;
            StatusText = purge ? "实例已删除（含世界数据）" : "实例已删除（世界数据保留在目录中）";
            Append("[ceobe] " + StatusText);
        }
        catch (CeobeException e)
        {
            HasError = true;
            StatusText = "删除失败: " + e.Message;
        }
    }

    /// <summary>清空界面日志（不删磁盘上的 console.log）。</summary>
    [RelayCommand]
    private void ClearView()
    {
        Lines.Clear();
        HasError = false;
        StatusText = "已清空显示（console.log 未删除）";
    }

    /// <summary>界面关闭时取消订阅，避免 Core 事件持有已释放的 ViewModel。</summary>
    public void Detach() => _manager.OnEvent -= _onEvent;
}
