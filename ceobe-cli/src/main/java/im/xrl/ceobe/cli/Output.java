package im.xrl.ceobe.cli;

import im.xrl.ceobe.core.util.Json;

import java.util.List;
import java.util.Map;

/** CLI 输出：默认人类表格，`--format json` 输出机器可读。 */
public final class Output {

    public static boolean json;

    public static void json(Object value) {
        System.out.println(Json.write(value));
    }

    /** 渲染两列表格，自动按显示宽度对齐（中日韩字符占 2 列）。 */
    public static void table(String[] headers, List<String[]> rows) {
        int cols = headers.length;
        int[] widths = new int[cols];
        for (int i = 0; i < cols; i++) widths[i] = displayWidth(headers[i]);
        for (String[] row : rows) {
            for (int i = 0; i < cols; i++) {
                widths[i] = Math.max(widths[i], displayWidth(row[i] == null ? "" : row[i]));
            }
        }
        StringBuilder head = new StringBuilder();
        for (int i = 0; i < cols; i++) head.append(pad(headers[i], widths[i])).append(i < cols - 1 ? "  " : "");
        System.out.println(head.toString().stripTrailing());
        StringBuilder sep = new StringBuilder();
        for (int i = 0; i < cols; i++) {
            sep.append("-".repeat(widths[i])).append(i < cols - 1 ? "  " : "");
        }
        System.out.println(sep.toString().stripTrailing());
        for (String[] row : rows) {
            StringBuilder line = new StringBuilder();
            for (int i = 0; i < cols; i++) {
                line.append(pad(row[i] == null ? "" : row[i], widths[i]))
                        .append(i < cols - 1 ? "  " : "");
            }
            System.out.println(line.toString().stripTrailing());
        }
    }

    public static void info(String fmt, Object... args) {
        if (!json) System.out.println(fmt.formatted(args));
    }

    public static void error(String fmt, Object... args) {
        System.err.println("错误: " + fmt.formatted(args));
    }

    public static Map<String, Object> map(Object... kv) {
        Map<String, Object> m = new java.util.LinkedHashMap<>();
        for (int i = 0; i + 1 < kv.length; i += 2) {
            m.put(String.valueOf(kv[i]), kv[i + 1]);
        }
        return m;
    }

    static int displayWidth(String s) {
        int w = 0;
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            w += isWide(c) ? 2 : 1;
        }
        return w;
    }

    private static boolean isWide(char c) {
        return (c >= 0x1100 && c <= 0x115F)
                || (c >= 0x2E80 && c <= 0xA4CF)
                || (c >= 0xAC00 && c <= 0xD7A3)
                || (c >= 0xF900 && c <= 0xFAFF)
                || (c >= 0xFE30 && c <= 0xFE6F)
                || (c >= 0xFF00 && c <= 0xFF60)
                || (c >= 0xFFE0 && c <= 0xFFE6);
    }

    private static String pad(String s, int width) {
        int diff = width - displayWidth(s);
        return diff <= 0 ? s : s + " ".repeat(diff);
    }

    private Output() {
    }
}
