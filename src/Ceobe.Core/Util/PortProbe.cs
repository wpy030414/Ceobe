using System.Net;
using System.Net.Sockets;

namespace Ceobe.Core.Util;

/// <summary>
/// 端口可用性探测（绑定测试，非仅查询）。
/// 本机多实例场景下，端口必须在实例间排他——否则玩家的穿透地址会指向错误的服务端（ADR-009）。
/// </summary>
public static class PortProbe
{
    /// <summary>尝试独占绑定 0.0.0.0:port，能绑定即视为空闲。</summary>
    public static bool IsFree(int port)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true,
            };
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>从 <paramref name="from"/> 起向上找一个可绑定的空闲端口。</summary>
    public static int FindFree(int from)
    {
        for (var p = Math.Max(1, from); p <= 65535; p++)
        {
            if (IsFree(p)) return p;
        }
        throw CeobeException.Of("从端口 {0} 起找不到可用端口", from);
    }
}
