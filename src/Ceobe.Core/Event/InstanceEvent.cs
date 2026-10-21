using Ceobe.Core.Model;

namespace Ceobe.Core.Event;

/// <summary>
/// core 事件契约：GUI / CLI / Web 控制台订阅同一套（对齐 Java 版 InstanceEvent sealed interface）。
///
/// 形态说明：Java 用 listener 接口 + CopyOnWriteArrayList；C# 侧基类不声明 <see cref="Instance"/>，
/// 改由各 record 的位置参数实现该接口属性（record 位置参数天然满足接口，且不会与基类抽象属性冲突）。
/// </summary>
public interface IInstanceEvent
{
    /// <summary>事件所属实例名。</summary>
    string Instance { get; }
}

/// <summary>服务端日志行（含 ceobe 自身注入的 <c>[ceobe]</c> / <c>$</c> 前缀行）。</summary>
public sealed record InstanceLog(string Instance, string Line) : IInstanceEvent;

/// <summary>状态机推进；进程退出时携带退出码。</summary>
public sealed record StateChanged(string Instance, InstanceState State, int ExitCode) : IInstanceEvent;

/// <summary>穿透通道地址变化（续期后会重新广播）。</summary>
public sealed record TunnelChanged(string Instance, string Kind, string? PublicUrl) : IInstanceEvent;
