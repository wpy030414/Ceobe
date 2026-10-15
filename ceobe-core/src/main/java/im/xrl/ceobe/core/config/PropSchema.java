package im.xrl.ceobe.core.config;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * server.properties schema 基线 = vanilla 26.3 实测生成的键集（46 键），
 * 另含少量 1.21.x 旧键（标 legacy，保证老配置不丢字段）。
 * 见 docs/ARCHITECTURE.md 与 docs/DECISIONS.md ADR-006。
 */
public final class PropSchema {
    public static final String G_BASIC = "基础";
    public static final String G_RULES = "游戏规则";
    public static final String G_WORLD = "世界";
    public static final String G_NET = "网络";
    public static final String G_PERF = "性能";
    public static final String G_SEC = "安全";
    public static final String G_ADV = "高级";

    private static final Map<String, PropKey> KEYS = new LinkedHashMap<>();

    static {
        // 基础
        add(PropKey.string("motd", "服务器描述", G_BASIC, "A Minecraft Server"));
        add(PropKey.integer("max-players", "最大玩家数", G_BASIC, 20, 1, 10000));
        add(PropKey.enumerate("gamemode", "默认游戏模式", G_BASIC, "survival",
                "survival", "creative", "adventure", "spectator"));
        add(PropKey.enumerate("difficulty", "难度", G_BASIC, "easy",
                "peaceful", "easy", "normal", "hard"));
        add(PropKey.bool("force-gamemode", "强制游戏模式", G_BASIC, false));
        add(PropKey.bool("hardcore", "极限模式", G_BASIC, false));
        add(PropKey.bool("pvp", "允许 PVP", G_BASIC, true));
        add(PropKey.bool("require-resource-pack", "强制资源包", G_BASIC, false));
        add(PropKey.string("resource-pack", "资源包 URL", G_BASIC, ""));
        add(PropKey.string("resource-pack-id", "资源包 ID", G_BASIC, ""));
        add(PropKey.string("resource-pack-prompt", "资源包提示", G_BASIC, ""));
        add(PropKey.string("resource-pack-sha1", "资源包 SHA1", G_BASIC, ""));

        // 游戏规则
        add(PropKey.bool("allow-flight", "允许飞行", G_RULES, false));
        add(PropKey.bool("enable-command-block", "启用命令方块", G_RULES, false));
        add(PropKey.integer("spawn-protection", "出生点保护半径", G_RULES, 16, 0, 1000));
        add(PropKey.integer("player-idle-timeout", "挂机踢出分钟", G_RULES, 0, 0, 1440));
        add(PropKey.integer("chat-spam-threshold-seconds", "聊天限流窗口(秒)", G_RULES, 10, 0, 1000));
        add(PropKey.integer("command-spam-threshold-seconds", "命令限流窗口(秒)", G_RULES, 10, 0, 1000));
        add(PropKey.integer("op-permission-level", "OP 权限等级", G_RULES, 4, 1, 4));
        add(PropKey.integer("function-permission-level", "函数权限等级", G_RULES, 2, 1, 4));

        // 世界
        add(PropKey.string("level-name", "世界名", G_WORLD, "world"));
        add(PropKey.string("level-seed", "世界种子", G_WORLD, ""));
        add(PropKey.string("level-type", "世界类型", G_WORLD, "minecraft:normal"));
        add(PropKey.string("generator-settings", "自定义生成器设置", G_WORLD, "{}"));
        add(PropKey.bool("generate-structures", "生成结构", G_WORLD, true));
        add(PropKey.integer("max-world-size", "世界边界半径", G_WORLD, 29999984, 1, 29999984));
        add(PropKey.string("initial-enabled-packs", "初始启用数据包", G_WORLD, "vanilla"));
        add(PropKey.string("initial-disabled-packs", "初始禁用数据包", G_WORLD, ""));

        // 网络
        add(PropKey.integer("server-port", "服务器端口", G_NET, 25565, 1, 65535));
        add(PropKey.string("server-ip", "绑定 IP（空=全部）", G_NET, ""));
        add(PropKey.bool("online-mode", "正版验证", G_NET, true));
        add(PropKey.bool("enable-status", "在服务器列表显示", G_NET, true));
        add(PropKey.bool("hide-online-players", "隐藏在线玩家", G_NET, false));
        add(PropKey.bool("enable-query", "启用 Query 协议", G_NET, false));
        add(PropKey.integer("query.port", "Query 端口", G_NET, 25565, 1, 65535));
        add(PropKey.bool("enable-rcon", "启用 RCON", G_NET, false));
        add(PropKey.integer("rcon.port", "RCON 端口", G_NET, 25575, 1, 65535));
        add(PropKey.string("rcon.password", "RCON 密码", G_NET, ""));
        add(PropKey.integer("rate-limit", "单 IP 连接速率上限", G_NET, 0, 0, 100000));
        add(PropKey.bool("prevent-proxy-connections", "阻止代理连接", G_NET, false));
        add(PropKey.bool("use-native-transport", "使用原生传输优化", G_NET, true));

        // 性能
        add(PropKey.integer("view-distance", "视距（区块）", G_PERF, 10, 3, 32));
        add(PropKey.integer("simulation-distance", "模拟距离（区块）", G_PERF, 10, 3, 32));
        add(PropKey.integer("entity-broadcast-range-percentage", "实体广播范围(%)", G_PERF, 100, 10, 1000));
        add(PropKey.integer("network-compression-threshold", "网络压缩阈值", G_PERF, 256, -1, 65536));
        add(PropKey.integer("max-tick-time", "单 tick 超时(ms)", G_PERF, 60000, -1, 600000));
        add(PropKey.bool("sync-chunk-writes", "同步区块写入", G_PERF, true));
        add(PropKey.integer("pause-when-empty-seconds", "空服暂停(秒)", G_PERF, 60, 0, 3600));
        add(PropKey.enumerate("region-file-compression", "区域文件压缩", G_PERF, "deflate",
                "deflate", "lz4", "none"));

        // 安全
        add(PropKey.bool("white-list", "启用白名单", G_SEC, true));
        add(PropKey.bool("enforce-whitelist", "强制白名单", G_SEC, false));
        add(PropKey.bool("enforce-secure-profile", "强制安全档案签名", G_SEC, true));
        add(PropKey.bool("log-ips", "记录玩家 IP", G_SEC, true));

        // 高级
        add(PropKey.bool("enable-jmx-monitoring", "启用 JMX 监控", G_ADV, false));
        add(PropKey.bool("broadcast-console-to-ops", "控制台消息广播给 OP", G_ADV, true));
        add(PropKey.bool("broadcast-rcon-to-ops", "RCON 消息广播给 OP", G_ADV, true));
        add(PropKey.bool("accepts-transfers", "接受跨服转移", G_ADV, false));
        add(PropKey.bool("enable-code-of-conduct", "启用行为准则", G_ADV, false));
        add(PropKey.string("bug-report-link", "问题反馈链接", G_ADV, ""));
        add(PropKey.integer("max-chained-neighbor-updates", "连锁方块更新上限", G_ADV, 1000000, 0, Integer.MAX_VALUE));
        add(PropKey.integer("status-heartbeat-interval", "状态心跳间隔", G_ADV, 0, 0, 3600));
        add(PropKey.string("text-filtering-config", "文本过滤配置", G_ADV, ""));
        add(PropKey.integer("text-filtering-version", "文本过滤版本", G_ADV, 0, 0, Integer.MAX_VALUE));
        add(PropKey.bool("management-server-enabled", "启用管理服务端 API", G_ADV, false));
        add(PropKey.string("management-server-host", "管理 API 绑定", G_ADV, "localhost"));
        add(PropKey.integer("management-server-port", "管理 API 端口", G_ADV, 0, 0, 65535));
        add(PropKey.string("management-server-secret", "管理 API 密钥", G_ADV, ""));
        add(PropKey.bool("management-server-tls-enabled", "管理 API 启用 TLS", G_ADV, true));
        add(PropKey.string("management-server-tls-keystore", "管理 API 密钥库", G_ADV, ""));
        add(PropKey.string("management-server-tls-keystore-password", "管理 API 密钥库口令", G_ADV, ""));
        add(PropKey.string("management-server-allowed-origins", "管理 API 允许来源", G_ADV, ""));

        // 旧版本键（legacy，1.21.x 及更早可能仍存在）
        add(PropKey.bool("spawn-monsters", "生成怪物", G_RULES, true).asLegacy());
        add(PropKey.bool("spawn-animals", "生成动物", G_RULES, true).asLegacy());
        add(PropKey.bool("spawn-npcs", "生成村民", G_RULES, true).asLegacy());
        add(PropKey.bool("spawn-water-animals", "生成水生生物", G_RULES, true).asLegacy());
        add(PropKey.bool("allow-nether", "允许下界", G_WORLD, true).asLegacy());
        add(PropKey.bool("previews-chat", "聊天预览", G_ADV, false).asLegacy());
        add(PropKey.bool("enable-command-block", "启用命令方块", G_RULES, false).asLegacy());
    }

    private static void add(PropKey key) {
        KEYS.putIfAbsent(key.key(), key);
    }

    public static PropKey key(String name) {
        return KEYS.get(name);
    }

    public static List<PropKey> all() {
        return List.copyOf(KEYS.values());
    }

    /** 当前版本（非 legacy）的分组顺序。 */
    public static List<String> groups() {
        return List.of(G_BASIC, G_RULES, G_WORLD, G_NET, G_PERF, G_SEC, G_ADV);
    }

    private PropSchema() {
    }
}
