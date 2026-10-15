package im.xrl.ceobe.core.util;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** 版本号比较与归组（新纪年 26.x 与旧制 1.21.x 混合）。 */
class VersionsTest {

    @Test
    void 降序排列跨代际正确() {
        List<String> input = new ArrayList<>(
                List.of("1.20.1", "26.3", "1.21.11", "26.2", "1.21.4", "1.20.6"));
        input.sort(Versions.descending());
        assertEquals(List.of("26.3", "26.2", "1.21.11", "1.21.4", "1.20.6", "1.20.1"), input);
    }

    @Test
    void 段数不同时短版本更大() {
        List<String> input = new ArrayList<>(List.of("1.21.4", "1.21"));
        input.sort(Versions.descending());
        assertEquals(List.of("1.21.4", "1.21"), input);
    }

    @Test
    void 正式版判定排除预发布() {
        assertTrue(Versions.isRelease("26.3"));
        assertTrue(Versions.isRelease("1.21.11"));
        assertFalse(Versions.isRelease("26.3-rc-3"));
        assertFalse(Versions.isRelease("26.4-snapshot-4"));
        assertFalse(Versions.isRelease("1.21.11-pre5"));
    }

    @Test
    void 家族归组() {
        assertEquals("26.x", Versions.family("26.3"));
        assertEquals("1.21.x", Versions.family("1.21.11"));
        assertEquals("1.20.x", Versions.family("1.20.6"));
    }
}
