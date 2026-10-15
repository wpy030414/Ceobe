package im.xrl.ceobe.core.jdk;

import com.fasterxml.jackson.databind.JsonNode;
import im.xrl.ceobe.core.provider.MojangProvider;

import java.util.HashMap;
import java.util.Map;

/** 某 MC 版本所需的 Java 主版本（取自 piston-meta 的 {@code javaVersion.majorVersion}）。 */
public final class JavaRequirement {

    private static final Map<String, Integer> CACHE = new HashMap<>();

    /** 兜底：多数 1.20.5+ 版本需要 21。 */
    private static final int FALLBACK = 21;

    public static synchronized int requiredMajor(String mcVersion) {
        Integer cached = CACHE.get(mcVersion);
        if (cached != null) return cached;
        int major;
        try {
            JsonNode jv = MojangProvider.versionJson(mcVersion).path("javaVersion").path("majorVersion");
            major = jv.canConvertToInt() ? jv.asInt() : FALLBACK;
        } catch (RuntimeException e) {
            major = FALLBACK;   // 非 Mojang 版本或网络失败时不阻塞流程
        }
        CACHE.put(mcVersion, major);
        return major;
    }

    private JavaRequirement() {
    }
}
