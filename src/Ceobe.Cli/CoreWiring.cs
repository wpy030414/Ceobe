using Ceobe.Core.Composition;
using Ceobe.Core.Util;

namespace Ceobe.Cli;

/// <summary>
/// CLI 侧的装配薄封装：真实的接线逻辑住在 <see cref="CeobeComposition"/>（Core 的 composition root，
/// GUI 与 CLI 共用，保证行为一致），这里只保留 CLI 专属的表现层部分。
/// 23 个命令调用点统一走 <see cref="NewManager"/>，故保持该名字不变。
/// </summary>
public static class CoreWiring
{
    /// <summary>装配 InstanceManager（委托给 Core 的 composition root）。</summary>
    public static Core.Runtime.InstanceManager NewManager() => CeobeComposition.NewManager();

    /// <summary>
    /// 控制台进度器：把 stage 变化打印到 stdout（json 模式下静默）。
    /// 这是表现层关注点，故留在 CLI，不进 Core——GUI 会把同一个 <see cref="IProgressSink"/>
    /// 绑到进度条而非控制台。
    /// </summary>
    private sealed class ConsoleProgress : IProgressSink
    {
        private string? _lastStage;

        public void Update(string stage, long done, long total)
        {
            if (stage == _lastStage) return;
            _lastStage = stage;
            Output.Info("[ceobe] {0}", stage);
        }
    }

    /// <summary>控制台进度器。</summary>
    public static IProgressSink ConsoleSink() => new ConsoleProgress();
}
