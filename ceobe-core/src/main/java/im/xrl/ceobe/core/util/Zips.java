package im.xrl.ceobe.core.util;

import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

/** ZIP 解压（含目录穿越防护）与递归删除。 */
public final class Zips {

    /** 解压到 dest（自动建目录）；拒绝逃逸出 dest 的条目。 */
    public static void extract(Path zip, Path dest) {
        try (ZipInputStream in = new ZipInputStream(Files.newInputStream(zip))) {
            ZipEntry entry;
            while ((entry = in.getNextEntry()) != null) {
                Path target = dest.resolve(entry.getName()).normalize();
                if (!target.startsWith(dest)) continue;   // 防目录穿越
                if (entry.isDirectory()) {
                    Files.createDirectories(target);
                } else {
                    Files.createDirectories(target.getParent());
                    Files.copy(in, target, StandardCopyOption.REPLACE_EXISTING);
                }
            }
        } catch (IOException e) {
            throw new CeobeException("解压失败: " + zip + " (" + e.getMessage() + ")", e);
        }
    }

    /** 递归复制目录内容到 dest（目录不存在则跳过），返回复制的文件数。 */
    public static int copyInto(Path source, Path dest) {
        if (!Files.isDirectory(source)) return 0;
        int[] count = {0};
        try (var walk = Files.walk(source)) {
            for (Path p : walk.toList()) {
                Path rel = source.relativize(p);
                Path target = dest.resolve(rel.toString()).normalize();
                if (!target.startsWith(dest)) continue;
                if (Files.isDirectory(p)) {
                    Files.createDirectories(target);
                } else {
                    Files.createDirectories(target.getParent());
                    Files.copy(p, target, StandardCopyOption.REPLACE_EXISTING);
                    count[0]++;
                }
            }
        } catch (IOException e) {
            throw new CeobeException("复制目录失败: " + source, e);
        }
        return count[0];
    }

    public static void deleteRecursively(Path root) {
        if (!Files.exists(root)) return;
        List<Path> paths = new ArrayList<>();
        try (var walk = Files.walk(root)) {
            walk.forEach(paths::add);
        } catch (IOException e) {
            return;
        }
        paths.sort(Comparator.reverseOrder());
        for (Path p : paths) {
            try {
                Files.deleteIfExists(p);
            } catch (IOException ignored) {
            }
        }
    }

    private Zips() {
    }
}
