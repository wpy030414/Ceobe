package wiregen;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.fasterxml.jackson.datatype.jsr310.JavaTimeModule;
import im.xrl.ceobe.core.store.AppSettings;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/**
 * 闸门 B 反向验证：**Java 读回自己写出的 JSON**，以及 **Java 读 C# 写出的 JSON**。
 *
 * 为什么需要：Gen.java 只证明"Java 能写"，没证明"Java 能读回"。
 * Jackson 默认开启 FAIL_ON_UNKNOWN_PROPERTIES，而 {@code AppSettings.isDark()} 是个
 * 没有 setter 的 getter —— 它会被序列化成 "dark" 字段，读回时就成了未知属性。
 * 这个组合意味着可能存在"Java 自己写的文件自己读不回"的 bug，必须实测而非推演。
 */
public final class RoundTrip {

    private static final ObjectMapper MAPPER = new ObjectMapper()
            .registerModule(new JavaTimeModule())
            .disable(SerializationFeature.WRITE_DATES_AS_TIMESTAMPS)
            .enable(SerializationFeature.INDENT_OUTPUT);

    public static void main(String[] args) throws Exception {
        Path dir = Path.of(args.length > 0 ? args[0] : ".");

        // ① Java 写 → Java 读（自往返）
        AppSettings s = new AppSettings();
        s.theme = "dark";
        String json = MAPPER.writeValueAsString(s);
        System.out.println("① Java 写出: " + json.replace("\n", "").replace("\r", ""));
        try {
            AppSettings back = MAPPER.readValue(json, AppSettings.class);
            System.out.println("   Java 读回: theme=" + back.theme + " isDark=" + back.isDark() + "  -> OK");
        } catch (Exception e) {
            System.out.println("   Java 读回: FAILED -> " + firstLine(e.getMessage()));
        }

        // ② C# 写（含 homePath 等新增字段）→ Java 读
        String csharpJson = Files.readString(dir.resolve("settings-from-csharp.json"), StandardCharsets.UTF_8);
        System.out.println("② C# 写出: " + csharpJson.replace("\n", "").replace("\r", ""));
        try {
            AppSettings back = MAPPER.readValue(csharpJson, AppSettings.class);
            System.out.println("   Java 读回: theme=" + back.theme + " isDark=" + back.isDark() + "  -> OK");
        } catch (Exception e) {
            System.out.println("   Java 读回: FAILED -> " + firstLine(e.getMessage()));
        }

        // ③ instance.json 往返（C# 写 → Java 读）
        for (String f : new String[]{"instance-full.json", "instance-minimal.json", "instance-neoforge.json"}) {
            String ij = Files.readString(dir.resolve(f), StandardCharsets.UTF_8);
            try {
                Object back = MAPPER.readValue(ij, Class.forName("im.xrl.ceobe.core.model.InstanceMeta"));
                System.out.println("③ " + f + " -> Java 读回 OK");
            } catch (Exception e) {
                System.out.println("③ " + f + " -> FAILED: " + firstLine(e.getMessage()));
            }
        }
    }

    private static String firstLine(String s) {
        if (s == null) return "(null)";
        int i = s.indexOf('\n');
        return i > 0 ? s.substring(0, i) : s;
    }
}
