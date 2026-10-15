package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.config.PropKey;
import im.xrl.ceobe.core.config.PropSchema;
import im.xrl.ceobe.core.config.ServerPropertiesFile;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Node;
import javafx.scene.control.Button;
import javafx.scene.control.CheckBox;
import javafx.scene.control.ComboBox;
import javafx.scene.control.Label;
import javafx.scene.control.ScrollPane;
import javafx.scene.control.Spinner;
import javafx.scene.control.SpinnerValueFactory;
import javafx.scene.control.TextField;
import javafx.scene.layout.ColumnConstraints;
import javafx.scene.layout.GridPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.Region;
import javafx.scene.layout.VBox;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * 由 schema 生成 server.properties 表单的公共基类。
 * <p>脏标记 + 原子保存；保存时**重新加载**文件，只写入本面板记录的键，避免两个面板互相覆盖。
 */
abstract class PropsPaneBase {

    protected final InstanceManager manager;
    protected final InstanceStatus status;
    protected final Path propsFile;
    protected final Map<String, String> pending = new LinkedHashMap<>();
    protected final Label dirtyLabel = new Label();
    protected final Button save = new Button("保存");

    protected PropsPaneBase(InstanceManager manager, InstanceStatus status) {
        this.manager = manager;
        this.status = status;
        this.propsFile = manager.dir(status.meta().name).resolve("server.properties");
    }

    protected boolean propsExist() {
        return Files.isRegularFile(propsFile);
    }

    /** 该分组的卡片；实例中不存在任何该组键时返回 null。 */
    protected Node groupCard(String group) {
        ServerPropertiesFile props = ServerPropertiesFile.load(propsFile);
        GridPane grid = newGrid();
        int row = 0;
        for (PropKey key : PropSchema.all()) {
            if (key.legacy() || !group.equals(key.group())) continue;
            if (!props.contains(key.key())) continue;
            grid.add(nameBox(key.zh(), key.key()), 0, row);
            grid.add(control(key, props.get(key.key())), 1, row);
            row++;
        }
        if (row == 0) return null;
        Label sub = new Label(group);
        sub.getStyleClass().add("ceobe-h3");
        VBox card = new VBox(8, sub, grid);
        card.getStyleClass().add("ceobe-card");
        card.setPadding(new Insets(14, 16, 14, 16));
        return card;
    }

    /** 未收录键（旧版本/插件写入）只读卡片；无则返回 null。 */
    protected Node unknownCard() {
        ServerPropertiesFile props = ServerPropertiesFile.load(propsFile);
        var unknown = props.values().entrySet().stream()
                .filter(e -> PropSchema.key(e.getKey()) == null)
                .toList();
        if (unknown.isEmpty()) return null;
        Label header = new Label("其他（未收录键，只读）");
        header.getStyleClass().add("ceobe-h3");
        GridPane grid = newGrid();
        int row = 0;
        for (var e : unknown) {
            TextField tf = new TextField(e.getValue());
            tf.setEditable(false);
            grid.add(nameBox(e.getKey(), ""), 0, row);
            grid.add(tf, 1, row);
            row++;
        }
        VBox card = new VBox(grid);
        card.getStyleClass().add("ceobe-card");
        card.setPadding(new Insets(14, 16, 14, 16));
        return new VBox(8, header, card);
    }

    /** 底部保存条。 */
    protected Node actionBar() {
        dirtyLabel.getStyleClass().add("ceobe-muted");
        save.getStyleClass().add("ceobe-primary");
        save.setDisable(true);
        save.setOnAction(e -> doSave());
        Region spacer = new Region();
        HBox.setHgrow(spacer, Priority.ALWAYS);
        HBox bar = new HBox(10, dirtyLabel, spacer, save);
        bar.setAlignment(Pos.CENTER_LEFT);
        bar.setPadding(new Insets(10, 16, 10, 16));
        bar.getStyleClass().add("ceobe-actionbar");
        return bar;
    }

    protected ScrollPane scroll(Node body) {
        ScrollPane scroll = new ScrollPane(body);
        scroll.setFitToWidth(true);
        scroll.getStyleClass().add("ceobe-scroll");
        return scroll;
    }

    protected void doSave() {
        Fx.background(() -> {
            ServerPropertiesFile f = ServerPropertiesFile.load(propsFile);
            for (var e : pending.entrySet()) {
                f.set(e.getKey(), e.getValue());
            }
            f.save();
            return pending.size();
        }, count -> {
            pending.clear();
            dirtyLabel.setText("已保存 " + count + " 项" + (status.state().isLive() ? "（重启后生效）" : ""));
            save.setDisable(true);
        }, err -> dirtyLabel.setText("保存失败: " + err.getMessage()));
    }

    protected void markDirty(PropKey key, String value) {
        pending.put(key.key(), value);
        dirtyLabel.setText("有 " + pending.size() + " 项未保存");
        save.setDisable(false);
    }

    protected GridPane newGrid() {
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
        return grid;
    }

    protected VBox nameBox(String zh, String key) {
        Label label = new Label(zh);
        label.getStyleClass().add("ceobe-label");
        VBox box = new VBox(0, label);
        if (key != null && !key.isBlank()) {
            Label keyLabel = new Label(key);
            keyLabel.getStyleClass().add("ceobe-mono-subtle");
            box.getChildren().add(keyLabel);
        }
        return box;
    }

    protected static Spinner<Integer> intSpinner(int min, int max, int value, int step) {
        Spinner<Integer> spinner = new Spinner<>();
        spinner.setValueFactory(new SpinnerValueFactory.IntegerSpinnerValueFactory(
                min, max, Math.max(min, Math.min(max, value)), step));
        spinner.setEditable(true);
        spinner.getStyleClass().add("ceobe-spinner");
        return spinner;
    }

    protected Node control(PropKey key, String value) {
        String initial = value != null ? value : key.def();
        switch (key.type()) {
            case BOOL -> {
                CheckBox cb = new CheckBox();
                cb.setSelected(Boolean.parseBoolean(initial));
                cb.selectedProperty().addListener((o, a, b) -> markDirty(key, String.valueOf(b)));
                return cb;
            }
            case INT -> {
                int min = key.min() == Integer.MIN_VALUE ? 0 : key.min();
                int max = key.max() == Integer.MAX_VALUE ? 1_000_000 : key.max();
                Spinner<Integer> spinner = intSpinner(min, max, parseIntSafe(initial, min, max), 1);
                spinner.valueProperty().addListener((o, a, b) -> {
                    if (b != null) markDirty(key, String.valueOf(b));
                });
                Label range = new Label("范围 " + min + " – " + max);
                range.getStyleClass().add("ceobe-muted");
                HBox box = new HBox(10, spinner, range);
                box.setAlignment(Pos.CENTER_LEFT);
                return box;
            }
            case ENUM -> {
                ComboBox<String> combo = new ComboBox<>();
                combo.getItems().setAll(key.options());
                combo.setValue(key.options().contains(initial) ? initial : key.def());
                combo.valueProperty().addListener((o, a, b) -> {
                    if (b != null) markDirty(key, b);
                });
                return combo;
            }
            case STRING -> {
                TextField tf = new TextField(initial);
                tf.textProperty().addListener((o, a, b) -> markDirty(key, b));
                return tf;
            }
        }
        return new Label(initial);
    }

    protected static Label muted(String text) {
        Label label = new Label(text);
        label.getStyleClass().add("ceobe-muted");
        return label;
    }

    protected static int parseIntSafe(String s, int min, int max) {
        try {
            return Math.max(min, Math.min(max, Integer.parseInt(s.trim())));
        } catch (NumberFormatException e) {
            return min;
        }
    }
}
