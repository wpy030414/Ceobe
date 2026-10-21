using Ceobe.Core.Config;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// 绑定一致性守卫（整改项 1(a)）。
/// <para>背景：隧道把公网流量打到本地 <c>targetHost:port</c>（Pinggy 恒为 127.0.0.1），
/// 而 <c>server-ip</c> 是 GUI 可改项——服务端一旦绑定到具体网卡地址，就不再监听回环，
/// 隧道会指向一个空端口（「本地正常、外部连不上」的候选病因之一）。</para>
/// <para>判定语义：空 / 0.0.0.0 / :: → 监听所有地址，与任何目标兼容；
/// 回环字面量（localhost / 127.0.0.1 / ::1）→ 仅当隧道目标也是回环时兼容；
/// 其余视为「具体地址」→ 仅当它恰好等于隧道目标时兼容，否则抛异常并给出两个可执行选项。</para>
/// <para>公开以便阶段二 GUI「玩家/穿透」页签直接复用同一判定，并与 McPing 一样保持可独立单测。</para>
/// </summary>
public static class TunnelBindGuard
{
    private static readonly string[] AllInterfaces = ["", "0.0.0.0", "::", "::0"];
    private static readonly string[] LoopbackLiterals = ["localhost", "127.0.0.1", "::1"];

    /// <summary>
    /// 校验 <c>server-ip</c> 与隧道本地目标兼容；不一致抛 <see cref="CeobeException"/>。
    /// server.properties 不存在时（尚未落盘）不做拦截——启动期由创建流程保证。
    /// </summary>
    /// <param name="instanceDir">实例目录</param>
    /// <param name="tunnelTargetHost">隧道的本地目标主机（如 127.0.0.1 / tailscale IP）</param>
    /// <param name="port">隧道指向的端口（仅用于报错信息）</param>
    /// <param name="tunnelLabel">通道中文名（用于报错信息）</param>
    internal static void AssertCompatible(string instanceDir, string tunnelTargetHost, int port,
        string tunnelLabel)
    {
        var props = ParseServerIp(instanceDir);
        if (props is null) return;
        var (raw, value) = props.Value;
        var target = Normalize(tunnelTargetHost);

        // 未显式配置该键（老配置文件）与显式留空同义：全部地址。
        if (raw is null || Array.IndexOf(AllInterfaces, value) >= 0) return;

        var targetIsLoopback = Array.IndexOf(LoopbackLiterals, target) >= 0;
        var bindIsLoopback = Array.IndexOf(LoopbackLiterals, value) >= 0;

        if (bindIsLoopback && targetIsLoopback) return; // 回环 → 回环，天然一致
        if (value == target) return;                    // 具体地址 == 隧道目标，可打通

        throw CeobeException.Of(
            "server-ip={0} 与隧道目标 {1} 不一致：服务端只监听 {0}，{2} 打到 {1}:{3} 必然连不上。" +
            "二选一修正——① 把 server-ip 留空（监听全部地址，推荐）；② 把 server-ip 改为 {1}",
            raw, tunnelTargetHost, tunnelLabel, port);
    }

    /// <summary>读取并规整 server-ip（返回 null 表示文件不存在，不拦截）。值做 trim 与小写规整前先保留原文用于报错。</summary>
    private static (string Raw, string Value)? ParseServerIp(string instanceDir)
    {
        var file = Path.Combine(instanceDir, "server.properties");
        if (!File.Exists(file)) return null;
        ServerPropertiesFile f;
        try
        {
            f = ServerPropertiesFile.Load(file);
        }
        catch (CeobeException)
        {
            return null; // 读不动就不要拦住开启通道；诊断信息走日志即可
        }
        var raw = f.Get("server-ip");
        if (raw is null) return ("", ""); // 键缺失：按未配置处理
        var v = Normalize(raw);
        return (raw.Trim(), v);
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();
}
