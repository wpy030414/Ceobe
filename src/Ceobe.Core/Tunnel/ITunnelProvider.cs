namespace Ceobe.Core.Tunnel;

/// <summary>
/// 穿透通道 SPI。移植自 Java <c>tunnel/TunnelProvider.java</c>（ADR-003：
/// CF Quick Tunnel 仅 HTTP，玩家 MC 裸 TCP 走 Pinggy）。
/// 新增通道（Tailscale / playit / named tunnel）只需实现本接口并在 <see cref="TunnelManager"/> 注册。
/// <para>语义差异：<c>StartAsync</c> 异步化（Java 的 start 阻塞到就绪，内部靠轮询 sleep；
/// C# 用进程退出信号 + 行到达信号等待，见 <see cref="TunnelProcess"/>）。</para>
/// </summary>
public interface ITunnelProvider
{
    TunnelKind Kind { get; }

    /// <summary>通道可用性（二进制/命令/网络条件是否就绪）。</summary>
    bool Available { get; }

    /// <summary>不可用原因（可直接展示给用户）。</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// 启动隧道并等待至公网地址就绪（含就绪探测，实现方决定超时）。
    /// </summary>
    /// <param name="instance">实例名</param>
    /// <param name="instanceDir">实例目录绝对路径（日志 / known_hosts 落盘处）</param>
    /// <param name="localPort">目标本地端口（Pinggy/Tailscale/LAN=MC 端口，Cloudflare=Web 控制台端口）</param>
    /// <param name="logSink">隧道进程诊断输出（逐行，含 [tunnel] 前缀的原样文本）</param>
    /// <param name="ct">取消令牌</param>
    Task<TunnelSession> StartAsync(string instance, string instanceDir, int localPort,
        Action<string> logSink, CancellationToken ct = default);

    void Stop(TunnelSession session);
}
