package im.xrl.ceobe.core.tunnel;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.function.Consumer;

/** 局域网直连：不依赖任何第三方，仅同网段可达；用于快速本机联机。 */
public final class LanDirect implements TunnelProvider {

    @Override
    public TunnelKind kind() {
        return TunnelKind.LAN;
    }

    @Override
    public boolean available() {
        return !localAddresses().isEmpty();
    }

    @Override
    public String unavailableReason() {
        return "未检测到可用的局域网 IPv4 地址";
    }

    @Override
    public TunnelSession start(String instance, Path instanceDir, int localPort, Consumer<String> logSink) {
        List<String> addrs = localAddresses();
        if (addrs.isEmpty()) throw new IllegalStateException(unavailableReason());
        String primary = addrs.get(0) + ":" + localPort;
        logSink.accept("[ceobe] 局域网地址: " + String.join("、", addrs));
        return new TunnelSession(kind(), instance, localPort, primary, null);
    }

    @Override
    public void stop(TunnelSession session) {
        // 无进程
    }

    /** 本机非环回 IPv4 地址（按可用性排序）。 */
    public static List<String> localAddresses() {
        List<String> out = new ArrayList<>();
        try {
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback() || ni.isVirtual()) continue;
                for (InetAddress addr : Collections.list(ni.getInetAddresses())) {
                    if (addr instanceof Inet4Address && !addr.isLoopbackAddress()
                            && !addr.isLinkLocalAddress()) {
                        out.add(addr.getHostAddress());
                    }
                }
            }
        } catch (Exception ignored) {
        }
        return out;
    }
}
