namespace Ceobe.Core.Client;

/// <summary>
/// 客户端事件契约。与服务端的 <see cref="Event.IInstanceEvent"/> 平行但**不共用**：
/// 服务端事件里带着端口与穿透语义，客户端只关心日志与进程状态，
/// 共用一个接口会让订阅方写一堆 <c>if (evt is ...)</c> 的空分支。
/// </summary>
public interface IClientEvent
{
    string Instance { get; }
}

/// <summary>一行游戏输出（stdout/stderr 合并后按行切分）。</summary>
public sealed record ClientLogLine(string Instance, string Line) : IClientEvent;

/// <summary>状态推进；进程退出时携带退出码（非 0 即视为崩溃，交给上层解析 crash-report）。</summary>
public sealed record ClientStateChanged(string Instance, ClientState State, int ExitCode) : IClientEvent;
