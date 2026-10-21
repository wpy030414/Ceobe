using Ceobe.Core.Model;

namespace Ceobe.Core.Providers;

/// <summary>
/// 服务端 jar 提供者 SPI。
/// <para>每个实现屏蔽一个官方源（Mojang / PaperMC / Fabric / Purpur / NeoForge）的协议差异；
/// 外部 API 迁移时只需替换实现，不影响 core 其余部分（见 docs/DECISIONS.md ADR-004）。
/// 历史上 Paper Fill v2 → v3 整体迁移、v2 直接 410，所以 URL 与响应字段解析必须留在实现内部。</para>
/// <para>架构约定（对齐 docs/MIGRATION.md）：core 一律异步，Java 版为同步，语义等价但调用风格不同。</para>
/// </summary>
public interface IServerJarProvider
{
    ServerType Type { get; }

    /// <summary>可安装的游戏版本（仅正式版，降序）。</summary>
    Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default);

    /// <summary>指定游戏版本可用的 loader 版本（仅 Fabric / NeoForge 有意义；其余返回空列表）。
    /// C# 接口默认实现无需 default 关键字（与 Java 语法差异）。</summary>
    Task<IReadOnlyList<string>> ListLoaderVersionsAsync(string gameVersion, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    /// <summary>
    /// 将服务端安装进实例目录。
    /// </summary>
    /// <param name="instanceDir">实例目录（绝对或相对均可，内部会解析为绝对路径传给子进程）。</param>
    /// <param name="gameVersion">游戏版本。</param>
    /// <param name="loaderVersion">可空 = 最新 stable。</param>
    /// <param name="progress">可空 = 静默。</param>
    Task<InstalledServer> InstallAsync(
        string instanceDir,
        string gameVersion,
        string? loaderVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default);
}
