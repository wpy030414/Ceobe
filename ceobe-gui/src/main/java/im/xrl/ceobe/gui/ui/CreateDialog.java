package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.jdk.JavaEnv;
import im.xrl.ceobe.core.jdk.JavaRequirement;
import im.xrl.ceobe.core.jdk.JdkInstaller;
import im.xrl.ceobe.core.jdk.JdkStore;
import im.xrl.ceobe.core.model.InstanceSpec;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.provider.ProviderRegistry;
import im.xrl.ceobe.core.runtime.InstanceManager;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.control.Button;
import javafx.scene.control.ButtonBar;
import javafx.scene.control.ButtonType;
import javafx.scene.control.ComboBox;
import javafx.scene.control.Dialog;
import javafx.scene.control.Label;
import javafx.scene.control.ProgressBar;
import javafx.scene.control.Spinner;
import javafx.scene.control.SpinnerValueFactory;
import javafx.scene.control.TextField;
import javafx.scene.layout.GridPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.VBox;
import javafx.stage.Window;


/** 新建实例对话框：服务端与版本 → 资源与端口 → 创建（带进度）。 */
public final class CreateDialog {

    private final InstanceManager manager;
    private final Runnable onCreated;
    private final Dialog<ButtonType> dialog = new Dialog<>();

    private final TextField nameField = new TextField();
    private final ComboBox<ServerType> typeBox = new ComboBox<>();
    private final ComboBox<String> versionBox = new ComboBox<>();
    private final TextField loaderField = new TextField();
    private final Spinner<Integer> memorySpinner = new Spinner<>();
    private final Spinner<Integer> portSpinner = new Spinner<>();
    private final TextField jvmField = new TextField();
    private final ProgressBar progress = new ProgressBar(0);
    private final Label stage = new Label("就绪");
    private final Label jdkHint = new Label();
    private final Button jdkInstall = new Button();
    private final ButtonType createBtn = new ButtonType("创建并下载", ButtonBar.ButtonData.OK_DONE);

    public CreateDialog(Window owner, InstanceManager manager, Runnable onCreated) {
        this.manager = manager;
        this.onCreated = onCreated;
        build(owner);
    }

    public void show() {
        dialog.showAndWait();
    }

    private void build(Window owner) {
        dialog.initOwner(owner);
        dialog.setTitle("新建实例");
        dialog.setHeaderText("选择服务端与版本，然后创建");

        nameField.setPromptText("例如 survival / modded");
        typeBox.getItems().setAll(ServerType.values());
        typeBox.setValue(ServerType.PAPER);
        versionBox.setPromptText("加载中…");
        loaderField.setPromptText("留空 = 最新 stable（仅 Fabric）");
        memorySpinner.setValueFactory(new SpinnerValueFactory.IntegerSpinnerValueFactory(
                512, 65536, 2048, 512));
        memorySpinner.setEditable(true);
        portSpinner.setValueFactory(new SpinnerValueFactory.IntegerSpinnerValueFactory(
                1, 65535, 25565));
        portSpinner.setEditable(true);
        jvmField.setPromptText("附加 JVM 参数（可留空）");

        GridPane grid = new GridPane();
        grid.setHgap(12);
        grid.setVgap(10);
        grid.setPadding(new Insets(10, 4, 4, 4));
        int r = 0;
        grid.addRow(r++, new Label("实例名"), nameField);
        grid.addRow(r++, new Label("服务端"), typeBox);
        grid.addRow(r++, new Label("游戏版本"), versionBox);
        grid.addRow(r++, new Label("Fabric loader"), loaderField);
        grid.addRow(r++, new Label("内存上限 (MB)"), memorySpinner);
        grid.addRow(r++, new Label("端口"), portSpinner);
        grid.addRow(r++, new Label("JVM 参数"), jvmField);

        progress.setPrefWidth(420);
        HBox step1 = new HBox(8, step("1 服务端与版本"), step("2 资源与端口"), step("3 创建"));
        step1.setAlignment(Pos.CENTER_LEFT);
        Label portHint = new Label("端口在实例间必须唯一（否则玩家的穿透地址会指向错误的实例）；此处已自动填入当前空闲端口。");
        portHint.getStyleClass().add("ceobe-muted");
        portHint.setWrapText(true);

        // Java 版本提示 + 一键安装
        jdkHint.getStyleClass().add("ceobe-muted");
        jdkInstall.setVisible(false);
        jdkInstall.setManaged(false);
        jdkInstall.addEventHandler(javafx.event.ActionEvent.ACTION, e -> doInstallJdk());
        HBox jdkRow = new HBox(8, jdkHint, jdkInstall);
        jdkRow.setAlignment(Pos.CENTER_LEFT);

        VBox content = new VBox(10, step1, grid, portHint, jdkRow, progress, stage);
        content.setPadding(new Insets(6));
        dialog.getDialogPane().setContent(content);
        dialog.getDialogPane().getButtonTypes().addAll(createBtn, ButtonType.CANCEL);

        var createNode = dialog.getDialogPane().lookupButton(createBtn);
        createNode.getStyleClass().add("ceobe-primary");
        createNode.addEventFilter(javafx.event.ActionEvent.ACTION, e -> {
            if (nameField.getText().isBlank()) {
                stage.setText("请填写实例名");
                e.consume();
                return;
            }
            if (versionBox.getValue() == null) {
                stage.setText("版本尚未加载完成");
                e.consume();
                return;
            }
            e.consume();
            doCreate();
        });

        typeBox.valueProperty().addListener((o, a, b) -> loadVersions(b));
        versionBox.valueProperty().addListener((o, a, b) -> updateJdkHint());
        loadVersions(typeBox.getValue());

        // 端口排他：默认填入当前可用端口（避开已登记实例与本机占用）
        Fx.background(() -> manager.suggestFreePort(25565),
                p -> portSpinner.getValueFactory().setValue(p),
                err -> {});
    }

    private Label step(String text) {
        Label label = new Label(text);
        label.getStyleClass().add("ceobe-step");
        return label;
    }

    private void loadVersions(ServerType type) {
        if (type == null) return;
        versionBox.getItems().clear();
        versionBox.setPromptText("加载中…");
        Fx.background(() -> ProviderRegistry.of(type).listVersions(),
                versions -> {
                    versionBox.getItems().setAll(versions);
                    if (!versions.isEmpty()) versionBox.setValue(versions.get(0));
                },
                err -> versionBox.setPromptText("版本加载失败: " + err.getMessage()));
    }

    /** 依据所选版本更新 Java 需求提示（异步探测，避免阻塞 UI）。 */
    private void updateJdkHint() {
        String v = versionBox.getValue();
        if (v == null) {
            jdkHint.setText("");
            setInstallVisible(false, 0);
            return;
        }
        Fx.background(() -> {
            int req = JavaRequirement.requiredMajor(v);
            int runtime = JavaEnv.runtimeMajor();
            boolean satisfied = runtime == req || JdkStore.findMajor(req).isPresent();
            return new int[]{req, runtime, satisfied ? 1 : 0};
        }, r -> {
            if (r[2] == 1) {
                jdkHint.getStyleClass().setAll("ceobe-muted");
                jdkHint.setText("需要 Java %d：已就绪".formatted(r[0]));
                setInstallVisible(false, 0);
            } else {
                jdkHint.getStyleClass().setAll("ceobe-warn");
                jdkHint.setText("需要 Java %d（本机 Java %d），可一键安装".formatted(r[0], r[1]));
                setInstallVisible(true, r[0]);
            }
        }, err -> jdkHint.setText(""));
    }

    private void setInstallVisible(boolean visible, int major) {
        jdkInstall.setVisible(visible);
        jdkInstall.setManaged(visible);
        if (visible) {
            jdkInstall.setText("安装 Java " + major);
            jdkInstall.setDisable(false);
        }
    }

    /** 一键安装所选版本所需的 JDK（按平台自动选包，清华镜像优先）。 */
    private void doInstallJdk() {
        int req;
        try {
            req = Integer.parseInt(jdkInstall.getText().replaceAll("\\D+", ""));
        } catch (NumberFormatException e) {
            return;
        }
        jdkInstall.setDisable(true);
        progress.setProgress(-1);
        Fx.background(() -> JdkInstaller.install(req, (what, done, total) ->
                        javafx.application.Platform.runLater(() -> {
                            stage.setText(what);
                            if (total > 0) progress.setProgress((double) done / total);
                        })),
                home -> {
                    progress.setProgress(0);
                    stage.setText("已安装 Java " + req + "（创建时将自动选用）");
                    updateJdkHint();
                },
                err -> {
                    progress.setProgress(0);
                    stage.setText("JDK 安装失败: " + err.getMessage());
                    jdkInstall.setDisable(false);
                });
    }

    private void doCreate() {
        ServerType type = typeBox.getValue();
        String loader = loaderField.getText() == null || loaderField.getText().isBlank()
                ? null : loaderField.getText().trim();
        InstanceSpec spec = new InstanceSpec(
                nameField.getText().trim(), type, versionBox.getValue(), loader,
                memorySpinner.getValue(), jvmField.getText() == null ? "" : jvmField.getText(),
                portSpinner.getValue(), null);

        progress.setProgress(-1);
        Fx.background(() -> manager.create(spec, (what, done, total) -> javafx.application.Platform.runLater(() -> {
            stage.setText(what);
            if (total > 0) progress.setProgress((double) done / total);
        })), meta -> {
            stage.setText("创建完成: " + meta.name);
            progress.setProgress(1);
            onCreated.run();
            dialog.setResult(createBtn);
            dialog.close();
        }, err -> {
            stage.setText("创建失败: " + err.getMessage());
            progress.setProgress(0);
        });
    }
}
