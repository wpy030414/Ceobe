package im.xrl.ceobe.core.provider;

import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.util.CeobeException;

import java.util.EnumMap;
import java.util.Map;

/** provider 注册表：类型 → 实现。 */
public final class ProviderRegistry {
    private static final Map<ServerType, ServerJarProvider> PROVIDERS = new EnumMap<>(ServerType.class);

    static {
        register(new MojangProvider());
        register(new PaperProvider());
        register(new FabricProvider());
        register(new PurpurProvider());
    }

    public static void register(ServerJarProvider provider) {
        PROVIDERS.put(provider.type(), provider);
    }

    public static ServerJarProvider of(ServerType type) {
        ServerJarProvider p = PROVIDERS.get(type);
        if (p == null) throw CeobeException.of("暂不支持的服务端类型: %s", type);
        return p;
    }

    private ProviderRegistry() {
    }
}
