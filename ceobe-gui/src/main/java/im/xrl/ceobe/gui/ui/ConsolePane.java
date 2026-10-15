package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.runtime.InstanceControl;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.CeobeException;
import javafx.application.Platform;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Node;
import javafx.scene.control.Button;
import javafx.scene.control.CheckBox;
import javafx.scene.control.Label;
import javafx.scene.control.TextArea;
import javafx.scene.control.TextField;
import javafx.scene.input.KeyCode;
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.VBox;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

/** 控制台页：实时日志 + 命令下发 + 启停控制。 */
public final class ConsolePane {

    private final InstanceManager manager;
    private final InstanceStatus status;
    private final TextArea log = new TextArea();
    private final TextField input = new TextField();
    private final Button send = new Button("发送");
    private final Button startBtn = new Button("启动");
    private final Button stopBtn = new Button("停止");
    private final CheckBox follow = new CheckBox("跟随");
    private final Label stateLabel = new Label();
    private final Button deleteBtn = new Button("删除");
    private final BorderPane root = new BorderPane();
    private final boolean hosted;
    private long logOffset;
    private boolean live;

    public ConsolePane(InstanceManager manager, InstanceStatus status) {
        this.manager = manager;
        this.status = status;
        this.hosted = manager.processOf(status.meta().name).isPresent();
        build();
        loadHistory();
        setLive(status.state().isLive());
    }

    public Node node() {
        return root;
    }

    private void build() {
        log.setEditable(false);
        log.setWrapText(false);
        log.getStyleClass().add("ceobe-log");
        log.setStyle("-fx-font-family: \"" + Theme.monoFontFamily() + "\";");

        input.setPromptText("输入服务端命令，如 say 你好 / op 玩家名");
        input.setOnAction(e -> doSend());
        HBox.setHgrow(input, Priority.ALWAYS);

        follow.setSelected(true);
        send.setOnAction(e -> doSend());
        send.getStyleClass().add("ceobe-primary");

        startBtn.setOnAction(e -> doStart());
        stopBtn.setOnAction(e -> doStop());
        startBtn.getStyleClass().add("ceobe-primary");

        deleteBtn.setOnAction(e -> doDelete());

        stateLabel.getStyleClass().addAll("ceobe-badge", "state-" + status.state().name());

        Label name = new Label(status.meta().name);
        name.getStyleClass().add("ceobe-h2");
        var spacer = new javafx.scene.layout.Region();
        HBox.setHgrow(spacer, Priority.ALWAYS);

        HBox toolbar = new HBox(8, name, stateLabel, spacer, follow, startBtn, stopBtn, deleteBtn);
        toolbar.setAlignment(Pos.CENTER_LEFT);
        toolbar.setPadding(new Insets(0, 0, 8, 0));

        HBox prompt = new HBox(8, input, send);
        prompt.setAlignment(Pos.CENTER_LEFT);
        prompt.setPadding(new Insets(8, 0, 0, 0));

        VBox body = new VBox(0, toolbar, log, prompt);
        body.setPadding(new Insets(12));
        VBox.setVgrow(log, Priority.ALWAYS);
        root.setCenter(body);
        if (input.getScene() != null) {
            input.getScene().getAccelerators();
        }
        log.setOnKeyPressed(e -> {
            if (e.getCode() == KeyCode.TAB) input.requestFocus();
        });
    }

    private void doStart() {
        Fx.background(() -> {
            manager.start(status.meta().name);
            return null;
        }, ok -> append("[ceobe] 已启动监督进程（前台由本界面托管）"), err -> append("[ceobe] 启动失败: " + err.getMessage()));
    }

    private void doStop() {
        Path dir = manager.dir(status.meta().name);
        Fx.background(() -> {
            if (manager.processOf(status.meta().name).isPresent()) {
                manager.stop(status.meta().name);
            } else if (InstanceControl.isAvailable(dir)) {
                InstanceControl.stop(dir);
            } else {
                throw new CeobeException("实例不受控，无法停止");
            }
            return null;
        }, ok -> append("[ceobe] 已请求停止"), err -> append("[ceobe] 停止失败: " + err.getMessage()));
    }

    private void doSend() {
        String cmd = input.getText();
        if (cmd == null || cmd.isBlank()) return;
        input.clear();
        Fx.background(() -> {
            manager.processOf(status.meta().name)
                    .filter(p -> p.isAlive())
                    .ifPresentOrElse(p -> p.sendCommand(cmd),
                            () -> InstanceControl.exec(manager.dir(status.meta().name), cmd));
            return null;
        }, ok -> {
        }, err -> append("[ceobe] 命令发送失败: " + err.getMessage()));
    }

    private void loadHistory() {
        Path dir = manager.dir(status.meta().name);
        List<String> lines = manager.processOf(status.meta().name)
                .map(p -> p.recentLogs(300))
                .orElseGet(() -> {
                    try {
                        Path file = dir.resolve("console.log");
                        if (!Files.isRegularFile(file)) return List.of();
                        List<String> all = Files.readAllLines(file, StandardCharsets.UTF_8);
                        return all.subList(Math.max(0, all.size() - 300), all.size());
                    } catch (IOException e) {
                        return List.of();
                    }
                });
        lines.forEach(this::append);
        try {
            Path file = dir.resolve("console.log");
            logOffset = Files.isRegularFile(file) ? Files.size(file) : 0;
        } catch (IOException e) {
            logOffset = 0;
        }
    }

    public void append(String line) {
        if (!Platform.isFxApplicationThread()) {
            Platform.runLater(() -> append(line));
            return;
        }
        log.appendText(line + System.lineSeparator());
        if (follow.isSelected()) log.positionCaret(log.getLength());
    }

    public void setLive(boolean live) {
        if (!Platform.isFxApplicationThread()) {
            Platform.runLater(() -> setLive(live));
            return;
        }
        this.live = live;
        input.setDisable(!live);
        send.setDisable(!live);
        startBtn.setDisable(live);
        stopBtn.setDisable(!live);
        deleteBtn.setDisable(live);
        stateLabel.setText(live ? "运行中" : "已停止");
        stateLabel.getStyleClass().removeIf(c -> c.startsWith("state-"));
        stateLabel.getStyleClass().add("state-" + (live ? "RUNNING" : "STOPPED"));
    }

    /** 删除实例：可选是否连同世界数据（默认仅移除注册）。 */
    private void doDelete() {
        var alert = new javafx.scene.control.Alert(javafx.scene.control.Alert.AlertType.CONFIRMATION);
        alert.setTitle("删除实例");
        alert.setHeaderText("删除实例「" + status.meta().name + "」？");
        alert.setContentText("选择「仅移除」保留世界数据文件；选择「连数据删除」将永久删除整个实例目录。");
        var keep = new javafx.scene.control.ButtonType("仅移除", javafx.scene.control.ButtonBar.ButtonData.NO);
        var purge = new javafx.scene.control.ButtonType("连数据删除", javafx.scene.control.ButtonBar.ButtonData.YES);
        alert.getButtonTypes().setAll(keep, purge, javafx.scene.control.ButtonType.CANCEL);
        var choice = alert.showAndWait();
        if (choice.isEmpty() || choice.get() == javafx.scene.control.ButtonType.CANCEL) return;
        boolean withData = choice.get() == purge;
        Fx.background(() -> {
            manager.remove(status.meta().name, withData);
            return null;
        }, ok -> {
            append("[ceobe] 实例已删除" + (withData ? "（含数据）" : "（世界数据保留）"));
            if (onDeleted != null) onDeleted.run();
        }, err -> append("[ceobe] 删除失败: " + err.getMessage()));
    }

    private Runnable onDeleted;

    public void setOnDeleted(Runnable callback) {
        this.onDeleted = callback;
    }

    /** 非本进程托管的实例：按文件偏移增量跟随 console.log。 */
    public void pollExternalLog() {
        if (hosted) return;
        try {
            Path file = manager.dir(status.meta().name).resolve("console.log");
            if (!Files.isRegularFile(file)) return;
            long size = Files.size(file);
            if (size < logOffset) logOffset = 0;   // 日志被轮转/重建
            if (size == logOffset) return;
            try (var channel = java.nio.channels.FileChannel.open(file)) {
                channel.position(logOffset);
                try (var reader = new java.io.BufferedReader(
                        java.nio.channels.Channels.newReader(channel, StandardCharsets.UTF_8))) {
                    String line;
                    while ((line = reader.readLine()) != null) append(line);
                }
            }
            logOffset = size;
        } catch (IOException ignored) {
        }
    }
}
