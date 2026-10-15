package im.xrl.ceobe.gui.ui;

import atlantafx.base.theme.PrimerDark;
import atlantafx.base.theme.PrimerLight;
import javafx.application.Application;
import javafx.scene.Scene;
import javafx.scene.text.Font;

import java.io.InputStream;

/**
 * 主题：AtlantaFX 明/暗基座 + shadcn/ui 风格覆盖（`/im/xrl/ceobe/gui/shadcn.css`，token 驱动）。
 * <p>默认浅色；在场景根节点挂 {@code theme-light} / {@code theme-dark} 切换。
 */
public final class Theme {

    private static final String CSS = "/im/xrl/ceobe/gui/shadcn.css";

    private static String mode = "light";

    public static void apply(Scene scene) {
        apply(scene, mode);
    }

    /** 切换主题（light | dark）并应用到场景。 */
    public static void apply(Scene scene, String requested) {
        mode = "dark".equalsIgnoreCase(requested) ? "dark" : "light";
        Application.setUserAgentStylesheet(
                (mode.equals("dark") ? new PrimerDark() : new PrimerLight()).getUserAgentStylesheet());
        loadFont("Inter-Regular.ttf");
        loadFont("Inter-Medium.ttf");
        loadFont("Inter-SemiBold.ttf");

        var root = scene.getRoot();
        if (root != null) {
            root.getStyleClass().removeAll("theme-light", "theme-dark");
            root.getStyleClass().add("theme-" + mode);
        }
        var url = Theme.class.getResource(CSS);
        if (url != null && !scene.getStylesheets().contains(url.toExternalForm())) {
            scene.getStylesheets().add(url.toExternalForm());
        }
    }

    public static String mode() {
        return mode;
    }

    /** 首选 Inter，缺失则回退系统无衬线。 */
    public static String uiFontFamily() {
        return Font.getFamilies().stream()
                .filter(f -> f.startsWith("Inter"))
                .findFirst()
                .orElse("Segoe UI");
    }

    public static String monoFontFamily() {
        return Font.getFamilies().stream()
                .filter(f -> f.equals("JetBrains Mono") || f.equals("Cascadia Mono"))
                .findFirst()
                .orElse("Consolas");
    }

    private static void loadFont(String resource) {
        try (InputStream in = Theme.class.getResourceAsStream("/im/xrl/ceobe/gui/fonts/" + resource)) {
            if (in != null) Font.loadFont(in, 13);
        } catch (Exception ignored) {
            // 字体缺失不致命
        }
    }

    private Theme() {
    }
}
