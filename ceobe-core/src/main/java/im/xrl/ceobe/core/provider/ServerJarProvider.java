package im.xrl.ceobe.core.provider;

import im.xrl.ceobe.core.model.ServerType;

import java.nio.file.Path;
import java.util.List;

/**
 * 服务端 jar 提供者 SPI。
 * <p>每个实现屏蔽一个官方源（Mojang / PaperMC / Fabric / Purpur）的协议差异；
 * 外部 API 迁移时只需替换实现，不影响 core 其余部分（见 docs/DECISIONS.md ADR-004）。
 */
public interface ServerJarProvider {

    ServerType type();

    /** 可安装的游戏版本（仅正式版，降序）。 */
    List<String> listVersions();

    /** 指定游戏版本可用的 loader 版本（仅 Fabric 有意义；其余返回空列表）。 */
    default List<String> listLoaderVersions(String gameVersion) {
        return List.of();
    }

    /**
     * 将服务端安装进实例目录。
     *
     * @param loaderVersion 可空 = 最新 stable
     */
    InstalledServer install(Path instanceDir, String gameVersion, String loaderVersion,
                            ProgressListener progress);
}
