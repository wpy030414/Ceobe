package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.model.InstanceSpec;
import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.mods.ModrinthClient;
import im.xrl.ceobe.core.mods.MrpackInstaller;
import im.xrl.ceobe.core.provider.ProgressListener;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.CeobeException;
import javafx.application.Platform;
import javafx.beans.property.ReadOnlyStringWrapper;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Node;
import javafx.scene.control.Button;
import javafx.scene.control.CheckBox;
import javafx.scene.control.Label;
import javafx.scene.control.ProgressBar;
import javafx.scene.control.SelectionMode;
import javafx.scene.control.Spinner;
import javafx.scene.control.SpinnerValueFactory;
import javafx.scene.control.TableColumn;
import javafx.scene.control.TableView;
import javafx.scene.control.TextField;
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.Region;
import javafx.scene.layout.VBox;

import java.nio.file.Path;

/**
 * 「整合包」页：搜索 Modrinth 整合包（.mrpack），
 * 既可安装到本实例（需加载器/版本一致），也可一键新建匹配实例再安装。
 */
public final class ModpackPane {

    private final InstanceManager manager;
    private final InstanceStatus status;
    private final Runnable onInstanceCreated;
    private final BorderPane root = new BorderPane();

    private final TextField query = new TextField();
    private final CheckBox onlyCompatible = new CheckBox("仅适配本实例");
    private final TableView<ModrinthClient.PackHit> table = new TableView<>();
    private final Button installHere = new Button("安装到本实例");
    private final TextField newName = new TextField();
    private final Spinner<Integer> newMem = new Spinner<>();
    private final Button createInstall = new Button("新建实例并安装");
    private final ProgressBar progress = new ProgressBar(0);
    private final Label statusLabel = new Label();

    public ModpackPane(InstanceManager manager, InstanceStatus status, Runnable onInstanceCreated) {
        this.manager = manager;
        this.status = status;
        this.onInstanceCreated = onInstanceCreated;
        build();
        setLive(status.state().isLive());
    }

    public Node node() {
        return root;
    }

    private void build() {
        // —— 搜索栏 ——
        query.setPromptText("搜索 Modrinth 整合包（留空=浏览）");
        query.setOnAction(e -> doSearch());
        HBox.setHgrow(query, Priority.ALWAYS);
        Button search = new Button("搜索");
        search.getStyleClass().add("ceobe-primary");
        search.setOnAction(e -> doSearch());
        onlyCompatible.setSelected(true);
        HBox bar = new HBox(10, query, onlyCompatible, search);
        bar.setAlignment(Pos.CENTER_LEFT);
        bar.setPadding(new Insets(0, 0, 8, 0));

        // —— 结果表 ——
        table.getSelectionModel().setSelectionMode(SelectionMode.SINGLE);
        table.setColumnResizePolicy(TableView.CONSTRAINED_RESIZE_POLICY_FLEX_LAST_COLUMN);
        table.getStyleClass().add("ceobe-table");
        table.setPlaceholder(new Label("搜索 Modrinth 以选择整合包"));
        table.getColumns().addAll(
                col("名称", 240, h -> h.title()),
                col("标识", 180, h -> h.slug()),
                col("作者", 140, h -> h.author()),
                col("下载量", 100, h -> String.valueOf(h.downloads())),
                col("分类", 220, h -> String.join(",", h.categories().stream().limit(3).toList())));
        table.getSelectionModel().selectedItemProperty().addListener((o, a, b) -> {
            if (b != null) newName.setText(b.slug());
        });
        VBox.setVgrow(table, Priority.ALWAYS);

        // —— 操作区 ——
        installHere.setOnAction(e -> doInstallHere());
        newName.setPromptText("新实例名（默认=标识）");
        newName.setPrefWidth(180);
        newMem.setValueFactory(new SpinnerValueFactory.IntegerSpinnerValueFactory(512, 65536, 4096, 512));
        newMem.setEditable(true);
        newMem.setPrefWidth(110);
        createInstall.setOnAction(e -> doCreateInstall());
        Region spacer = new Region();
        HBox.setHgrow(spacer, Priority.ALWAYS);
        HBox actions = new HBox(10, installHere, spacer,
                new Label("内存(MB)"), newMem, newName, createInstall);
        actions.setAlignment(Pos.CENTER_LEFT);

        statusLabel.getStyleClass().add("ceobe-muted");
        progress.setPrefWidth(240);
        HBox statusRow = new HBox(10, progress, statusLabel);
        statusRow.setAlignment(Pos.CENTER_LEFT);

        VBox body = new VBox(10, bar, table, actions, statusRow);
        body.setPadding(new Insets(14, 16, 14, 16));
        root.setCenter(body);
    }

    private static TableColumn<ModrinthClient.PackHit, String> col(
            String title, double width, java.util.function.Function<ModrinthClient.PackHit, String> getter) {
        TableColumn<ModrinthClient.PackHit, String> c = new TableColumn<>(title);
        c.setPrefWidth(width);
        c.setCellValueFactory(cd -> new ReadOnlyStringWrapper(getter.apply(cd.getValue())));
        return c;
    }

    public void setLive(boolean live) {
        installHere.setDisable(live);
        installHere.setTooltip(live ? new javafx.scene.control.Tooltip("实例运行中；停止后安装") : null);
    }

    private void doSearch() {
        String q = query.getText() == null ? "" : query.getText().trim();
        boolean compat = onlyCompatible.isSelected();
        String gv = compat ? status.meta().gameVersion : null;
        String loader = compat ? status.meta().serverType.loaderId() : null;
        statusLabel.setText("搜索中…");
        Fx.background(() -> new ModrinthClient().searchPacks(q, gv, loader, 25), hits -> {
            table.getItems().setAll(hits);
            statusLabel.setText(hits.isEmpty() ? "没有匹配的整合包" : "共 " + hits.size() + " 个结果");
        }, err -> statusLabel.setText("搜索失败: " + err.getMessage()));
    }

    private ProgressListener progressTo() {
        return (stage, done, total) -> Platform.runLater(() -> {
            statusLabel.setText(stage);
            progress.setProgress(total > 0 ? (double) done / total : -1);
        });
    }

    private void doInstallHere() {
        ModrinthClient.PackHit hit = table.getSelectionModel().getSelectedItem();
        if (hit == null) {
            statusLabel.setText("请先选择一个整合包");
            return;
        }
        installHere.setDisable(true);
        String gv = status.meta().gameVersion;
        String loader = status.meta().serverType.loaderId();
        Fx.background(() -> {
            if (loader == null) {
                throw CeobeException.of("%s 不支持装整合包（无模组加载器）", status.meta().serverType.zhLabel());
            }
            ModrinthClient.PackVersion pv = new ModrinthClient().resolvePack(hit.slug(), gv, loader);
            Path zip = MrpackInstaller.fetch(pv, progressTo());
            MrpackInstaller.Index idx = MrpackInstaller.readIndex(zip);
            if (idx.mcVersion() != null && !idx.mcVersion().equals(gv)) {
                throw CeobeException.of("本实例是 MC %s，整合包要求 MC %s", gv, idx.mcVersion());
            }
            if (idx.loader() != null && !idx.loader().equals(loader)) {
                throw CeobeException.of("本实例加载器是 %s，整合包要求 %s", loader, idx.loader());
            }
            return MrpackInstaller.install(manager.dir(status.meta().name), zip, progressTo());
        }, r -> {
            installHere.setDisable(false);
            progress.setProgress(1);
            statusLabel.setText("已安装「%s」：%d 个文件，跳过 %d，覆盖 %d"
                    .formatted(r.name(), r.files(), r.skipped(), r.overrides()));
        }, err -> {
            installHere.setDisable(false);
            progress.setProgress(0);
            statusLabel.setText("安装失败: " + err.getMessage());
        });
    }

    private void doCreateInstall() {
        ModrinthClient.PackHit hit = table.getSelectionModel().getSelectedItem();
        if (hit == null) {
            statusLabel.setText("请先选择一个整合包");
            return;
        }
        createInstall.setDisable(true);
        int mem = newMem.getValue();
        Fx.background(() -> {
            ModrinthClient.PackVersion pv = new ModrinthClient().resolvePack(hit.slug(), null, null);
            Path zip = MrpackInstaller.fetch(pv, progressTo());
            MrpackInstaller.Index idx = MrpackInstaller.readIndex(zip);
            if (idx.mcVersion() == null) throw new CeobeException("整合包未声明 MC 版本");
            ServerType type = ServerType.fromLoaderId(idx.loader());
            String name = newName.getText() == null || newName.getText().isBlank()
                    ? hit.slug() : newName.getText().trim();
            InstanceSpec spec = new InstanceSpec(name, type, idx.mcVersion(), idx.loaderVersion(),
                    mem, "", manager.suggestFreePort(25565), null);
            manager.create(spec, progressTo());
            return MrpackInstaller.install(manager.dir(name), zip, progressTo());
        }, r -> {
            createInstall.setDisable(false);
            progress.setProgress(1);
            statusLabel.setText("已新建实例并安装「%s」：%d 个文件".formatted(r.name(), r.files()));
            onInstanceCreated.run();
        }, err -> {
            createInstall.setDisable(false);
            progress.setProgress(0);
            statusLabel.setText("新建失败: " + err.getMessage());
        });
    }
}
