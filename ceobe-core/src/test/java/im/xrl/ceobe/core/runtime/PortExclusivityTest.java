package im.xrl.ceobe.core.runtime;

import im.xrl.ceobe.core.model.InstanceMeta;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.util.Json;
import im.xrl.ceobe.core.util.Ports;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.assertDoesNotThrow;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** 端口排他性：实例间不得共用端口（否则玩家的穿透地址会指向错误实例）。 */
class PortExclusivityTest {

    @TempDir
    static Path home;

    @BeforeAll
    static void useTempHome() {
        System.setProperty("ceobe.home", home.toString());
    }

    @AfterAll
    static void restore() {
        System.clearProperty("ceobe.home");
    }

    private static void writeInstance(String name, int port) throws Exception {
        Path dir = CeobeHome.instanceDir(name);
        Files.createDirectories(dir);
        InstanceMeta meta = new InstanceMeta(name, ServerType.PAPER, "26.3", null);
        meta.port = port;
        Files.writeString(dir.resolve("instance.json"), Json.write(meta), StandardCharsets.UTF_8);
    }

    @Test
    void 端口冲突被拒绝而自身端口不算冲突() throws Exception {
        writeInstance("alpha", 25565);
        InstanceManager manager = new InstanceManager();
        assertThrows(RuntimeException.class, () -> manager.assertPortExclusive("beta", 25565));
        assertDoesNotThrow(() -> manager.assertPortExclusive("alpha", 25565), "自身端口不应算冲突");
        assertDoesNotThrow(() -> manager.assertPortExclusive("beta", 25566));
    }

    @Test
    void 自动选取避开已登记端口且可独占绑定() throws Exception {
        writeInstance("gamma", 25570);
        InstanceManager manager = new InstanceManager();
        int free = manager.suggestFreePort(25570);
        assertNotEquals(25570, free, "应避开已登记实例占用的端口");
        assertTrue(Ports.isFree(free), "选出的端口应可独占绑定");
    }

    @Test
    void 非法端口视为不可用() {
        assertFalse(Ports.isFree(0));
        assertFalse(Ports.isFree(70000));
    }
}
