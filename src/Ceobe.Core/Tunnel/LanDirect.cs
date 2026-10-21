using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Ceobe.Core.Tunnel;

/// <summary>
/// 局域网直连：不依赖任何第三方，仅同网段可达；用于快速本机联机。
/// 移植自 Java <c>tunnel/LanDirect.java</c>（硬约束 6：用 <see cref="NetworkInterface"/> 重写，语义对齐）。
/// <para>与 Java 的判定对齐：跳过未启用 / 环回 / 虚拟网卡；只收非环回、非链路本地的 IPv4。</para>
/// <para>整改项 1(b) 复用：建立后用 <see cref="McPing"/> 对本机地址做一次自ping，
/// 验证「MC 确实在这个地址上应答」——这是最便宜的一条诊断证据。</para>
/// </summary>
public sealed class LanDirect : ITunnelProvider
{
    public TunnelKind Kind => TunnelKind.LAN;

    public bool Available => LocalAddresses().Count > 0;

    public string UnavailableReason => "未检测到可用的局域网 IPv4 地址";

    public async Task<TunnelSession> StartAsync(string instance, string instanceDir, int localPort,
        Action<string> logSink, CancellationToken ct = default)
    {
        var addrs = LocalAddresses();
        if (addrs.Count == 0) throw new InvalidOperationException(UnavailableReason);
        var primary = addrs[0] + ":" + localPort;
        logSink("[ceobe] 局域网地址: " + string.Join("、", addrs));
        var session = new TunnelSession(Kind, instance, localPort, primary, null);

        // 自ping：打自己第一个 LAN 地址。能收到 status JSON 说明服务端确实在监听（且 server-ip 兼容该地址）。
        var probe = await McPing.ProbeAsync(addrs[0], localPort, McPing.DefaultTimeout, ct)
            .ConfigureAwait(false);
        session.Verified = probe.Verified;
        session.Diagnostic = probe.Verified
            ? "已验证：本机 MC 在该地址应答 status"
            : $"无法验证（服务端可能未就绪、绑定了其它地址或禁用了 status）：{probe.Reason}";
        logSink("[ceobe] 局域网自检：" + session.Diagnostic);
        return session;
    }

    public void Stop(TunnelSession session)
    {
        // 无进程
    }

    /// <summary>本机非环回 IPv4 地址（按接口枚举顺序）。</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var outList = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or (NetworkInterfaceType)24 /* ISATAP 等虚拟隧道 */
                    or (NetworkInterfaceType)131 /* Tunnel */
                    or (NetworkInterfaceType)166 /* IP over IEEE1394 */) continue;
                if (IsVirtualName(ni.Name)) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (System.Net.IPAddress.IsLoopback(ua.Address)) continue;
                    if (ua.Address.GetAddressBytes()[0] == 169 && ua.Address.GetAddressBytes()[1] == 254)
                        continue; // 169.254/16 = link-local（APIPA），Java 的 isLinkLocalAddress 同义
                    outList.Add(ua.Address.ToString());
                }
            }
        }
        catch (Exception)
        {
            // 枚举网络接口失败（权限/驱动异常）按「无地址」处理，与 Java 一致
        }
        return outList;
    }

    /// <summary>
    /// 虚拟网卡启发式：Java 用 <c>NetworkInterface.isVirtual()</c>（对 Windows 大多返回 false，
    /// 实际过滤靠 loopback/link-local）；C# 无此判定，这里用名称关键词补一刀，
    /// 避免把 vEthernet/TAP/VPN 假地址当首选。找不到时仍由「地址本身是否可达」兜底。
    /// </summary>
    private static bool IsVirtualName(string name)
    {
        foreach (var kw in new[] { "vethernet", "tap-", "tun-", "wintun", "virtual", "hyper-v",
                                   "loopback", "zero-span", "isatap" })
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
