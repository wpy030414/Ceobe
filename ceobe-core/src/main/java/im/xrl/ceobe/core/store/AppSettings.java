package im.xrl.ceobe.core.store;

import im.xrl.ceobe.core.util.Json;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/** 应用级设置（`~/.ceobe/settings.json`）。 */
public final class AppSettings {

    /** 主题：light（默认）| dark。 */
    public String theme = "light";

    public static AppSettings load() {
        Path f = CeobeHome.root().resolve("settings.json");
        if (!Files.isRegularFile(f)) return new AppSettings();
        try {
            AppSettings s = Json.parse(Files.readString(f, StandardCharsets.UTF_8), AppSettings.class);
            return s != null ? s : new AppSettings();
        } catch (RuntimeException | java.io.IOException e) {
            return new AppSettings();
        }
    }

    public void save() {
        Path f = CeobeHome.root().resolve("settings.json");
        try {
            CeobeHome.ensure(CeobeHome.root());
            Files.writeString(f, Json.write(this), StandardCharsets.UTF_8);
        } catch (java.io.IOException ignored) {
            // 设置落盘失败不致命
        }
    }

    public boolean isDark() {
        return "dark".equalsIgnoreCase(theme);
    }
}
