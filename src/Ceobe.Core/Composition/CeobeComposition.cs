using Ceobe.Core.Jdk;
using Ceobe.Core.Model;
using Ceobe.Core.Providers;
using Ceobe.Core.Runtime;
using Ceobe.Core.Tunnel;
using Ceobe.Core.Util;
// 命名空间 Ceobe.Core.WebConsole 与其中的类 WebConsole 同名，不加别名就得写成
// WebConsole.WebConsole（脆弱且易误读）
using ConsoleHost = Ceobe.Core.WebConsole.WebConsole;

namespace Ceobe.Core.Composition;

/// <summary>
/// Core 各层的装配入口（composition root）。
///
/// 为什么住在 Core 而不是 CLI：C# 版刻意用依赖倒置切断了 runtime ↔ providers/jdk/tunnel 的
/// 编译期耦合（见 <c>IRuntimePorts.cs</c>），所以必须由某个地方负责接线。接线逻辑是
/// **产品行为**（哪个 ServerType 用哪个 provider、JDK 决策链怎么走、隧道何时建），
/// GUI 与 CLI 必须完全一致；放在 CLI 里会迫使 GUI 依赖 CLI。
///
/// 与 UI 无关的部分全部在此；「进度怎么显示」这类表现层关注点由调用方注入
/// <see cref="IProgressSink"/>（CLI 打印到控制台，GUI 绑到进度条）。
/// </summary>
public static class CeobeComposition
{
    /// <summary>把 Core 的 <see cref="IProgressSink"/> 适配成 provider 需要的 <see cref="IProgress{T}"/>。</summary>
    private sealed class ProgressAdapter(IProgressSink sink) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => sink.Update(value.Stage, value.Done, value.Total);
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
            // cacheWarm 必须传下去：冷缓存时 required 只是兜底猜测，不能当硬性要求来判断
            var r = JavaResolver.Resolve(gameVersion, required, javaHome, default, cacheWarm);
            if (!r.Ok || string.IsNullOrEmpty(r.JavaHome))
            {
                var hint = cacheWarm
                    ? $"未找到 Java {required} 运行时；请先执行 `ceobe jdk install {required}`，或在设置中指定 JDK 目录"
                    // 缓存冷 → required 是兜底值，不能断言版本不符，提示要留余地
                    : "未找到可用的 Java 运行时；请先执行 `ceobe jdk install 21`"
                      + $"（或用 `ceobe jdk required {gameVersion}` 查询所需主版本），也可手动指定 JDK 目录";
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

    /// <summary>
    /// 启动应用级服务：内嵌 Web 控制台 + 隧道管理器。
    /// GUI 进程本身就是所启动实例的监督进程（对齐 Java 版 CeobeGui.start）。
    /// </summary>
    /// <returns>Web 控制台启动失败时 Web 为 null（对齐 Java 版 try/catch 后 webConsole = null）。</returns>
    public static (TunnelManager Tunnels, ConsoleHost? Web) StartAppServices(InstanceManager manager)
    {
        var tunnels = new TunnelManager(manager);
        ConsoleHost? web;
        try
        {
            web = new ConsoleHost(manager, 0);
            tunnels.WebConsolePort(web.Port);
        }
        catch (CeobeException)
        {
            // Web 控制台起不来（端口被占等）不应阻止整个 GUI 启动
            web = null;
        }
        return (tunnels, web);
    }
}
