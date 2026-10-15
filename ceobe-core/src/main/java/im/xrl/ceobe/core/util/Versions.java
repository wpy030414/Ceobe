package im.xrl.ceobe.core.util;

import java.util.Comparator;

/** MC 版本号比较与归组：支持新纪年制（26.3）与旧制（1.21.11）。 */
public final class Versions {
    private static final Comparator<String> CMP = (a, b) -> {
        int[] x = segments(a), y = segments(b);
        for (int i = 0; i < Math.max(x.length, y.length); i++) {
            int va = i < x.length ? x[i] : 0;
            int vb = i < y.length ? y[i] : 0;
            if (va != vb) return Integer.compare(vb, va); // 降序：新版本在前
        }
        return 0;
    };

    public static Comparator<String> descending() {
        return CMP;
    }

    public static boolean isRelease(String v) {
        // 26.3 / 1.21.4 纯数字点分视为正式版；含 -rc/-pre/-snapshot 的排除
        return v.matches("\\d+(\\.\\d+)+");
    }

    /** 家族分组键：26.3→"26.x"，1.21.4→"1.21.x"。 */
    public static String family(String v) {
        String[] parts = v.split("\\.");
        if (parts.length <= 1) return v;
        return parts[0] + (parts.length == 2 ? ".x" : "." + parts[1] + ".x");
    }

    private static int[] segments(String v) {
        String body = v.split("[-+]", 2)[0];
        String[] parts = body.split("\\.");
        int[] out = new int[parts.length];
        for (int i = 0; i < parts.length; i++) {
            try {
                out[i] = Integer.parseInt(parts[i]);
            } catch (NumberFormatException e) {
                out[i] = 0;
            }
        }
        return out;
    }

    private Versions() {
    }
}
