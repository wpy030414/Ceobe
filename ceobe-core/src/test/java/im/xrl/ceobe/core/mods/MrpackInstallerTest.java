package im.xrl.ceobe.core.mods;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

/** 整合包索引解析（离线：构造合成 .mrpack）。 */
class MrpackInstallerTest {

    private static Path writeMrpack(Path dir, String indexJson) throws IOException {
        Path zip = dir.resolve("test.mrpack");
        try (ZipOutputStream out = new ZipOutputStream(Files.newOutputStream(zip))) {
            out.putNextEntry(new ZipEntry("modrinth.index.json"));
            out.write(indexJson.getBytes(StandardCharsets.UTF_8));
            out.closeEntry();
            out.putNextEntry(new ZipEntry("overrides/config/foo.toml"));
            out.write("x=1".getBytes(StandardCharsets.UTF_8));
            out.closeEntry();
        }
        return zip;
    }

    @Test
    void 解析fabric依赖(@TempDir Path dir) throws Exception {
        String json = """
                {"formatVersion":1,"name":"Test Pack","versionId":"1.2.0",
                 "dependencies":{"minecraft":"26.3","fabric-loader":"0.19.5"},
                 "files":[]}
                """;
        var idx = MrpackInstaller.readIndex(writeMrpack(dir, json));
        assertEquals("Test Pack", idx.name());
        assertEquals("1.2.0", idx.versionId());
        assertEquals("26.3", idx.mcVersion());
        assertEquals("fabric", idx.loader());
        assertEquals("0.19.5", idx.loaderVersion());
    }

    @Test
    void 解析neoforge依赖(@TempDir Path dir) throws Exception {
        String json = """
                {"formatVersion":1,"name":"N","versionId":"9",
                 "dependencies":{"minecraft":"26.2","neoforge":"26.2.0.93"},
                 "files":[]}
                """;
        var idx = MrpackInstaller.readIndex(writeMrpack(dir, json));
        assertEquals("neoforge", idx.loader());
        assertEquals("26.2.0.93", idx.loaderVersion());
    }

    @Test
    void 缺少索引时抛错(@TempDir Path dir) throws Exception {
        Path zip = dir.resolve("bad.mrpack");
        try (ZipOutputStream out = new ZipOutputStream(Files.newOutputStream(zip))) {
            out.putNextEntry(new ZipEntry("readme.txt"));
            out.write("x".getBytes(StandardCharsets.UTF_8));
            out.closeEntry();
        }
        assertThrows(RuntimeException.class, () -> MrpackInstaller.readIndex(zip));
    }
}
