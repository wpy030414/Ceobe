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

    @Override
    public void start(Stage stage) {
        InstanceManager manager = new InstanceManager();
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
        stage.setOnCloseRequest(e -> shutdown());
        stage.show();
        view.refresh();
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
