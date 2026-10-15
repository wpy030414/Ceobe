package im.xrl.ceobe.core.mods;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Modrinth 环境枚举判定（回归：曾误把 version.environment 当对象读取，导致客户端专属模组被装入服务端）。 */
class ModrinthClientTest {

    @Test
    void 仅client_only视为客户端专属() {
        assertTrue(ModrinthClient.clientOnly("client_only"));
        assertFalse(ModrinthClient.clientOnly("server_only"));
        assertFalse(ModrinthClient.clientOnly("client_and_server"));
        assertFalse(ModrinthClient.clientOnly("client_or_server_prefers_both"));
        assertFalse(ModrinthClient.clientOnly("unknown"));
        assertFalse(ModrinthClient.clientOnly("optional"));
    }
}
