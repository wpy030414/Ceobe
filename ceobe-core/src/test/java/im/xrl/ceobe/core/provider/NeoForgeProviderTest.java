package im.xrl.ceobe.core.provider;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

/** NeoForge 版本映射：归一化前缀（1.x 去前导 "1."，26.x 保留）。 */
class NeoForgeProviderTest {

    @Test
    void 版本前缀归一化() {
        assertEquals("26.3", NeoForgeProvider.prefix("26.3"));
        assertEquals("26.1.2", NeoForgeProvider.prefix("26.1.2"));
        assertEquals("21.8", NeoForgeProvider.prefix("1.21.8"));
        assertEquals("20.4", NeoForgeProvider.prefix("1.20.4"));
        // 1.0 → "0"：主版本 <20 会被 supportedPrefix 过滤，避免伪匹配
        assertEquals("0", NeoForgeProvider.prefix("1.0"));
    }
}
