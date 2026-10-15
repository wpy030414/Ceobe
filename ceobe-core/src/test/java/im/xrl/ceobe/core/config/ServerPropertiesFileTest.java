package im.xrl.ceobe.core.config;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** server.properties 保序保注释读写与 schema 校验。 */
class ServerPropertiesFileTest {

    /** vanilla 26.3 实测生成样本（节选关键行 + 注释 + 转义）。 */
    private static final String SAMPLE = """
            #Minecraft server properties
            #Thu Oct 15 11:31:44 CST 2026
            accepts-transfers=false
            allow-flight=false
            difficulty=easy
            level-type=minecraft\\:normal
            max-players=20
            motd=A Minecraft Server
            online-mode=true
            server-port=25565
            view-distance=10
            """;

    @Test
    void 读取语义值并还原转义(@TempDir Path dir) throws Exception {
        Path f = dir.resolve("server.properties");
        Files.writeString(f, SAMPLE, StandardCharsets.UTF_8);
        ServerPropertiesFile props = ServerPropertiesFile.load(f);

        assertEquals("easy", props.get("difficulty"));
        assertEquals("minecraft:normal", props.get("level-type"), "冒号转义应被还原");
        assertEquals("A Minecraft Server", props.get("motd"));
    }

    @Test
    void 仅改动目标行其余字节不变(@TempDir Path dir) throws Exception {
        Path f = dir.resolve("server.properties");
        Files.writeString(f, SAMPLE, StandardCharsets.UTF_8);
        ServerPropertiesFile props = ServerPropertiesFile.load(f);
        props.set("max-players", "30");
        props.save();

        List<String> after = Files.readAllLines(f, StandardCharsets.UTF_8);
        assertTrue(after.contains("max-players=30"));
        assertTrue(after.contains("#Thu Oct 15 11:31:44 CST 2026"), "注释必须保留");
        assertTrue(after.contains("level-type=minecraft\\:normal"), "其他行字节不变");
        assertEquals(after.size(), SAMPLE.lines().count(), "行数不变（原地替换）");
    }

    @Test
    void 新增键追加到末尾(@TempDir Path dir) throws Exception {
        Path f = dir.resolve("server.properties");
        Files.writeString(f, SAMPLE, StandardCharsets.UTF_8);
        ServerPropertiesFile props = ServerPropertiesFile.load(f);
        props.set("enable-rcon", "true");
        props.save();

        List<String> after = Files.readAllLines(f, StandardCharsets.UTF_8);
        assertEquals("enable-rcon=true", after.get(after.size() - 1));
    }

    @Test
    void schema校验拒绝非法值(@TempDir Path dir) {
        Path f = dir.resolve("server.properties");
        ServerPropertiesFile props = ServerPropertiesFile.load(f);
        assertThrows(RuntimeException.class, () -> props.set("view-distance", "99"));
        assertThrows(RuntimeException.class, () -> props.set("gamemode", "fly"));
        assertThrows(RuntimeException.class, () -> props.set("max-players", "abc"));
    }

    @Test
    void 模板覆盖全部非legacy键() {
        String template = ServerPropertiesFile.template();
        for (PropKey k : PropSchema.all()) {
            if (k.legacy()) continue;
            assertTrue(template.contains(k.key() + "="), "模板缺键: " + k.key());
        }
        assertFalse(template.contains("spawn-monsters"), "legacy 键不应进模板");
    }
}
