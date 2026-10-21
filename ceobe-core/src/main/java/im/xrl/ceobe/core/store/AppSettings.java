package im.xrl.ceobe.core.store;

import com.fasterxml.jackson.annotation.JsonIgnore;
import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import im.xrl.ceobe.core.util.Json;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/** 应用级设置（`~/.ceobe/settings.json`）。 */
// ignoreUnknown：C# 重构版（ADR-019）在 settings.json 里新增了 homePath 等字段，
// 迁移期两侧要能互读同一份文件。Jackson 默认开启 FAIL_ON_UNKNOWN_PROPERTIES，
// 不关掉的话 load() 的 catch 会静默回默认值（用户设置凭空丢失，且无任何报错）。
@JsonIgnoreProperties(ignoreUnknown = true)
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

    // JsonIgnore 是必需的：Jackson 把 isDark() 当 getter 序列化成 "dark" 字段，
    // 而类里没有 dark 字段，导致「Java 写出的 settings.json 自己读不回来」——
    // 实测报 Unrecognized field "dark"，被 load() 的 catch 吞掉后深色主题静默丢失。
    // 闸门 B 真值验证（tools/wiregen/RoundTrip.java）抓到此问题。
    @JsonIgnore
    public boolean isDark() {
        return "dark".equalsIgnoreCase(theme);
    }
}
