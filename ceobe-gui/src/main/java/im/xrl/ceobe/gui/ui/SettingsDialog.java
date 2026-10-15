package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.store.AppSettings;
import javafx.geometry.Insets;
import javafx.scene.Scene;
import javafx.scene.control.ButtonType;
import javafx.scene.control.Dialog;
import javafx.scene.control.Label;
import javafx.scene.control.RadioButton;
import javafx.scene.control.ToggleGroup;
import javafx.scene.layout.HBox;
import javafx.scene.layout.VBox;
import javafx.stage.Window;

/** 设置对话框：当前仅暴露「主题」一项（浅色默认 / 深色）。 */
public final class SettingsDialog {

    public static void show(Window owner, Scene scene) {
        Dialog<ButtonType> dialog = new Dialog<>();
        dialog.initOwner(owner);
        dialog.setTitle("设置");
        dialog.setHeaderText("应用设置");
        // Dialog 自建场景：补主题类以取到 token，并在切换时同步
        dialog.getDialogPane().getStyleClass().add("theme-" + Theme.mode());

        Label themeLabel = new Label("主题");
        themeLabel.getStyleClass().add("ceobe-h3");
        RadioButton light = new RadioButton("浅色（默认）");
        RadioButton dark = new RadioButton("深色");
        ToggleGroup group = new ToggleGroup();
        light.setToggleGroup(group);
        dark.setToggleGroup(group);
        boolean isDark = "dark".equals(Theme.mode());
        dark.setSelected(isDark);
        light.setSelected(!isDark);

        HBox options = new HBox(18, light, dark);
        VBox content = new VBox(8, themeLabel, options);
        content.setPadding(new Insets(12, 4, 4, 4));
        dialog.getDialogPane().setContent(content);
        dialog.getDialogPane().getButtonTypes().add(ButtonType.CLOSE);

        // 即时生效并持久化
        group.selectedToggleProperty().addListener((o, a, b) -> {
            if (b == null) return;   // 不允许全部取消
            String mode = dark.isSelected() ? "dark" : "light";
            Theme.apply(scene, mode);
            dialog.getDialogPane().getStyleClass().removeAll("theme-light", "theme-dark");
            dialog.getDialogPane().getStyleClass().add("theme-" + mode);
            AppSettings settings = AppSettings.load();
            settings.theme = mode;
            settings.save();
        });

        dialog.showAndWait();
    }

    private SettingsDialog() {
    }
}
