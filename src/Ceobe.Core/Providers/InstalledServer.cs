namespace Ceobe.Core.Providers;

/// <summary>
/// 安装结果：启动器 jar（或参数文件）相对路径 + 实际选定的版本。对齐 Java <c>InstalledServer</c> record。
/// </summary>
/// <param name="LaunchJar">相对实例目录的启动 jar 路径；NeoForge 情形与 <see cref="LaunchArgsFile"/> 同值。</param>
/// <param name="GameVersion">实际安装的游戏版本。</param>
/// <param name="LoaderVersion">实际选定的 loader 版本；可空。</param>
/// <param name="LaunchArgsFile">非空时改用参数文件启动（NeoForge）：<c>java @&lt;file&gt; nogui</c>。</param>
public sealed record InstalledServer(
    string LaunchJar,
    string GameVersion,
    string? LoaderVersion,
    string? LaunchArgsFile)
{
    /// <summary>常规（-jar 启动）安装：无参数文件。</summary>
    public InstalledServer(string launchJar, string gameVersion, string? loaderVersion)
        : this(launchJar, gameVersion, loaderVersion, null)
    {
    }
}
