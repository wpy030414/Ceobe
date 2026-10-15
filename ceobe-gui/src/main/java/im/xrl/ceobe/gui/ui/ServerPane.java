package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.model.InstanceMeta;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Node;
import javafx.scene.control.Button;
import javafx.scene.control.Label;
import javafx.scene.control.ScrollPane;
import javafx.scene.control.Spinner;
import javafx.scene.control.SpinnerValueFactory;
import javafx.scene.control.TextField;
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.ColumnConstraints;
import javafx.scene.layout.GridPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.Region;
import javafx.scene.layout.VBox;

import java.util.function.Consumer;

/**
 * 「服务器配置」页：实例级设置（不写 server.properties）——
 * 名称 / 内存上限 / 端口 / JVM 参数 / authlib-injector，落 `instance.json`。
 * <p>保存按钮与「游戏配置」页一致，统一在右下角。
 */
public final class ServerPane {

    private final InstanceManager manager;
    private final InstanceStatus status;
    private final Consumer<String> onRenamed;
    private final BorderPane root = new BorderPane();

    public ServerPane(InstanceManager manager, InstanceStatus status, Consumer<String> onRenamed) {
        this.manager = manager;
        this.status = status;
        this.onRenamed = onRenamed;
        build();
    }

    public Node node() {
        return root;
    }

    private void build() {
        InstanceMeta meta = manager.meta(status.meta().name);

        Label header = new Label("服务器配置");
        header.getStyleClass().add("ceobe-h3");

        GridPane grid = new GridPane();
        grid.setHgap(14);
        grid.setVgap(10);
        ColumnConstraints c1 = new ColumnConstraints();
        c1.setPrefWidth(230);
        c1.setMinWidth(180);
        ColumnConstraints c2 = new ColumnConstraints();
        c2.setHgrow(Priority.ALWAYS);
        c2.setFillWidth(true);
        grid.getColumnConstraints().addAll(c1, c2);

        TextField nameField = new TextField(meta.name);
        Spinner<Integer> memSpinner = intSpinner(512, 65536, meta.memoryMb, 512);
        Spinner<Integer> portSpinner = intSpinner(1, 65535, meta.port, 1);
        TextField jvmField = new TextField(meta.jvmArgs == null ? "" : meta.jvmArgs);
        jvmField.setPromptText("附加 JVM 参数，如 -XX:+UseZGC（可留空）");
        TextField authlibField = new TextField(meta.authlibInjectorUrl == null ? "" : meta.authlibInjectorUrl);
        authlibField.setPromptText("如 https://littleskin.cn/api/yggdrasil（留空=关闭）");

        int r = 0;
        grid.add(nameBox("实例名", "name"), 0, r); grid.add(nameField, 1, r++);
        grid.add(nameBox("内存上限 (MB)", "memoryMb"), 0, r); grid.add(memSpinner, 1, r++);
        grid.add(nameBox("服务器端口", "port"), 0, r); grid.add(portSpinner, 1, r++);
        grid.add(nameBox("JVM 参数", "jvmArgs"), 0, r); grid.add(jvmField, 1, r++);
        grid.add(nameBox("authlib-injector", "authlibInjectorUrl"), 0, r); grid.add(authlibField, 1, r++);

        Label hint = muted("authlib-injector：填入 Yggdrasil API 地址以启用三方鉴权；启用后会自动关闭正版验证"
                + "（online-mode）。改端口会同步 server.properties 的 server-port/query.port。");
        hint.setWrapText(true);
        hint.setMaxWidth(660);

        VBox card = new VBox(10, grid, hint);
        card.getStyleClass().add("ceobe-card");
        card.setPadding(new Insets(14, 16, 14, 16));

        VBox sections = new VBox(12, header, card);
        sections.setPadding(new Insets(16));
        ScrollPane scroll = new ScrollPane(sections);
        scroll.setFitToWidth(true);
        scroll.getStyleClass().add("ceobe-scroll");

        // —— 右下角统一「保存」——
        Label result = new Label();
        result.getStyleClass().add("ceobe-muted");
        Button save = new Button("保存");
        save.getStyleClass().add("ceobe-primary");
        boolean live = status.state().isLive();
        save.setDisable(live);
        if (live) result.setText("实例运行中；停止后方可修改内存/端口等");

        save.setOnAction(e -> {
            String newName = nameField.getText().trim();
            int mem = memSpinner.getValue();
            int port = portSpinner.getValue();
            String jvm = jvmField.getText();
            String authlib = authlibField.getText();
            Fx.background(() -> {
                if (!newName.equals(meta.name)) manager.rename(meta.name, newName);
                manager.updateInstance(newName, mem, port, jvm, authlib);
                return newName;
            }, savedName -> {
                result.setText("已保存服务器配置" + (live ? "" : "（内存/端口等重启后生效）"));
                if (!savedName.equals(status.meta().name)) {
                    onRenamed.accept(savedName);
                }
            }, err -> result.setText("保存失败: " + err.getMessage()));
        });

        Region spacer = new Region();
        HBox.setHgrow(spacer, Priority.ALWAYS);
        HBox bar = new HBox(10, result, spacer, save);
        bar.setAlignment(Pos.CENTER_LEFT);
        bar.setPadding(new Insets(10, 16, 10, 16));
        bar.getStyleClass().add("ceobe-actionbar");

        root.setCenter(scroll);
        root.setBottom(bar);
    }

    private static Spinner<Integer> intSpinner(int min, int max, int value, int step) {
        Spinner<Integer> spinner = new Spinner<>();
        spinner.setValueFactory(new SpinnerValueFactory.IntegerSpinnerValueFactory(
                min, max, Math.max(min, Math.min(max, value)), step));
        spinner.setEditable(true);
        spinner.getStyleClass().add("ceobe-spinner");
        return spinner;
    }

    private static VBox nameBox(String zh, String key) {
        Label label = new Label(zh);
        label.getStyleClass().add("ceobe-label");
        Label keyLabel = new Label(key);
        keyLabel.getStyleClass().add("ceobe-mono-subtle");
        return new VBox(0, label, keyLabel);
    }

    private static Label muted(String text) {
        Label label = new Label(text);
        label.getStyleClass().add("ceobe-muted");
        return label;
    }
}
