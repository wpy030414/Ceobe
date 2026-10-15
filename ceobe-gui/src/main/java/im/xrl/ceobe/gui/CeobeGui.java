package im.xrl.ceobe.gui;

import javafx.application.Application;
import javafx.scene.Scene;
import javafx.scene.control.Label;
import javafx.scene.layout.StackPane;
import javafx.stage.Stage;

/**
 * ceobe GUI 入口。shadcn 风格主题与视图逐步替换此占位。
 */
public final class CeobeGui extends Application {

    @Override
    public void start(Stage stage) {
        var root = new StackPane(new Label("ceobe"));
        stage.setScene(new Scene(root, 960, 640));
        stage.setTitle("ceobe");
        stage.show();
    }

    public static void main(String[] args) {
        launch(args);
    }
}
