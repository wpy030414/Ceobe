package wiregen;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.fasterxml.jackson.datatype.jsr310.JavaTimeModule;
import im.xrl.ceobe.core.model.InstanceMeta;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.mods.ModEntry;
import im.xrl.ceobe.core.store.AppSettings;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.List;

/**
 * 闸门 B 真值生成器：用**真实 Jackson + 真实 Java 模型类**产出 instance.json 样例，
 * 供 C# 侧 WireCompatibilityTest 作为 fixture 断言。
 *
 * 为什么需要它：C# 测试里如果用手打的 JSON 当"Java 侧输出"，那测试就只是在验证我自己的想象——
 * 手打错一个字段名，测试照样全绿，而真实双跑会静默丢数据。只有真 Jackson 的输出才算真值。
 *
 * ObjectMapper 配置与 {@code im.xrl.ceobe.core.util.Json} 逐行一致（JavaTimeModule +
 * 关闭 WRITE_DATES_AS_TIMESTAMPS + INDENT_OUTPUT），否则生成的形态与产品实际写盘不同。
 *
 * 运行方式（PowerShell，需本机 JDK；Jackson jar 从阿里云镜像下载）：
 * <pre>
 *   javac -encoding UTF-8 -d $out\classes -cp "$jars\*" `
 *     ceobe-core\src\main\java\im\xrl\ceobe\core\model\InstanceMeta.java `
 *     ceobe-core\src\main\java\im\xrl\ceobe\core\model\ServerType.java `
 *     ceobe-core\src\main\java\im\xrl\ceobe\core\mods\ModEntry.java `
 *     tools\wiregen\Gen.java
 *   java "-Dfile.encoding=UTF-8" -cp "$out\classes;$jars\*" wiregen.Gen tests\Ceobe.Core.Tests\fixtures
 * </pre>
 * 注意：本类**不**依赖 core 的 Json 工具类（避免把 CeobeException 拉进编译闭包），
 * 而是内联同一份 ObjectMapper 配置；若 Json.java 的配置变更，此处必须同步。
 */
public final class Gen {

    private static final ObjectMapper MAPPER = new ObjectMapper()
            .registerModule(new JavaTimeModule())
            .disable(SerializationFeature.WRITE_DATES_AS_TIMESTAMPS)
            .enable(SerializationFeature.INDENT_OUTPUT);

    public static void main(String[] args) throws Exception {
        Path outDir = Path.of(args.length > 0 ? args[0] : ".");
        Files.createDirectories(outDir);

        write(outDir, "instance-full.json", full());
        write(outDir, "instance-minimal.json", minimal());
        write(outDir, "instance-neoforge.json", neoforge());

        // settings.json：new 出来的默认 theme=light，改成 dark 以验证非默认值序列化
        AppSettings settings = new AppSettings();
        settings.theme = "dark";
        write(outDir, "settings-dark.json", settings);

        System.out.println("已生成 4 个 fixture 到 " + outDir.toAbsolutePath());
    }

    /** 完整形态：所有字段都有值，含一个 mod 条目。 */
    private static InstanceMeta full() {
        InstanceMeta m = new InstanceMeta("alpha", ServerType.FABRIC, "26.3", "0.16.9");
        m.memoryMb = 4096;
        m.jvmArgs = "-XX:+UseG1GC";
        m.port = 25570;
        m.javaHome = "C:\\jdk-25";
        m.launchJar = "server.jar";
        m.launchArgsFile = null;
        m.authlibInjectorUrl = null;
        m.createdAt = Instant.parse("2026-10-15T03:31:44.123Z");
        m.mods = List.of(new ModEntry(
                "PpRxcduo", "fabric-api", "Fabric API", "0.98.0+1.21.4",
                "fabric-api-0.98.0.jar", "0123456789abcdef0123456789abcdef01234567",
                "required", "required"));
        m.mods.get(0).installedAt = Instant.parse("2026-10-15T03:40:00Z");
        m.lastPinggyAddress = "tcp://x.run.pinggy-free.link:35663";
        m.lastCloudflareUrl = "https://abc-def-ghi.trycloudflare.com";
        return m;
    }

    /** 最小形态：老版本 instance.json（ADR-010/015 之前的字段缺失），验证 C# 侧宽容读回。 */
    private static InstanceMeta minimal() {
        InstanceMeta m = new InstanceMeta("old", ServerType.PAPER, "1.21.4", null);
        m.memoryMb = 2048;
        m.mods = List.of();
        // 固定时间戳：默认是 Instant.now()，会让 fixture 每次重生成都产生 diff 噪音，
        // 而这个字段在「缺字段宽容读回」这条用例里并不是被验证的对象
        m.createdAt = Instant.parse("2025-01-01T00:00:00Z");
        return m;
    }

    /** NeoForge 形态：launchArgsFile 非空（ADR-010 的参数文件启动）。 */
    private static InstanceMeta neoforge() {
        InstanceMeta m = new InstanceMeta("nf", ServerType.NEOFORGE, "1.21.4", "21.4.1");
        m.launchJar = "libraries/net/neoforged/neoforge/21.4.1/win_args.txt";
        m.launchArgsFile = "libraries/net/neoforged/neoforge/21.4.1/win_args.txt";
        m.authlibInjectorUrl = "https://auth.example.test/yggdrasil";
        m.mods = List.of();
        m.createdAt = Instant.parse("2025-06-01T12:00:00Z"); // 固定，理由同 minimal()
        return m;
    }

    private static void write(Path dir, String name, Object value) throws Exception {
        Path f = dir.resolve(name);
        Files.writeString(f, MAPPER.writeValueAsString(value), StandardCharsets.UTF_8);
        System.out.println("  " + name + " (" + Files.size(f) + " 字节)");
    }
}
