package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.config.PropKey;
import im.xrl.ceobe.core.config.PropSchema;
import im.xrl.ceobe.core.config.PropType;
import im.xrl.ceobe.core.config.ServerPropertiesFile;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.CeobeException;
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
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.ColumnConstraints;
import javafx.scene.layout.GridPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.VBox;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.Consumer;

/** 配置页：由 schema 生成的类型化表单，保存时原子写回 server.properties。 */
public final class ConfigPane {

    private final InstanceManager manager;
    private final InstanceStatus status;
    private final BorderPane root = new BorderPane();
    private final Map<String, Consumer<String>> writers = new LinkedHashMap<>();
    private final Label dirtyLabel = new Label();
    private final Button save = new Button("保存");
    private ServerPropertiesFile props;

    public ConfigPane(InstanceManager manager, InstanceStatus status) {
        this.manager = manager;
        this.status = status;
        build();
    }

    public Node node() {
        return root;
    }

    private void build() {
        var file = manager.dir(status.meta().name).resolve("server.properties");
        if (!java.nio.file.Files.isRegularFile(file)) {
            root.setCenter(new Label("尚无 server.properties（先启动一次服务器）"));
            return;
        }
        props = ServerPropertiesFile.load(file);

        VBox sections = new VBox(14);
        sections.setPadding(new Insets(16));
        for (String group : PropSchema.groups()) {
            VBox box = new VBox(6);
            Label header = new Label(group);
            header.getStyleClass().add("ceobe-h3");
            box.getChildren().add(header);

            GridPane grid = new GridPane();
            grid.setHgap(14);
            grid.setVgap(8);
            ColumnConstraints c1 = new ColumnConstraints();
            c1.setPrefWidth(230);
            ColumnConstraints c2 = new ColumnConstraints();
            c2.setHgrow(Priority.ALWAYS);
            c2.setFillWidth(true);
            grid.getColumnConstraints().addAll(c1, c2);

            int row = 0;
            for (PropKey key : PropSchema.all()) {
                if (key.legacy() || !group.equals(key.group())) continue;
                if (!props.contains(key.key())) continue; // 仅展示该实例实际存在的键
                Label label = new Label(key.zh());
                label.getStyleClass().add("ceobe-label");
                Label keyLabel = new Label(key.key());
                keyLabel.getStyleClass().add("ceobe-mono-subtle");
                VBox nameBox = new VBox(0, label, keyLabel);
                grid.add(nameBox, 0, row);
                grid.add(control(key, props.get(key.key())), 1, row);
                row++;
            }
            if (row > 0) {
                box.getChildren().add(card(grid));
                sections.getChildren().add(box);
            }
        }

        // 未知键（旧版本或插件写入）——原样展示，只读
        var unknown = props.values().entrySet().stream()
                .filter(e -> PropSchema.key(e.getKey()) == null)
                .toList();
        if (!unknown.isEmpty()) {
            Label header = new Label("其他（未收录键，只读）");
            header.getStyleClass().add("ceobe-h3");
            VBox box = new VBox(6, header);
            GridPane grid = new GridPane();
            grid.setHgap(14);
            grid.setVgap(6);
            int row = 0;
            for (var e : unknown) {
                grid.add(new Label(e.getKey()), 0, row);
                TextField tf = new TextField(e.getValue());
                tf.setEditable(false);
                grid.add(tf, 1, row);
                row++;
            }
            box.getChildren().add(card(grid));
            sections.getChildren().add(box);
        }

        ScrollPane scroll = new ScrollPane(sections);
        scroll.setFitToWidth(true);
        scroll.getStyleClass().add("ceobe-scroll");

        dirtyLabel.getStyleClass().add("ceobe-muted");
        save.getStyleClass().add("ceobe-primary");
        save.setDisable(true);
        save.setOnAction(e -> doSave());
        HBox bar = new HBox(10, dirtyLabel, new javafx.scene.layout.Region(), save);
        HBox.setHgrow(bar.getChildren().get(1), Priority.ALWAYS);
        bar.setAlignment(Pos.CENTER_LEFT);
        bar.setPadding(new Insets(10, 16, 10, 16));
        bar.getStyleClass().add("ceobe-actionbar");

        root.setCenter(scroll);
        root.setBottom(bar);
    }

    private Node card(GridPane grid) {
        VBox card = new VBox(grid);
        card.getStyleClass().add("ceobe-card");
        card.setPadding(new Insets(14, 16, 14, 16));
        return card;
    }

    private Node control(PropKey key, String value) {
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
                int current = parseIntSafe(initial, min, max);
                Spinner<Integer> spinner = new Spinner<>();
                spinner.setValueFactory(new SpinnerValueFactory.IntegerSpinnerValueFactory(min, max, current));
                spinner.setEditable(true);
                spinner.getStyleClass().add("ceobe-spinner");
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
                combo.getStyleClass().add("ceobe-combo");
                combo.valueProperty().addListener((o, a, b) -> {
                    if (b != null) markDirty(key, b);
                });
                return combo;
            }
            case STRING -> {
                TextField tf = new TextField(initial);
                tf.getStyleClass().add("ceobe-input");
                tf.textProperty().addListener((o, a, b) -> markDirty(key, b));
                return tf;
            }
        }
        return new Label(initial);
    }

    private void markDirty(PropKey key, String value) {
        pending.put(key.key(), value);
        dirtyLabel.setText("有 " + pending.size() + " 项未保存");
        save.setDisable(false);
    }

    private final Map<String, String> pending = new LinkedHashMap<>();

    private void doSave() {
        Fx.background(() -> {
            for (var e : pending.entrySet()) {
                props.set(e.getKey(), e.getValue());
            }
            props.save();
            return pending.size();
        }, count -> {
            pending.clear();
            dirtyLabel.setText("已保存 " + count + " 项" + (status.state().isLive() ? "（重启后生效）" : ""));
            save.setDisable(true);
        }, err -> dirtyLabel.setText("保存失败: " + err.getMessage()));
    }

    private static int parseIntSafe(String s, int min, int max) {
        try {
            int v = Integer.parseInt(s.trim());
            return Math.max(min, Math.min(max, v));
        } catch (NumberFormatException e) {
            return min;
        }
    }
}
