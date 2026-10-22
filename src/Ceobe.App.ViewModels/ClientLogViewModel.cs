using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ceobe.Core.Client;

namespace Ceobe.App.ViewModels;

/// <summary>
/// 「日志」分区（设计稿 06）。主视觉是**结论**，日志退为证据。
///
/// 崩溃报告只在进程异常退出时才有价值，所以这里做两件事：
/// 1) 实时跟随 stdout（用户能看到「卡在哪一行」）；
/// 2) 退出后解析最新一份 crash-report，给出「哪个 Mod / 为什么 / 怎么办」。
/// 解析规则在 Core（<see cref="CrashReportParser"/>），CLI 也能复用同一套结论。
/// </summary>
public partial class ClientLogViewModel : ObservableObject
{
    private const int MaxLines = 5000;

    private readonly ClientRuntime _runtime;
    private readonly ClientMeta _meta;
    private readonly Action<IClientEvent> _onEvent;

    public ObservableCollection<string> Lines { get; } = [];
    public ObservableCollection<string> Actions { get; } = [];
    public ObservableCollection<ClientCrashRow> RecentCrashes { get; } = [];

    [ObservableProperty]
    private bool _follow = true;

    [ObservableProperty]
    private bool _hasCrash;

    [ObservableProperty]
    private string _crashHeadline = "";

    [ObservableProperty]
    private string _crashCause = "";

    [ObservableProperty]
    private string _exceptionLine = "";

    [ObservableProperty]
    private string _suspectMod = "";

    [ObservableProperty]
    private string _crashTime = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _logPath = "";

    [ObservableProperty]
    private bool _isRunning;

    public ClientLogViewModel(ClientRuntime runtime, ClientMeta meta)
    {
        _runtime = runtime;
        _meta = meta;
        _onEvent = evt => UiThread.Post(() => Handle(evt));
        _runtime.OnEvent += _onEvent;
        LogPath = ClientStore.LatestLog(meta.Name);
    }

    public void Reload()
    {
        Lines.Clear();
        foreach (var line in _runtime.RecentLogs(_meta.Name, MaxLines)) Append(line);
        if (Lines.Count == 0)
        {
            foreach (var line in CrashReports.Tail(LogPath, 200)) Append(line);
        }

        IsRunning = _runtime.IsRunning(_meta.Name);
        StatusText = IsRunning
            ? "实时跟随中（游戏输出会即时出现在这里）"
            : Lines.Count == 0 ? "还没有运行记录，启动一次游戏后这里会有输出" : "历史日志（logs/latest.log 的尾部）";

        LoadCrash();
    }

    private void LoadCrash()
    {
        var latest = CrashReports.Latest(_meta.Name);
        if (latest is null)
        {
            HasCrash = _meta.LastExit == "crashed";
            if (HasCrash)
            {
                CrashHeadline = "上次异常退出，但没有生成崩溃报告";
                CrashCause = "游戏进程非正常结束时，JVM 可能来不及写 crash-report（例如被强杀、显卡驱动崩溃或 Java 本身启动失败）。";
                Actions.Clear();
                Actions.Add("看下方日志最后 30 行，确认退出前最后一条输出");
                Actions.Add("检查「设置」里的 JVM 参数与 Java 版本");
            }
            RecentCrashes.Clear();
            return;
        }

        HasCrash = true;
        CrashTime = new DateTimeOffset(File.GetLastWriteTimeUtc(latest), TimeSpan.Zero)
            .ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var analysis = CrashReportParser.Parse(CrashReports.ReadText(latest),
            ClientModScanner.Scan(_meta.Name).Select(m => m.ModId ?? "").Where(s => s.Length > 0).ToList());
        CrashHeadline = analysis.Headline;
        CrashCause = analysis.Cause;
        ExceptionLine = analysis.ExceptionLine ?? "";
        SuspectMod = analysis.SuspectMod ?? "";
        Actions.Clear();
        foreach (var a in analysis.Actions) Actions.Add(a);

        RecentCrashes.Clear();
        foreach (var (path, at) in CrashReports.Recent(_meta.Name, 3))
        {
            RecentCrashes.Add(new ClientCrashRow(
                at.ToLocalTime().ToString("MM-dd HH:mm"), Path.GetFileName(path)));
        }
    }

    private void Handle(IClientEvent evt)
    {
        if (evt.Instance != _meta.Name) return;
        switch (evt)
        {
            case ClientLogLine log:
                Append(log.Line);
                break;
            case ClientStateChanged sc:
                IsRunning = sc.State.IsLive();
                if (sc.State == ClientState.CRASHED)
                {
                    StatusText = $"游戏异常退出（退出码 {sc.ExitCode}），已解析崩溃报告";
                    LoadCrash();
                }
                else if (sc.State == ClientState.STOPPED)
                {
                    StatusText = "游戏已退出";
                    LoadCrash();
                }
                break;
        }
    }

    private void Append(string line)
    {
        if (Lines.Count >= MaxLines)
        {
            for (var i = 0; i < MaxLines / 10 && Lines.Count > 0; i++) Lines.RemoveAt(0);
        }
        Lines.Add(line);
    }

    public void Clear()
    {
        Lines.Clear();
        StatusText = "已清空显示（logs/latest.log 未删除）";
    }

    public void Detach() => _runtime.OnEvent -= _onEvent;
}

/// <summary>最近崩溃报告列表项。</summary>
public sealed record ClientCrashRow(string Time, string FileName);
