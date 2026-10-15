package im.xrl.ceobe.core.util;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;

/**
 * 端口可用性探测（绑定测试，非仅查询）。
 * <p>本机多实例场景下，端口必须在实例间排他——否则玩家的穿透地址会指向错误的服务端。
 */
public final class Ports {

    /** 尝试独占绑定 0.0.0.0:port，能绑定即视为空闲。 */
    public static boolean isFree(int port) {
        if (port < 1 || port > 65535) return false;
        try (ServerSocket socket = new ServerSocket()) {
            socket.setReuseAddress(false);
            socket.bind(new InetSocketAddress("0.0.0.0", port), 1);
            return true;
        } catch (IOException e) {
            return false;
        }
    }

    /** 从 {@code from} 起向上找一个可绑定的空闲端口。 */
    public static int findFree(int from) {
        for (int p = Math.max(1, from); p <= 65535; p++) {
            if (isFree(p)) return p;
        }
        throw CeobeException.of("从端口 %d 起找不到可用端口", from);
    }

    private Ports() {
    }
}
