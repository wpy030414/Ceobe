package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.event.InstanceEvent;
import im.xrl.ceobe.core.runtime.InstanceControl;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import im.xrl.ceobe.core.tunnel.TunnelKind;
import im.xrl.ceobe.core.tunnel.TunnelSession;
import javafx.application.Platform;
import javafx.geometry.Insets;
import javafx.geometry.Pos;
import javafx.scene.Node;
import javafx.scene.control.Button;
import javafx.scene.control.Label;
import javafx.scene.layout.HBox;
import javafx.scene.layout.Priority;
import javafx.scene.layout.VBox;

import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * 穿透页：三条通道卡片（Pinggy 玩家连接 / Cloudflare Web 控制台 / 局域网直连）。
 * <p>地址轮换（Pinggy 续期）会实时刷新并提示重新分享。
 */
public final class TunnelPane {

    private final InstanceManager manager;
    private final im.xrl.ceobe.core.tunnel.TunnelManager tunnels;
    private final InstanceStatus status;
    private final VBox root = new VBox(12);

    private final Map<TunnelKind, Card> cards = new LinkedHashMap<>();

    /** 单条通道卡片。 */
    private final class Card {
        final TunnelKind kind;
        final Label state = new Label("未开启");
        final Label address = new Label("—");
        final Label notice = new Label();
        final Button toggle = new Button("开启");

        Card(TunnelKind kind) {
            this.kind = kind;
        }
    }

    public TunnelPane(InstanceManager manager, im.xrl.ceobe.core.tunnel.TunnelManager tunnels,
                      InstanceStatus status) {
        this.manager = manager;
        this.tunnels = tunnels;
        this.status = status;
        build();
    }

    public Node node() {
        return root;
    }

    private void build() {
        root.setPadding(new Insets(16));
        root.setAlignment(Pos.TOP_LEFT);

        Label intro = new Label("把地址发给朋友，在「多人游戏 → 直接连接」填入即可。"
                + "Pinggy 免费通道约 55 分钟后自动续期，届时地址会轮换。");
        intro.getStyleClass().add("ceobe-muted");
        intro.setWrapText(true);
        root.getChildren().add(intro);

        for (TunnelKind kind : new TunnelKind[]{TunnelKind.PINGGY, TunnelKind.CLOUDFLARE, TunnelKind.LAN}) {
            Card card = new Card(kind);
            cards.put(kind, card);
            root.getChildren().add(buildCard(card));
        }
        refresh();
    }

    private Node buildCard(Card card) {
        Label title = new Label(card.kind.zhLabel());
        title.getStyleClass().add("ceobe-h3");
        card.state.getStyleClass().addAll("ceobe-badge", "state-STOPPED");

        var spacer = new javafx.scene.layout.Region();
        HBox.setHgrow(spacer, Priority.ALWAYS);
        HBox head = new HBox(8, title, spacer, card.state);
        head.setAlignment(Pos.CENTER_LEFT);

        card.address.getStyleClass().add("ceobe-mono-addr");
        card.address.setWrapText(true);
        Button copy = new Button("复制地址");
        copy.setOnAction(e -> {
            Fx.copy(card.address.getText());
            card.notice.setText("已复制到剪贴板");
        });
        HBox addrRow = new HBox(8, card.address, copy);
        addrRow.setAlignment(Pos.CENTER_LEFT);

        card.notice.getStyleClass().add("ceobe-muted");

        card.toggle.getStyleClass().add("ceobe-primary");
        card.toggle.setOnAction(e -> toggle(card));

        HBox actions = new HBox(8, card.toggle, card.notice);
        actions.setAlignment(Pos.CENTER_LEFT);

        VBox box = new VBox(8, head, addrRow, actions);
        box.getStyleClass().add("ceobe-card");
        box.setPadding(new Insets(14, 16, 14, 16));
        return box;
    }

    private void toggle(Card card) {
        boolean up = isUp(card.kind);
        card.toggle.setDisable(true);
        if (card.kind == TunnelKind.LAN) {
            if (up) {
                // 局域网为纯展示，不做关闭
            } else {
                card.notice.setText("局域网地址无需开启，直接使用即可");
            }
            card.toggle.setDisable(false);
            return;
        }
        Fx.background(() -> {
            Path dir = manager.dir(status.meta().name);
            if (manager.processOf(status.meta().name).isPresent()) {
                if (up) tunnels.down(status.meta().name, card.kind);
                else return tunnels.up(status.meta().name, card.kind).publicUrl;
            } else if (InstanceControl.isAvailable(dir)) {
                if (up) InstanceControl.tunnelDown(dir, card.kind.id());
                else return InstanceControl.tunnelUp(dir, card.kind.id()).get(0);
            } else {
                throw new IllegalStateException("实例未在运行（需先启动，且由 ceobe 托管）");
            }
            return null;
        }, url -> {
            card.toggle.setDisable(false);
            if (url != null && !url.isBlank() && !"-".equals(url)) {
                card.address.setText(url);
                card.state.setText("已开启");
                setState(card, "RUNNING");
                card.notice.setText("已开启，请把地址分享给朋友");
            } else {
                card.notice.setText("已关闭");
            }
            refresh();
        }, err -> {
            card.toggle.setDisable(false);
            card.notice.setText("失败: " + err.getMessage());
        });
    }

    public void setLive(boolean live) {
        for (Card card : cards.values()) {
            if (card.kind != TunnelKind.LAN) {
                card.toggle.setDisable(!live && !card.toggle.getText().equals("关闭"));
            }
        }
    }

    /** 地址轮换通知。 */
    public void onAddressChanged(InstanceEvent.TunnelChanged event) {
        TunnelKind kind = TunnelKind.fromId(event.kind());
        Card card = cards.get(kind);
        if (card == null) return;
        card.address.setText(event.publicUrl());
        card.notice.getStyleClass().removeAll("ceobe-warn");
        card.notice.getStyleClass().add("ceobe-warn");
        card.notice.setText("地址已轮换，请重新分享给朋友");
        refresh();
    }

    private boolean isUp(TunnelKind kind) {
        Path dir = manager.dir(status.meta().name);
        if (manager.processOf(status.meta().name).isPresent()) {
            return tunnels.isUp(status.meta().name, kind);
        }
        if (kind == TunnelKind.LAN) return true;
        if (!InstanceControl.isAvailable(dir)) return false;
        try {
            return InstanceControl.tunnelStatus(dir).stream()
                    .anyMatch(line -> line.startsWith(kind.id() + "|"));
        } catch (RuntimeException e) {
            return false;
        }
    }

    private void refresh() {
        Fx.background(() -> {
            Map<TunnelKind, String> urls = new LinkedHashMap<>();
            Path dir = manager.dir(status.meta().name);
            if (manager.processOf(status.meta().name).isPresent()) {
                for (TunnelSession s : tunnels.activeOf(status.meta().name)) {
                    urls.put(s.kind, s.publicUrl);
                }
            } else if (InstanceControl.isAvailable(dir)) {
                for (String line : InstanceControl.tunnelStatus(dir)) {
                    String[] parts = line.split("\\|", -1);
                    if (parts.length >= 2) {
                        try {
                            urls.put(TunnelKind.fromId(parts[0]), parts[1]);
                        } catch (IllegalArgumentException ignored) {
                        }
                    }
                }
            }
            return urls;
        }, urls -> {
            for (Card card : cards.values()) {
                String url = urls.get(card.kind);
                if (card.kind == TunnelKind.LAN && !urls.containsKey(card.kind)) {
                    var addrs = im.xrl.ceobe.core.tunnel.LanDirect.localAddresses();
                    if (!addrs.isEmpty()) {
                        url = addrs.get(0) + ":" + status.meta().port;
                    }
                }
                boolean up = url != null;
                card.address.setText(up ? url : "—");
                card.state.setText(up ? "已开启" : "未开启");
                setState(card, up ? "RUNNING" : "STOPPED");
                card.toggle.setText(card.kind == TunnelKind.LAN ? "局域网" : (up ? "关闭" : "开启"));
                card.toggle.setDisable(card.kind == TunnelKind.LAN);
            }
        }, err -> {
        });
    }

    private static void setState(Card card, String state) {
        if (!Platform.isFxApplicationThread()) {
            Platform.runLater(() -> setState(card, state));
            return;
        }
        card.state.getStyleClass().removeIf(c -> c.startsWith("state-"));
        card.state.getStyleClass().add("state-" + state);
    }
}
