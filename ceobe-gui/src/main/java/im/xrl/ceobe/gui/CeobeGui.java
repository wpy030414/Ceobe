package im.xrl.ceobe.gui;

import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.tunnel.TunnelManager;
import im.xrl.ceobe.core.webconsole.WebConsole;
import im.xrl.ceobe.gui.ui.MainView;
import im.xrl.ceobe.gui.ui.Theme;
import javafx.application.Application;
import javafx.scene.Scene;
import javafx.stage.Stage;

/**
 * ceobe GUI 入口：本进程即所启动实例的「监督进程」（含控制通道与隧道管理）。
 */
public final class CeobeGui extends Application {

    private TunnelManager tunnels;
    private WebConsole webConsole;
    private InstanceManager manager;

    @Override
    public void start(Stage stage) {
        manager = new InstanceManager();
        tunnels = new TunnelManager(manager);
        try {
            webConsole = new WebConsole(manager, 0);
            tunnels.webConsolePort(webConsole.port());
        } catch (RuntimeException e) {
            webConsole = null;
        }

        MainView view = new MainView(manager, tunnels, webConsole);
        Scene scene = new Scene(view.root(), 1180, 760);
        Theme.apply(scene);

        stage.setTitle("ceobe — Minecraft 服务器编排");
        stage.setScene(scene);
        stage.setMinWidth(900);
        stage.setMinHeight(600);
        stage.setOnCloseRequest(e -> {
            if (!confirmStopHosted(stage)) {
                e.consume();
                return;
            }
            shutdown();
        });
        stage.show();
        view.refresh();
    }

    /** 本进程托管的运行实例：关闭前确认并优雅停止（避免留下无控制通道的孤儿进程）。 */
    private boolean confirmStopHosted(Stage stage) {
        var hosted = manager.list().stream()
                .filter(s -> s.ownedByThisProcess() && s.state().isLive())
                .toList();
        if (hosted.isEmpty()) return true;
        var alert = new javafx.scene.control.Alert(
                javafx.scene.control.Alert.AlertType.CONFIRMATION,
                "由本界面托管的 " + hosted.size() + " 个服务器仍在运行："
                        + String.join("、", hosted.stream().map(s -> s.meta().name).toList())
                        + "\n关闭窗口将优雅停止它们（世界数据会正常保存）。",
                javafx.scene.control.ButtonType.OK, javafx.scene.control.ButtonType.CANCEL);
        alert.setTitle("仍有服务器在运行");
        alert.setHeaderText(null);
        alert.initOwner(stage);
        var choice = alert.showAndWait();
        if (choice.isEmpty() || choice.get() != javafx.scene.control.ButtonType.OK) return false;
        hosted.forEach(s -> {
            try {
                manager.stop(s.meta().name);
            } catch (RuntimeException ignored) {
                // 单个实例停止失败不阻塞退出
            }
        });
        return true;
    }

    private void shutdown() {
        if (tunnels != null) tunnels.close();
        if (webConsole != null) webConsole.close();
    }

    @Override
    public void stop() {
        shutdown();
    }

    public static void main(String[] args) {
        launch(args);
    }
}
