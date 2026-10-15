package im.xrl.ceobe.core.provider;

/** 安装结果：启动器 jar 相对路径 + 实际选定的版本。 */
public record InstalledServer(String launchJar, String gameVersion, String loaderVersion) {
}
