package im.xrl.ceobe.core.provider;

/** 安装进度回调。 */
@FunctionalInterface
public interface ProgressListener {
    ProgressListener SILENT = (stage, done, total) -> {
    };

    /** @param stage 阶段描述（中文）；@param done 已完成字节/-1；@param total 总量字节/-1。 */
    void update(String stage, long done, long total);
}
