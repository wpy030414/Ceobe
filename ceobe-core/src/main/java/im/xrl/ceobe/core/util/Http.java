package im.xrl.ceobe.core.util;

import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.time.Duration;
import java.util.HexFormat;

/** JDK HttpClient 封装：统一 User-Agent、超时、下载与校验。 */
public final class Http {
    public static final String USER_AGENT = "ceobe/0.1 (https://github.com/wpy030414/ceobe)";

    private static final HttpClient CLIENT = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(15))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();

    public static String get(String url) {
        try {
            HttpRequest req = HttpRequest.newBuilder(URI.create(url))
                    .timeout(Duration.ofSeconds(30))
                    .header("User-Agent", USER_AGENT)
                    .GET()
                    .build();
            HttpResponse<String> resp = CLIENT.send(req, HttpResponse.BodyHandlers.ofString());
            if (resp.statusCode() / 100 != 2) {
                throw CeobeException.of("请求失败 HTTP %d: %s", resp.statusCode(), trim(url));
            }
            return resp.body();
        } catch (CeobeException e) {
            throw e;
        } catch (Exception e) {
            throw new CeobeException("网络请求失败: " + trim(url) + " (" + e.getMessage() + ")", e);
        }
    }

    public static byte[] getBytes(String url) {
        try {
            HttpRequest req = HttpRequest.newBuilder(URI.create(url))
                    .timeout(Duration.ofMinutes(5))
                    .header("User-Agent", USER_AGENT)
                    .GET()
                    .build();
            HttpResponse<byte[]> resp = CLIENT.send(req, HttpResponse.BodyHandlers.ofByteArray());
            if (resp.statusCode() / 100 != 2) {
                throw CeobeException.of("下载失败 HTTP %d: %s", resp.statusCode(), trim(url));
            }
            return resp.body();
        } catch (CeobeException e) {
            throw e;
        } catch (Exception e) {
            throw new CeobeException("下载失败: " + trim(url) + " (" + e.getMessage() + ")", e);
        }
    }

    /** 进度回调：done 为已读字节数，total 为 Content-Length（未知为 -1）。 */
    @FunctionalInterface
    public interface Progress {
        void update(long done, long total);
    }

    /** 下载到 target（.part 原子改名），可选 sha1/sha256 校验。 */
    public static void download(String url, Path target, String sha1, String sha256, Progress progress) {
        Path part = target.resolveSibling(target.getFileName() + ".part");
        try {
            HttpRequest req = HttpRequest.newBuilder(URI.create(url))
                    .timeout(Duration.ofMinutes(30))
                    .header("User-Agent", USER_AGENT)
                    .GET()
                    .build();
            HttpResponse<InputStream> resp =
                    CLIENT.send(req, HttpResponse.BodyHandlers.ofInputStream());
            if (resp.statusCode() / 100 != 2) {
                throw CeobeException.of("下载失败 HTTP %d: %s", resp.statusCode(), trim(url));
            }
            long total = resp.headers().firstValueAsLong("Content-Length").orElse(-1);
            MessageDigest d1 = sha1 != null ? MessageDigest.getInstance("SHA-1") : null;
            MessageDigest d256 = sha256 != null ? MessageDigest.getInstance("SHA-256") : null;
            try (InputStream in = resp.body();
                 var out = java.nio.file.Files.newOutputStream(part)) {
                byte[] buf = new byte[64 * 1024];
                long done = 0;
                int n;
                while ((n = in.read(buf)) > 0) {
                    out.write(buf, 0, n);
                    if (d1 != null) d1.update(buf, 0, n);
                    if (d256 != null) d256.update(buf, 0, n);
                    done += n;
                    if (progress != null) progress.update(done, total);
                }
            }
            verify(part, "SHA-1", sha1, d1);
            verify(part, "SHA-256", sha256, d256);
            Files.move(part, target, StandardCopyOption.REPLACE_EXISTING);
        } catch (CeobeException e) {
            safeDelete(part);
            throw e;
        } catch (Exception e) {
            safeDelete(part);
            throw new CeobeException("下载中断: " + trim(url) + " (" + e.getMessage() + ")", e);
        }
    }

    public static String sha1(Path file) {
        return hash(file, "SHA-1");
    }

    public static String hash(Path file, String algo) {
        try {
            MessageDigest md = MessageDigest.getInstance(algo);
            try (InputStream in = Files.newInputStream(file)) {
                byte[] buf = new byte[64 * 1024];
                int n;
                while ((n = in.read(buf)) > 0) md.update(buf, 0, n);
            }
            return HexFormat.of().formatHex(md.digest());
        } catch (Exception e) {
            throw new CeobeException("计算哈希失败: " + file, e);
        }
    }

    private static void verify(Path file, String algo, String expected, MessageDigest digest) {
        if (expected == null) return;
        String actual = digest != null
                ? HexFormat.of().formatHex(digest.digest())
                : hash(file, algo);
        if (!expected.equalsIgnoreCase(actual)) {
            throw CeobeException.of("%s 校验失败\n期望: %s\n实际: %s", file.getFileName(), expected, actual);
        }
    }

    private static void safeDelete(Path p) {
        try {
            Files.deleteIfExists(p);
        } catch (IOException ignored) {
        }
    }

    private static String trim(String url) {
        return url.length() > 96 ? url.substring(0, 96) + "…" : url;
    }

    private Http() {
    }
}
