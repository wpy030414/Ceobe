package im.xrl.ceobe.core.provider;

/** 安装结果：启动器 jar（或参数文件）相对路径 + 实际选定的版本。 */
public record InstalledServer(String launchJar, String gameVersion, String loaderVersion,
                              String launchArgsFile) {

    /** 常规（-jar 启动）安装：无参数文件。 */
    public InstalledServer(String launchJar, String gameVersion, String loaderVersion) {
        this(launchJar, gameVersion, loaderVersion, null);
    }
}
