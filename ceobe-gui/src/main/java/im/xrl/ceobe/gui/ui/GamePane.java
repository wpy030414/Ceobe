package im.xrl.ceobe.gui.ui;

import im.xrl.ceobe.core.config.PropSchema;
import im.xrl.ceobe.core.runtime.InstanceManager;
import im.xrl.ceobe.core.runtime.InstanceStatus;
import javafx.geometry.Insets;
import javafx.scene.Node;
import javafx.scene.layout.BorderPane;
import javafx.scene.layout.VBox;

/** 「游戏配置」页：server.properties 全部键（基础/游戏规则/世界/网络/性能/安全/高级）。 */
public final class GamePane extends PropsPaneBase {

    private final BorderPane root = new BorderPane();

    public GamePane(InstanceManager manager, InstanceStatus status) {
        super(manager, status);
        build();
    }

    public Node node() {
        return root;
    }

    private void build() {
        VBox sections = new VBox(14);
        sections.setPadding(new Insets(16));

        if (propsExist()) {
            for (String group : PropSchema.groups()) {
                Node card = groupCard(group);
                if (card != null) sections.getChildren().add(card);
            }
            Node unknown = unknownCard();
            if (unknown != null) sections.getChildren().add(unknown);
        } else {
            sections.getChildren().add(muted("尚无 server.properties（先启动一次服务器后即出现）"));
        }

        save.setText("保存");
        root.setCenter(scroll(sections));
        root.setBottom(actionBar());
    }
}
