package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.event.InstanceEvent;
import im.xrl.ceobe.core.model.InstanceState;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.store.CeobeHome;
import im.xrl.ceobe.core.tunnel.TunnelManager;
import im.xrl.ceobe.core.webconsole.WebConsole;
import javafx.application.Platform;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Parent;
import javafx.scene.control.Button;
import javafx.scene.control.Label;
import javafx.scene.control.ListCell;
import javafx.scene.control.ListView;
import javafx.scene.control.Tab;
import javafx.scene.control.TabPane;
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.Region;
import javafx.scene.layout.VBox;

import java.util.List;

/** 主界面：左侧实例列表 + 右侧功能页签 + 底部状态栏。 */
public final class MainView {

    private final InstanceManager manager;
    private final TunnelManager tunnels;
    private final WebConsole webConsole;

    private final BorderPane root = new BorderPane();
    private final ListView<InstanceStatus> list = new ListView<>();
    private final TabPane tabs = new TabPane();
    private final Label statusBar = new Label();
    private final Label emptyHint = new Label("← 先创建一个实例\n（左上「新建实例」）");

    private ConsolePane console;
    private ConfigPane config;
    private ModPane mods;
    private TunnelPane tunnelPane;
    private String selected;
    /** 上次列表签名：用于判断是否需要重建列表。 */
    private String currentSig = "\u0000";
    /** 已应用到页签的运行态：null 表示尚未应用。 */
    private Boolean liveApplied;

    public MainView(InstanceManager manager, TunnelManager tunnels, WebConsole webConsole) {
        this.manager = manager;
        this.tunnels = tunnels;
        this.webConsole = webConsole;
        build();
        wire();
        startPoller();
    }

    public Parent root() {
        return root;
    }

    private void build() {
        root.getStyleClass().add("ceobe-root");

        // 侧栏
        Label title = new Label("ceobe");
        title.getStyleClass().add("ceobe-title");
        Label subtitle = new Label("Minecraft 服务器编排");
        subtitle.getStyleClass().add("ceobe-muted");
        Button create = new Button("＋ 新建实例");
        create.getStyleClass().add("ceobe-primary");
        create.setMaxWidth(Double.MAX_VALUE);
        create.setOnAction(e -> new CreateDialog(root.getScene().getWindow(), manager, this::refresh).show());

        VBox header = new VBox(2, title, subtitle);
        VBox side = new VBox(10, header, create, list);
        side.getStyleClass().add("ceobe-sidebar");
        side.setPrefWidth(264);
        side.setMinWidth(220);
        side.setPadding(new Insets(12));
        VBox.setVgrow(list, Priority.ALWAYS);

        // 内容区
        emptyHint.getStyleClass().add("ceobe-empty");
        emptyHint.setAlignment(Pos.CENTER);
        BorderPane content = new BorderPane();
        content.setCenter(emptyHint);
        content.getStyleClass().add("ceobe-content");

        // 状态栏
        statusBar.getStyleClass().add("ceobe-status");
        HBox status = new HBox(statusBar);
        status.setPadding(new Insets(6, 14, 6, 14));
        status.getStyleClass().add("ceobe-statusbar");

        root.setLeft(side);
        root.setCenter(content);
        root.setBottom(status);
        this.contentArea = content;

        list.setCellFactory(v -> new InstanceCell());
        list.getSelectionModel().selectedItemProperty().addListener((obs, old, now) -> {
            if (now == null) return;
            // 同一实例的重复选中（如列表刷新引起的对象替换）不重建页签
            if (now.meta().name.equals(selected)) return;
            select(now);
        });
    }

    private BorderPane contentArea;

    private void wire() {
        manager.addListener(event -> Platform.runLater(() -> {
            if (event instanceof InstanceEvent.LogLine l) {
                if (l.instance().equals(selected) && console != null) console.append(l.line());
            } else if (event instanceof InstanceEvent.StateChanged s) {
                if (s.instance().equals(selected)) onStateChanged(s.state());
                refresh();
            } else if (event instanceof InstanceEvent.TunnelChanged t) {
                if (t.instance().equals(selected) && tunnelPane != null) tunnelPane.onAddressChanged(t);
            }
        }));
    }

    /** 外部进程改变状态时也能刷新（每 1.5 秒轮询）。 */
    private void startPoller() {
        javafx.animation.Timeline timeline = new javafx.animation.Timeline(
                new javafx.animation.KeyFrame(javafx.util.Duration.seconds(1.5), e -> refresh()));
        timeline.setCycleCount(javafx.animation.Animation.INDEFINITE);
        timeline.play();
    }

    /**
     * 拉取实例列表并就地更新。
     * <p>列表内容不变时绝不重建列表——否则每轮轮询都会替换选中项对象、触发选中变化，
     * 进而重建页签并把用户强制弹回「控制台」（此前的抽搐/回弹 bug）。
     */
    public void refresh() {
        Fx.background(manager::list, statuses -> {
            if (!sameAsCurrent(statuses)) {
                String keep = selected;
                list.getItems().setAll(statuses);
                if (keep != null) {
                    statuses.stream().filter(s -> s.meta().name.equals(keep)).findFirst()
                            .ifPresent(s -> list.getSelectionModel().select(s));
                }
            }
            statusBar.setText(statusText(statuses));
            statuses.stream().filter(s -> s.meta().name.equals(selected)).findFirst()
                    .ifPresent(this::syncLive);
            if (console != null) console.pollExternalLog();
        }, t -> statusBar.setText("读取实例列表失败: " + t.getMessage()));
    }

    /** 与当前列表逐项比对（名称/状态/pid/地址），完全一致即视为无变化。 */
    private boolean sameAsCurrent(List<InstanceStatus> statuses) {
        String sig = signature(statuses);
        if (sig.equals(currentSig)) return true;
        currentSig = sig;
        return false;
    }

    private static String signature(List<InstanceStatus> statuses) {
        StringBuilder sb = new StringBuilder();
        for (InstanceStatus s : statuses) {
            sb.append(s.meta().name).append('|').append(s.state()).append('|').append(s.pid())
                    .append('|').append(s.pinggyAddress()).append('|').append(s.cloudflareUrl())
                    .append('\n');
        }
        return sb.toString();
    }

    /** 仅在实际变化时同步运行态，避免每轮轮询无谓重设样式造成闪烁。 */
    private void syncLive(InstanceStatus status) {
        boolean live = status.state().isLive();
        if (liveApplied != null && liveApplied == live) return;
        liveApplied = live;
        if (console != null) console.setLive(live);
        if (tunnelPane != null) tunnelPane.setLive(live);
    }

    private String statusText(List<InstanceStatus> statuses) {
        long running = statuses.stream().filter(s -> s.state() == InstanceState.RUNNING).count();
        String web = webConsole != null ? "Web 控制台 http://127.0.0.1:" + webConsole.port() : "Web 控制台未启用";
        return "主目录 %s   ·   实例 %d（运行中 %d）   ·   %s"
                .formatted(CeobeHome.root(), statuses.size(), running, web);
    }

    private void select(InstanceStatus status) {
        selected = status.meta().name;
        liveApplied = null;
        console = new ConsolePane(manager, status);
        console.setOnDeleted(() -> {
            selected = null;
            liveApplied = null;
            contentArea.setCenter(emptyHint);
            refresh();
        });
        config = new ConfigPane(manager, status);
        mods = new ModPane(manager, status);
        tunnelPane = new TunnelPane(manager, tunnels, status);

        tabs.getTabs().setAll(
                tab("控制台", console.node()),
                tab("配置", config.node()),
                tab("Mod", mods.node()),
                tab("穿透", tunnelPane.node()));
        tabs.setTabClosingPolicy(TabPane.TabClosingPolicy.UNAVAILABLE);
        contentArea.setCenter(tabs);
        syncLive(status);
        statusBar.setText(statusText(list.getItems()));
    }

    private static Tab tab(String name, javafx.scene.Node content) {
        Tab t = new Tab(name, content);
        t.setClosable(false);
        return t;
    }

    private void onStateChanged(InstanceState state) {
        if (console != null) console.setLive(state.isLive());
        if (tunnelPane != null) tunnelPane.setLive(state.isLive());
    }

    /** 列表单元：名称 + 状态徽标 + 版本/地址两行。 */
    private static final class InstanceCell extends ListCell<InstanceStatus> {
        @Override
        protected void updateItem(InstanceStatus item, boolean empty) {
            super.updateItem(item, empty);
            if (empty || item == null) {
                setText(null);
                setGraphic(null);
                return;
            }
            Label name = new Label(item.meta().name);
            name.getStyleClass().add("ceobe-item-name");
            Label badge = new Label(item.state().zhLabel());
            badge.getStyleClass().addAll("ceobe-badge", "state-" + item.state().name());
            Region spacer = new Region();
            HBox.setHgrow(spacer, Priority.ALWAYS);
            HBox top = new HBox(8, name, spacer, badge);
            top.setAlignment(Pos.CENTER_LEFT);

            Label second = new Label("%s %s · %s".formatted(
                    item.meta().serverType.name(), item.meta().gameVersion,
                    item.pinggyAddress() != null ? item.pinggyAddress()
                            : "127.0.0.1:" + item.meta().port));
            second.getStyleClass().add("ceobe-muted");
            VBox cell = new VBox(3, top, second);
            cell.setPadding(new Insets(6, 4, 6, 4));
            setText(null);
            setGraphic(cell);
        }
    }

}
