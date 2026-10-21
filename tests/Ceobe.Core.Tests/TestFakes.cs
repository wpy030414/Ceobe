using Ceobe.Core.Model;
using Ceobe.Core.Runtime;
using Ceobe.Core.Util;

namespace Ceobe.Core.Tests;

/// <summary>
/// 测试假件集合。
/// </summary>
public static class TestFakes
{
    /// <summary>
    /// 离线假安装器：不联网、不下载，只在实例目录落一个占位 server.jar。
    /// 用于验证 InstanceManager 的创建流程本身（端口排他、eula/properties 落盘、meta 写入），
    /// 不验证 provider 的下载逻辑。
    /// </summary>
    public static JarInstaller OfflineJarInstaller() => OfflineInstall;

    private static Task<JarInstallResult> OfflineInstall(ServerType serverType, string instanceDir,
        string gameVersion, string? loaderVersion, IProgressSink progress, CancellationToken ct)
    {
        progress.Update($"假安装 {gameVersion}", 0, -1);
        Directory.CreateDirectory(instanceDir);
        // 占位 jar：内容无关，ServerProcess.Start 只检查文件存在性
        File.WriteAllText(Path.Combine(instanceDir, "server.jar"), "PK\u0003\u0004-fake-jar-for-test");
        progress.Update("创建完成", 1, 1);
        return Task.FromResult(new JarInstallResult("server.jar", gameVersion, loaderVersion, null));
    }

    /// <summary>回一个「已安装版本与请求不同」的安装器，用于验证 CreateAsync 以 provider 返回值为准。</summary>
    public static JarInstaller VersionCorrectingInstaller(string correctedVersion) =>
        (serverType, instanceDir, gameVersion, loaderVersion, progress, ct) =>
        {
            Directory.CreateDirectory(instanceDir);
            File.WriteAllText(Path.Combine(instanceDir, "server.jar"), "fake");
            return Task.FromResult(new JarInstallResult("server.jar", correctedVersion, loaderVersion, null));
        };

    /// <summary>回参数文件启动形态的安装器（NeoForge 场景，ADR-010）。
    /// 参数文件通常带子目录（libraries/neoforge/win_args.txt），故必须先建父目录。</summary>
    public static JarInstaller ArgsFileInstaller(string argsFile) =>
        (serverType, instanceDir, gameVersion, loaderVersion, progress, ct) =>
        {
            Directory.CreateDirectory(instanceDir);
            var target = Path.Combine(instanceDir, argsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "fake-args");
            return Task.FromResult(new JarInstallResult(argsFile, gameVersion, loaderVersion, argsFile));
        };

    /// <summary>不联网、不探测的假 Java 定位器（占位路径，不保证存在）。</summary>
    public sealed class StubJavaLocator : IJavaLocator
    {
        public string? LastJavaHome { get; private set; }
        public string? LastGameVersion { get; private set; }

        public string ResolveLauncher(string? javaHome, string gameVersion)
        {
            LastJavaHome = javaHome;
            LastGameVersion = gameVersion;
            return OperatingSystem.IsWindows() ? @"C:\jdk\bin\java.exe" : "/usr/bin/java";
        }
    }

    /// <summary>记录事件的假宿主：用于验证 TunnelManager 与 runtime 的交互而不碰真实目录。</summary>
    public sealed class RecordingHost : IInstanceHost
    {
        private readonly Dictionary<string, InstanceStatus> _byName = new(StringComparer.Ordinal);

        public List<(string Instance, string Kind, string? Url)> Tunnels { get; } = [];
        public List<(string Instance, string Line)> Logs { get; } = [];

        public void Add(InstanceStatus status) => _byName[status.Name] = status;

        public InstanceStatus Require(string name) =>
            _byName.TryGetValue(name, out var s)
                ? s
                : throw CeobeException.Of("实例不存在: {0}", name);

        public string Dir(string name) =>
            Path.Combine(Path.GetTempPath(), "ceobe-fake-" + name);

        public void RememberTunnel(string name, string kindId, string? publicUrl) =>
            Tunnels.Add((name, kindId, publicUrl));

        public void TunnelLog(string name, string line) => Logs.Add((name, line));
    }
}
