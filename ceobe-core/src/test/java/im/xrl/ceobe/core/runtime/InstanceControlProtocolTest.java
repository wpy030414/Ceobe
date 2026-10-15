package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.util.CeobeException;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** 控制通道应答解析（回归：应答前缀 "OK" 无尾随空格）。 */
class InstanceControlProtocolTest {

    @Test
    void 剥离无尾空格的前缀行() {
        assertEquals(List.of("RUNNING"),
                InstanceControl.parse(new ArrayList<>(List.of("OK", "RUNNING"))));
    }

    @Test
    void 剥离带正文的前缀行() {
        assertEquals(List.of("a", "b"),
                InstanceControl.parse(new ArrayList<>(List.of("OK extra", "a", "b"))));
    }

    @Test
    void 纯确认应答解析为空列表() {
        assertTrue(InstanceControl.parse(new ArrayList<>(List.of("OK"))).isEmpty());
    }

    @Test
    void ERR转异常并保留消息() {
        CeobeException ex = assertThrows(CeobeException.class,
                () -> InstanceControl.parse(new ArrayList<>(List.of("ERR 实例未运行"))));
        assertEquals("实例未运行", ex.getMessage());
    }

    @Test
    void 空应答不抛异常() {
        assertTrue(InstanceControl.parse(new ArrayList<>()).isEmpty());
    }

    @Test
    void 隧道地址应答形如地址加有效期() {
        List<String> parsed = InstanceControl.parse(new ArrayList<>(
                List.of("OK", "tcp://x.run.pinggy-free.link:35663", "2026-10-15T04:55:00Z")));
        assertEquals("tcp://x.run.pinggy-free.link:35663", parsed.get(0));
        assertEquals(2, parsed.size());
    }
}
