using Ceobe.Core.Model;
using Ceobe.Core.Providers;
using Ceobe.Core.Runtime;
using Ceobe.Core.Store;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
using Ceobe.Core.Jdk;

namespace Ceobe.Cli;

/// <summary>
/// 把 Core 各层装配起来（等价 Java 版 CLI 里散落在各命令中的 <c>new InstanceManager()</c>）。
///
/// 为什么需要这一层：C# 版刻意用依赖倒置切断了 runtime ↔ providers/jdk/tunnel 的编译期耦合
/// （见 <c>IRuntimePorts.cs</c>），所以必须由入口负责接线。GUI 与 CLI 各自装配一次，
/// 装配逻辑集中在此以保证两者行为一致。
/// </summary>
public static class CoreWiring
{
    /// <summary>把 Core 的 <see cref="IProgressSink"/> 适配成 provider 需要的 <see cref="IProgress{T}"/>。</summary>
    private sealed class ProgressAdapter(IProgressSink sink) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => sink.Update(value.Stage, value.Done, value.Total);
    }

    /// <summary>控制台进度：把 stage 变化打印出来，字节进度不打（太吵）。</summary>
    private sealed class ConsoleProgress : IProgressSink
    {
        private string? _lastStage;

        public void Update(string stage, long done, long total)
        {
            if (stage == _lastStage) return;
            _lastStage = stage;
            Output.Info("[ceobe] " + stage);
        }
    }

    /// <summary>provider 安装器：按 ServerType 选 provider，结果映射回 runtime 的本地形态。</summary>
    private static readonly JarInstaller ProviderJarInstaller =
        (serverType, instanceDir, gameVersion, loaderVersion, progress, ct) =>
            InstallViaProviderAsync(serverType, instanceDir, gameVersion, loaderVersion, progress, ct);

    private static async Task<JarInstallResult> InstallViaProviderAsync(
        ServerType serverType, string instanceDir, string gameVersion, string? loaderVersion,
        IProgressSink progress, CancellationToken ct)
    {
        var provider = ProviderRegistry.Of(serverType);
        var installed = await provider.InstallAsync(
                instanceDir, gameVersion, loaderVersion, new ProgressAdapter(progress), ct)
            .ConfigureAwait(false);
        return new JarInstallResult(
            installed.LaunchJar,
            installed.GameVersion,
            installed.LoaderVersion,
            installed.LaunchArgsFile);
    }

    /// <summary>
    /// Java 定位器：走 <see cref="JavaResolver"/> 的决策链（显式 &gt; 自管 JDK &gt; 宿主 JAVA_HOME）。
    /// 同步签名内用同步解析——<c>ResolveLauncher</c> 由 <c>ServerProcess</c> 在启动路径上调用，
    /// 那里不能 await；required 主版本从 <see cref="JavaRequirement"/> 缓存读（由 CreateAsync 预热）。
    /// </summary>
    private sealed class ResolverJavaLocator : IJavaLocator
    {
        public string ResolveLauncher(string? javaHome, string gameVersion)
        {
            var required = JavaRequirement.CachedMajorOrFallback(gameVersion);
            var cacheWarm = JavaRequirement.IsCached(gameVersion);
            var r = JavaResolver.Resolve(gameVersion, required, javaHome);
            if (!r.Ok || string.IsNullOrEmpty(r.JavaHome))
            {
                var hint = cacheWarm
                    ? $"未找到 Java {required} 运行时；请先 `ceobe jdk install {required}` 或用 --java-home 指定"
                    // 缓存冷 → required 是兜底值，不能断言版本不符，提示要留余地
                    : "未找到可用的 Java 运行时；请先 `ceobe jdk install 21`（或按 `ceobe jdk required "
                      + $"{gameVersion}` 查询所需主版本），也可用 --java-home 指定";
                throw new CeobeException(r.Message ?? hint);
            }
            return JavaEnv.LauncherOf(r.JavaHome);
        }

        /// <summary>真实预热：联网查 piston-meta 拿该 MC 版本的 javaVersion.majorVersion。</summary>
        public Task WarmRequirementCacheAsync(string gameVersion, CancellationToken ct = default) =>
            JavaRequirement.RequiredMajorAsync(gameVersion, ct);
    }

    /// <summary>装配一个可用的 InstanceManager（含 provider 安装器 + Java 定位器）。</summary>
    public static InstanceManager NewManager() =>
        new(new ResolverJavaLocator(), ProviderJarInstaller);

    /// <summary>
    /// 为受托管实例装配隧道管理器并开启控制通道（监督进程用）。
    /// 隧道依赖 <see cref="IInstanceHost"/>=manager，故必须后建；控制通道按 Java 版
    /// <c>ControlServer(process, dir, name, tunnels)</c> 由调用方把两者一起传入。
    /// </summary>
    public static (TunnelManager Tunnels, ControlServer Control) StartControlled(
        InstanceManager manager, string instanceName)
    {
        var tunnels = new TunnelManager(manager);
        var control = manager.OpenControl(instanceName, tunnels);
        return (tunnels, control);
    }

    /// <summary>控制台进度器。</summary>
    public static IProgressSink ConsoleSink() => new ConsoleProgress();
}
