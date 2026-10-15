package im.xrl.ceobe.core.tunnel;

import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.CeobeException;

import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.EnumMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * 穿透会话管理：启动/停止/查询 + Pinggy 到期自动续期。
 * <p>地址变化通过 {@link InstanceManager#rememberTunnel} 落盘并广播事件。
 */
public final class TunnelManager implements AutoCloseable {

    private static final Duration RENEW_BEFORE = Duration.ofMinutes(5);

    private final InstanceManager instances;
    private final Map<TunnelKind, TunnelProvider> providers = new EnumMap<>(TunnelKind.class);
    private final Map<String, Map<TunnelKind, TunnelSession>> active = new ConcurrentHashMap<>();
    private final ScheduledExecutorService scheduler = Executors.newSingleThreadScheduledExecutor(
            r -> Thread.ofVirtual().unstarted(r));
    private volatile int webConsolePort = -1;

    public TunnelManager(InstanceManager instances) {
        this.instances = instances;
        register(new PinggyTunnel());
        register(new CloudflareQuickTunnel());
        register(new LanDirect());
    }

    public void register(TunnelProvider provider) {
        providers.put(provider.kind(), provider);
    }

    public void webConsolePort(int port) {
        this.webConsolePort = port;
    }

    public boolean isUp(String instance, TunnelKind kind) {
        Map<TunnelKind, TunnelSession> m = active.get(instance);
        return m != null && m.containsKey(kind);
    }

    public List<TunnelSession> activeOf(String instance) {
        Map<TunnelKind, TunnelSession> m = active.get(instance);
        return m == null ? List.of() : new ArrayList<>(m.values());
    }

    public TunnelProvider provider(TunnelKind kind) {
        TunnelProvider p = providers.get(kind);
        if (p == null) throw CeobeException.of("通道未注册: %s", kind);
        return p;
    }

    /** 开启通道；目标端口：PINGGY→MC 端口，CLOUDFLARE→Web 控制台端口。 */
    public synchronized TunnelSession up(String instance, TunnelKind kind) {
        InstanceStatus status = instances.require(instance);
        if (isUp(instance, kind)) return active.get(instance).get(kind);

        TunnelProvider provider = provider(kind);
        if (!provider.available()) throw new CeobeException(provider.unavailableReason());

        // 玩家通道直指 MC 端口：服务端未就绪时会得到一个指向空端口的“死”通道，先行拦截
        if (kind == TunnelKind.PINGGY && !status.state().isLive()) {
            throw CeobeException.of("实例 %s 未运行，玩家通道需在服务端就绪后开启", instance);
        }

        int localPort = switch (kind) {
            case PINGGY, LAN -> status.meta().port;
            case CLOUDFLARE -> {
                if (webConsolePort <= 0) {
                    throw new CeobeException("Web 控制台未启动，无法开启 Cloudflare 通道");
                }
                yield webConsolePort;
            }
        };

        TunnelSession session = provider.start(instance, instances.dir(instance), localPort,
                line -> instances.tunnelLog(instance, line));
        active.computeIfAbsent(instance, k -> new ConcurrentHashMap<>()).put(kind, session);
        instances.rememberTunnel(instance, kind.id(), session.publicUrl);

        if (kind == TunnelKind.PINGGY) scheduleRenewal(instance, session);
        return session;
    }

    private void scheduleRenewal(String instance, TunnelSession session) {
        Instant renewAt = session.expiresAt.minus(RENEW_BEFORE);
        long delayMs = Math.max(1_000, Duration.between(Instant.now(), renewAt).toMillis());
        scheduler.schedule(() -> renew(instance, session), delayMs, TimeUnit.MILLISECONDS);
    }

    private void renew(String instance, TunnelSession old) {
        try {
            if (!isUp(instance, TunnelKind.PINGGY)) return;
            active.get(instance).get(TunnelKind.PINGGY);
            provider(TunnelKind.PINGGY).stop(old);
            int port = instances.require(instance).meta().port;
            TunnelSession fresh = provider(TunnelKind.PINGGY)
                    .start(instance, instances.dir(instance), port,
                            line -> instances.tunnelLog(instance, line));
            active.get(instance).put(TunnelKind.PINGGY, fresh);
            instances.rememberTunnel(instance, "pinggy", fresh.publicUrl);
            scheduleRenewal(instance, fresh);
        } catch (RuntimeException e) {
            // 续期失败：保留旧记录，30 秒后重试（最长 5 次）
            scheduler.schedule(() -> renew(instance, old), 30, TimeUnit.SECONDS);
        }
    }

    public synchronized void down(String instance, TunnelKind kind) {
        Map<TunnelKind, TunnelSession> m = active.get(instance);
        if (m == null) return;
        TunnelSession session = m.remove(kind);
        if (session != null) provider(kind).stop(session);
    }

    public synchronized void downAll(String instance) {
        for (TunnelKind kind : TunnelKind.values()) {
            down(instance, kind);
        }
        active.remove(instance);
    }

    @Override
    public synchronized void close() {
        scheduler.shutdownNow();
        for (String instance : new ArrayList<>(active.keySet())) {
            downAll(instance);
        }
    }
}
