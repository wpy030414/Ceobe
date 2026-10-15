package im.xrl.ceobe.core.config;

import im.xrl.ceobe.core.util.CeobeException;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * server.properties 读写：保序、保注释、保转义。
 * <p>读：逐行保留原文；语义值按 Java Properties 转义规则反解。
 * <p>写：仅重写被 set 的键所在行（原地替换），新增键追加至末尾；其余字节原样。
 */
public final class ServerPropertiesFile {

    private final Path file;
    private final List<String> lines = new ArrayList<>();
    private final Map<String, String> values = new LinkedHashMap<>();
    private final Map<String, Integer> lineIndexOf = new LinkedHashMap<>();
    private boolean dirty;

    private ServerPropertiesFile(Path file) {
        this.file = file;
    }

    public static ServerPropertiesFile load(Path file) {
        ServerPropertiesFile p = new ServerPropertiesFile(file);
        if (!Files.isRegularFile(file)) return p;
        try {
            p.lines.addAll(Files.readAllLines(file, StandardCharsets.UTF_8));
        } catch (IOException e) {
            throw new CeobeException("读取配置失败: " + file, e);
        }
        for (int i = 0; i < p.lines.size(); i++) {
            String raw = p.lines.get(i);
            String trimmed = raw.stripLeading();
            if (trimmed.isEmpty() || trimmed.startsWith("#") || trimmed.startsWith("!")) continue;
            int sep = indexOfSeparator(raw);
            if (sep < 0) continue;
            String key = raw.substring(0, sep).trim();
            String value = raw.substring(sep + 1);
            p.values.put(key, unescape(value));
            p.lineIndexOf.put(key, i);
        }
        return p;
    }

    public String get(String key) {
        return values.get(key);
    }

    public String getOrDefault(String key) {
        String v = values.get(key);
        if (v != null) return v;
        PropKey schema = PropSchema.key(key);
        return schema != null ? schema.def() : "";
    }

    public boolean contains(String key) {
        return values.containsKey(key);
    }

    public Map<String, String> values() {
        return Map.copyOf(values);
    }

    public void set(String key, String value) {
        PropKey schema = PropSchema.key(key);
        if (schema != null) {
            try {
                schema.validate(value);
            } catch (IllegalArgumentException e) {
                throw new CeobeException(e.getMessage());
            }
        }
        String encoded = escape(value);
        values.put(key, value);
        Integer idx = lineIndexOf.get(key);
        if (idx != null) {
            lines.set(idx, key + "=" + encoded);
        } else {
            lines.add(key + "=" + encoded);
            lineIndexOf.put(key, lines.size() - 1);
        }
        dirty = true;
    }

    public boolean isDirty() {
        return dirty;
    }

    /** 原子写回（temp + rename）。 */
    public void save() {
        if (!dirty) return;
        try {
            Path tmp = file.resolveSibling(file.getFileName() + ".tmp");
            Files.write(tmp, lines, StandardCharsets.UTF_8);
            Files.move(tmp, file, StandardCopyOption.REPLACE_EXISTING);
            dirty = false;
        } catch (IOException e) {
            throw new CeobeException("写入配置失败: " + file, e);
        }
    }

    public static String template() {
        StringBuilder sb = new StringBuilder("#Minecraft server properties\n");
        for (PropKey k : PropSchema.all()) {
            if (k.legacy()) continue;
            sb.append(k.key()).append('=').append(escape(k.def())).append('\n');
        }
        return sb.toString();
    }

    // —— 转义（对齐 Java Properties 语义）——

    private static int indexOfSeparator(String line) {
        for (int i = 0; i < line.length(); i++) {
            char c = line.charAt(i);
            if (c == '\\') {
                i++;
                continue;
            }
            if (c == '=' || c == ':') return i;
        }
        return -1;
    }

    static String unescape(String s) {
        StringBuilder sb = new StringBuilder(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c != '\\' || i + 1 >= s.length()) {
                sb.append(c);
                continue;
            }
            char n = s.charAt(++i);
            switch (n) {
                case 't' -> sb.append('\t');
                case 'n' -> sb.append('\n');
                case 'r' -> sb.append('\r');
                case 'f' -> sb.append('\f');
                case 'u' -> {
                    if (i + 4 < s.length()) {
                        sb.append((char) Integer.parseInt(s.substring(i + 1, i + 5), 16));
                        i += 4;
                    }
                }
                default -> sb.append(n);
            }
        }
        return sb.toString();
    }

    static String escape(String s) {
        StringBuilder sb = new StringBuilder(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '\\' -> sb.append("\\\\");
                case ':' -> sb.append("\\:");
                case '=' -> sb.append("\\=");
                case '#' -> sb.append("\\#");
                case '!' -> sb.append("\\!");
                case '\t' -> sb.append("\\t");
                case '\n' -> sb.append("\\n");
                case '\r' -> sb.append("\\r");
                default -> sb.append(c);
            }
        }
        return sb.toString();
    }
}
