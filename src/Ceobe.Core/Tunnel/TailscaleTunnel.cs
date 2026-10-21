using System.Diagnostics;
using System.Text.RegularExpressions;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// Tailscale 组网直连（整改项 1(c)：长期联机首选）。
///
/// 为什么需要它：Pinggy 免费通道会话短（本项目按 55 分钟续期）且地址每次变化，
/// 而 MC 客户端的服务器地址不能频繁改——玩家体验上就是「每隔一小时要重填地址」。
/// 具体免费配额限制以官方为准，此处不假设数值；结构性结论只有一条：
/// 短期演示隧道不适合承担长期联机，需要一条地址稳定的通道。
/// Tailscale 走虚拟组网，地址稳定（100.x.y.z）、不过期、不依赖公网端口映射，
/// 玩家把 <c>tailscale-ip:port</c> 存进服务器列表就长期有效。
///
/// 实现边界（诚实说明）：本类只读取本机 tailscale 已建立的组网身份，**不代用户登录、不改网络配置**。
/// 未安装或未登录时 <see cref="Available"/> 为 false 并给出可执行指引。
/// Funnel（对公网暴露）需要 tailnet 启用且属另一套语义，本类不做。
/// </summary>
public sealed partial class TailscaleTunnel : ITunnelProvider
{
    /// <summary>取本机 tailscale IP（100.64.0.0/10 段）。<c>tailscale ip -4</c> 输出纯 IP。</summary>
    private static readonly Regex IpLine = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Multiline);

    public TunnelKind Kind => TunnelKind.TAILSCALE;

    public bool Available => TunnelProcess.Which("tailscale") is not null && ReadTailscaleIp() is not null;

    public string UnavailableReason
    {
        get
        {
            if (TunnelProcess.Which("tailscale") is null)
            {
                return "未找到 tailscale 客户端；安装后登录，并确保与朋友在同一 tailnet（免费个人账号够 3 台设备）";
            }
            return "tailscale 已安装但未连接（未登录或服务未启动）；运行 `tailscale up` 完成登录";
        }
    }

    public Task<TunnelSession> StartAsync(string instance, string instanceDir, int localPort,
        Action<string> logSink, CancellationToken ct = default)
    {
        if (!Available) throw new CeobeException(UnavailableReason);

        var ip = ReadTailscaleIp()!;
        // 语义与 LAN 通道一致：直接给出 ip:port，无子进程，不过期。
        var session = new TunnelSession(Kind, instance, localPort, ip + ":" + localPort, null);

        // 复用 MC ping 自检：打自己的 tailscale 地址。
        // 这一步能区分「服务端只绑了回环」——那种情况下组网地址虽然可达，玩家仍连不进来。
        try
        {
            var probe = McPing.ProbeAsync(ip, localPort, McPing.DefaultTimeout, ct)
                .GetAwaiter().GetResult();
            session.Verified = probe.Verified;
            session.Diagnostic = probe.Verified
                ? "已验证：MC 在 tailscale 地址上应答 status"
                : $"无法验证（若 server-ip 被设成 127.0.0.1，请清空该值以监听全部地址）：{probe.Reason}";
        }
        catch (Exception e)
        {
            session.Diagnostic = "无法验证：" + e.Message;
        }

        logSink($"[ceobe] Tailscale 组网地址: {ip}:{localPort}（{session.Diagnostic}）");
        logSink("[ceobe] 提示：朋友需先装 tailscale 并加入同一 tailnet，然后在 MC 多player服务器里填上面地址");
        return Task.FromResult(session);
    }

    public void Stop(TunnelSession session)
    {
        // 无子进程：组网地址由 tailscale 服务常驻维护
    }

    /// <summary>本机 tailscale IPv4；不可用时 null。带超时，避免 tailscale 服务卡死拖住 GUI。</summary>
    internal static string? ReadTailscaleIp()
    {
        var exe = TunnelProcess.Which("tailscale");
        if (exe is null) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("ip");
            psi.ArgumentList.Add("-4");
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
            var outText = p.StandardOutput.ReadToEnd().Trim();
            // `tailscale ip -4` 正常输出单个 IP；未连接时可能为空或多行
            var m = IpLine.Match(outText);
            return m.Success ? m.Value : (outText.Length > 0 && IpLine.IsMatch(outText) ? outText : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// tailscale 的 UDP 打洞在某些 NAT 下需要 DERP 中转，此时裸 TCP 延迟高但仍可用。
    /// 供 GUI 展示「本通道为组网直连，非公网暴露」。
    /// </summary>
    public static string Describe() => "虚拟组网内直连（需双方安装 tailscale 并加入同一 tailnet）";
}
