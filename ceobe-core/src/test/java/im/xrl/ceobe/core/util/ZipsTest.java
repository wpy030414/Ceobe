package im.xrl.ceobe.core.util;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** ZIP 解压：内容落地 + 目录穿越防护 + 递归复制。 */
class ZipsTest {

    @Test
    void 解压并拒绝目录穿越(@TempDir Path dir) throws Exception {
        Path zip = dir.resolve("a.zip");
        try (ZipOutputStream out = new ZipOutputStream(Files.newOutputStream(zip))) {
            out.putNextEntry(new ZipEntry("ok/file.txt"));
            out.write("hi".getBytes());
            out.closeEntry();
            out.putNextEntry(new ZipEntry("../escape.txt"));
            out.write("bad".getBytes());
            out.closeEntry();
        }
        Path dest = dir.resolve("out");
        Files.createDirectories(dest);
        Zips.extract(zip, dest);

        assertTrue(Files.isRegularFile(dest.resolve("ok/file.txt")));
        assertFalse(Files.exists(dir.resolve("escape.txt")), "不得写出到 dest 之外");
    }

    @Test
    void 递归复制到位(@TempDir Path dir) throws Exception {
        Path src = dir.resolve("src");
        Files.createDirectories(src.resolve("sub"));
        Files.writeString(src.resolve("sub/x.txt"), "v");
        Path dst = dir.resolve("dst");
        Files.createDirectories(dst);

        int n = Zips.copyInto(src, dst);
        assertEquals(1, n);
        assertEquals("v", Files.readString(dst.resolve("sub/x.txt")));
        assertEquals(0, Zips.copyInto(dir.resolve("missing"), dst), "源不存在返回 0");
    }
}
