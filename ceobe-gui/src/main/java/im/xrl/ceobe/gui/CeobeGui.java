package im.xrl.ceobe.gui;

import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.store.AppSettings;
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
        Theme.apply(scene, AppSettings.load().theme);

        stage.setTitle("Ceobe：Minecraft 私有服务器编排引擎");
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

        // 开发自检：-Dceobe.snapshot=<png> 时自动选实例/切页签并导出 Scene 快照
        String snap = System.getProperty("ceobe.snapshot");
        if (snap != null) {
            var pause = new javafx.animation.PauseTransition(javafx.util.Duration.seconds(4));
            pause.setOnFinished(e -> {
                view.debugSelect(System.getProperty("ceobe.snapshot.tab", "服务器配置"));
                var p2 = new javafx.animation.PauseTransition(javafx.util.Duration.seconds(1.5));
                p2.setOnFinished(e2 -> writeSnapshot(scene, snap));
                p2.play();
            });
            pause.play();
        }
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

    /** 把当前 Scene 渲染成 PNG（开发自检用，借助 java.desktop 的 ImageIO）。 */
    private static void writeSnapshot(Scene scene, String path) {
        try {
            javafx.scene.image.WritableImage img = scene.snapshot(null);
            int w = (int) img.getWidth();
            int h = (int) img.getHeight();
            var bi = new java.awt.image.BufferedImage(w, h, java.awt.image.BufferedImage.TYPE_INT_ARGB);
            var pr = img.getPixelReader();
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    bi.setRGB(x, y, pr.getArgb(x, y));
                }
            }
            javax.imageio.ImageIO.write(bi, "png", new java.io.File(path));
            System.out.println("[snapshot] " + path);
        } catch (Exception e) {
            System.err.println("[snapshot] failed: " + e);
        }
    }

    public static void main(String[] args) {
        launch(args);
    }
}
