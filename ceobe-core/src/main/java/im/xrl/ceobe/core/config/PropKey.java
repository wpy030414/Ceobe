package im.xrl.ceobe.core.config;

import java.util.List;

/**
 * server.properties 单个键的 schema 描述。
 *
 * @param key      键名
 * @param zh       中文友好名
 * @param group    分组（基础/游戏规则/世界/网络/性能/安全/高级）
 * @param type     类型
 * @param def      默认值（字符串原样）
 * @param options  ENUM 的可选值
 * @param min      INT 下限（无则 Integer.MIN_VALUE）
 * @param max      INT 上限（无则 Integer.MAX_VALUE）
 * @param legacy   是否为旧版本键（当前版本已不再生成）
 */
public record PropKey(String key, String zh, String group, PropType type, String def,
                      List<String> options, int min, int max, boolean legacy) {

    public static PropKey bool(String key, String zh, String group, boolean def) {
        return new PropKey(key, zh, group, PropType.BOOL, String.valueOf(def), List.of(),
                Integer.MIN_VALUE, Integer.MAX_VALUE, false);
    }

    public static PropKey integer(String key, String zh, String group, int def, int min, int max) {
        return new PropKey(key, zh, group, PropType.INT, String.valueOf(def), List.of(), min, max, false);
    }

    public static PropKey enumerate(String key, String zh, String group, String def, String... options) {
        return new PropKey(key, zh, group, PropType.ENUM, def, List.of(options),
                Integer.MIN_VALUE, Integer.MAX_VALUE, false);
    }

    public static PropKey string(String key, String zh, String group, String def) {
        return new PropKey(key, zh, group, PropType.STRING, def, List.of(),
                Integer.MIN_VALUE, Integer.MAX_VALUE, false);
    }

    public PropKey asLegacy() {
        return new PropKey(key, zh, group, type, def, options, min, max, true);
    }

    /** 用户输入校验；抛 IllegalArgumentException 携带中文原因。 */
    public void validate(String value) {
        switch (type) {
            case BOOL -> {
                if (!value.equalsIgnoreCase("true") && !value.equalsIgnoreCase("false"))
                    throw new IllegalArgumentException(zh + " 必须是 true/false");
            }
            case INT -> {
                int v;
                try {
                    v = Integer.parseInt(value.trim());
                } catch (NumberFormatException e) {
                    throw new IllegalArgumentException(zh + " 必须是整数");
                }
                if (v < min || v > max)
                    throw new IllegalArgumentException("%s 需在 %d-%d 之间".formatted(zh, min, max));
            }
            case ENUM -> {
                if (!options.contains(value))
                    throw new IllegalArgumentException("%s 只能是 %s".formatted(zh, String.join("/", options)));
            }
            case STRING -> {
            }
        }
    }
}
