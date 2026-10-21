namespace Ceobe.App.ViewModels;

/// <summary>
/// UI 线程亲和（ViewModel 层唯一允许知道「有个 UI 线程」的地方）。
///
/// 为什么需要：Core 的进度回调走的是自研 <c>IProgressSink</c>，**不是** BCL 的
/// <c>Progress&lt;T&gt;</c>——后者会自动把回调 marshal 回创建时的 SynchronizationContext，
/// 前者不会。于是下载线程直接调用 <c>IProgressSink.Update</c>，ViewModel 就地改
/// <c>StatusText</c>/<c>Progress</c>，<c>x:Bind</c> 随即在后台线程碰控件：
/// 实测报 <c>0x8001010E</c>（The application called an interface that was marshalled
/// for a different thread），界面直接卡死/崩溃。
///
/// 约定：ViewModel 里凡是**可能从后台线程**被调用的状态写入，一律经 <see cref="Post"/>。
///
/// 测试环境下没有捕获到上下文（<see cref="_context"/> 为 null），<see cref="Post"/>
/// 直接同步执行——这样 ViewModel 单测既不用起 UI 线程，也不会因线程切换而变慢或抖动。
/// </summary>
public static class UiThread
{
    private static SynchronizationContext? _context;

    /// <summary>在 UI 线程启动时调用一次（App.OnLaunched）。重复调用无害。</summary>
    public static void Capture() => _context = SynchronizationContext.Current;

    public static bool IsCaptured => _context is not null;

    /// <summary>把动作排到 UI 线程；已在 UI 线程或未捕获上下文时直接执行。</summary>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var ctx = _context;
        if (ctx is null || SynchronizationContext.Current == ctx)
        {
            action();
            return;
        }
        ctx.Post(static state => ((Action)state!)(), action);
    }
}
