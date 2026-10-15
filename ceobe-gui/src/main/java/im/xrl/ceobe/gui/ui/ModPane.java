package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.model.ServerType;
import im.xrl.ceobe.core.mods.ModEntry;
import im.xrl.ceobe.core.mods.ModrinthClient;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.util.Json;
import javafx.beans.property.SimpleStringProperty;
import javafx.collections.FXCollections;
import javafx.collections.ObservableList;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Node;
import javafx.scene.control.Button;
import javafx.scene.control.Label;
import javafx.scene.control.TableColumn;
import javafx.scene.control.TableView;
import javafx.scene.control.TextField;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.VBox;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

/** Mod 页：Modrinth 搜索安装 + 已装列表卸载。 */
public final class ModPane {

    private final InstanceManager manager;
    private final InstanceStatus status;
    private final VBox root = new VBox(12);
    private final ObservableList<ResultRow> results = FXCollections.observableArrayList();
    private final ObservableList<InstalledRow> installed = FXCollections.observableArrayList();
    private final Label hint = new Label();

    record ResultRow(String slug, String title, String author, long downloads, String categories) {
    }

    record InstalledRow(String file, String title, String version, String env, String present) {
    }

    public ModPane(InstanceManager manager, InstanceStatus status) {
        this.manager = manager;
        this.status = status;
        build();
    }

    public Node node() {
        return root;
    }

    private void build() {
        root.setPadding(new Insets(16));
        ServerType type = status.meta().serverType;

        if (!type.hasModsDir()) {
            Label empty = new Label(type.zhLabel() + " 没有 mods 目录，不支持安装模组。\n"
                    + "如需模组，请创建 Fabric 类型的实例。");
            empty.getStyleClass().add("ceobe-empty");
            root.getChildren().add(empty);
            root.setAlignment(Pos.CENTER);
            return;
        }

        TextField query = new TextField();
        query.setPromptText("搜索模组（将按 " + status.meta().gameVersion + " + "
                + type.name().toLowerCase() + " 过滤）");
        query.getStyleClass().add("ceobe-input");
        HBox.setHgrow(query, Priority.ALWAYS);
        Button search = new Button("搜索");
        search.getStyleClass().add("ceobe-primary");
        search.setOnAction(e -> doSearch(query.getText()));
        query.setOnAction(e -> doSearch(query.getText()));
        HBox searchBar = new HBox(8, query, search);
        searchBar.setAlignment(Pos.CENTER_LEFT);

        TableView<ResultRow> resultTable = new TableView<>(results);
        resultTable.getStyleClass().add("ceobe-table");
        resultTable.setPlaceholder(new Label("搜索 Modrinth 以安装模组"));
        resultTable.getColumns().addAll(
                col("名称", 240, ResultRow::title),
                col("标识", 150, ResultRow::slug),
                col("作者", 130, ResultRow::author),
                col("下载量", 100, r -> String.valueOf(r.downloads())),
                col("分类", 160, ResultRow::categories));
        VBox.setVgrow(resultTable, Priority.ALWAYS);

        Button install = new Button("安装所选");
        install.getStyleClass().add("ceobe-primary");
        install.setOnAction(e -> {
            ResultRow row = resultTable.getSelectionModel().getSelectedItem();
            if (row != null) doInstall(row.slug());
        });

        TableView<InstalledRow> installedTable = new TableView<>(installed);
        installedTable.getStyleClass().add("ceobe-table");
        installedTable.setPlaceholder(new Label("尚未安装模组"));
        installedTable.getColumns().addAll(
                col("文件", 260, InstalledRow::file),
                col("名称", 200, InstalledRow::title),
                col("版本", 140, InstalledRow::version),
                col("服务端侧", 100, InstalledRow::env),
                col("状态", 80, InstalledRow::present));
        installedTable.setPrefHeight(220);

        Button uninstall = new Button("卸载所选");
        uninstall.setOnAction(e -> {
            InstalledRow row = installedTable.getSelectionModel().getSelectedItem();
            if (row != null) doUninstall(row.file());
        });

        hint.getStyleClass().add("ceobe-muted");
        root.getChildren().addAll(
                searchBar,
                resultTable,
                install,
                new Label("已安装") {{
                    getStyleClass().add("ceobe-h3");
                }},
                installedTable,
                uninstall,
                hint);
        refreshInstalled();
    }

    private static <T> TableColumn<T, String> col(String name, double width,
                                                  java.util.function.Function<T, String> getter) {
        TableColumn<T, String> c = new TableColumn<>(name);
        c.setPrefWidth(width);
        c.setCellValueFactory(d -> new SimpleStringProperty(getter.apply(d.getValue())));
        return c;
    }

    private void doSearch(String q) {
        hint.setText("搜索中…");
        Fx.background(() -> new ModrinthClient().search(q, status.meta().gameVersion,
                        status.meta().serverType, 30),
                hits -> {
                    results.setAll(hits.stream().map(h -> new ResultRow(
                            h.slug(), h.title(), h.author(), h.downloads(),
                            String.join(",", h.categories().stream().limit(3).toList()))).toList());
                    hint.setText("找到 " + hits.size() + " 个结果");
                },
                err -> hint.setText("搜索失败: " + err.getMessage()));
    }

    private void doInstall(String slug) {
        hint.setText("安装 " + slug + " …");
        Fx.background(() -> {
            ModEntry entry = new ModrinthClient().install(manager.dir(status.meta().name),
                    status.meta().serverType, slug, status.meta().gameVersion, (s, d, t) -> {
                    });
            var meta = status.meta();
            meta.mods.removeIf(m -> m.fileName.equals(entry.fileName));
            meta.mods.add(entry);
            writeMeta(meta);
            return entry;
        }, entry -> {
            hint.setText("已安装 " + entry.title + " → " + entry.fileName);
            refreshInstalled();
        }, err -> hint.setText("安装失败: " + err.getMessage()));
    }

    private void doUninstall(String fileName) {
        Fx.background(() -> {
            var meta = status.meta();
            var match = meta.mods.stream().filter(m -> m.fileName.equals(fileName)).findFirst()
                    .orElseThrow();
            new ModrinthClient().uninstall(manager.dir(status.meta().name), match);
            meta.mods.remove(match);
            writeMeta(meta);
            return match.title;
        }, title -> {
            hint.setText("已卸载 " + title);
            refreshInstalled();
        }, err -> hint.setText("卸载失败: " + err.getMessage()));
    }

    private void writeMeta(im.xrl.ceobe.core.model.InstanceMeta meta) {
        try {
            Files.writeString(manager.dir(status.meta().name).resolve("instance.json"), Json.write(meta));
        } catch (java.io.IOException e) {
            throw new im.xrl.ceobe.core.util.CeobeException("写入实例元数据失败: " + e.getMessage(), e);
        }
    }

    private void refreshInstalled() {
        Path modsDir = manager.dir(status.meta().name).resolve("mods");
        List<InstalledRow> rows = status.meta().mods.stream()
                .map(m -> new InstalledRow(m.fileName, m.title, m.versionNumber, m.envServer,
                        Files.isRegularFile(modsDir.resolve(m.fileName)) ? "存在" : "缺失"))
                .toList();
        installed.setAll(rows);
    }
}
