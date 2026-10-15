package im.xrl.ceobe.gui.ui;

import atlantafx.base.theme.PrimerDark;
import javafx.scene.Scene;
import javafx.scene.text.Font;
import javafx.application.Application;

import java.io.InputStream;

/**
 * 主题：AtlantaFX PrimerDark 为基座 + shadcn/ui 风格覆盖（zinc 色板、8px 圆角、1px hairline）。
 * <p>观感变量集中于 shadcn.css 的 {@code -ceobe-*} 与 AtlantaFX 的 {@code -color-*} 覆盖。
 */
public final class Theme {

    private static final String CSS = "/im/xrl/ceobe/gui/shadcn.css";

    public static void apply(Scene scene) {
        Application.setUserAgentStylesheet(new PrimerDark().getUserAgentStylesheet());
        loadFont("Inter-Regular.ttf");
        loadFont("Inter-Medium.ttf");
        loadFont("Inter-SemiBold.ttf");
        var url = Theme.class.getResource(CSS);
        if (url != null) {
            scene.getStylesheets().add(url.toExternalForm());
        }
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
