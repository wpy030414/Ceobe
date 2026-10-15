package im.xrl.ceobe.core.model;

/** 实例运行状态机：CREATED→STARTING→RUNNING→STOPPING→STOPPED；异常退出→CRASHED。 */
public enum InstanceState {
    CREATED("已创建"),
    STARTING("启动中"),
    RUNNING("运行中"),
    STOPPING("停止中"),
    STOPPED("已停止"),
    CRASHED("崩溃");

    private final String zhLabel;

    InstanceState(String zhLabel) {
        this.zhLabel = zhLabel;
    }

    public String zhLabel() {
        return zhLabel;
    }

    public boolean isLive() {
        return this == STARTING || this == RUNNING || this == STOPPING;
    }
}
