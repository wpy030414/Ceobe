using Ceobe.App.ViewModels;
using Xunit;

namespace Ceobe.App.ViewModels.Tests;

/// <summary>
/// <see cref="UiThread"/> 的行为契约。
///
/// 这条契约是「进度回调不得跨线程碰控件」的唯一保障：Core 的 <c>IProgressSink</c>
/// 不做 marshal，ViewModel 必须自己经 <see cref="UiThread.Post"/> 回 UI 线程。
/// 若这里退化成「总是同步执行」，下载线程就会直接改绑定属性——
/// 实测后果是 WinUI 抛 0x8001010E 并卡死界面。
/// </summary>
public sealed class UiThreadTest
{
    /// <summary>记录 Post 的同步上下文：只排队、不自动执行，便于断言「还没跑」。</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _pending = [];

        public override void Post(SendOrPostCallback d, object? state) => _pending.Add((d, state));

        public void RunPending()
        {
            foreach (var (cb, st) in _pending) cb(st);
            _pending.Clear();
        }
    }

    /// <summary>
    /// 清掉 UiThread 捕获的上下文。
    /// 注意 xUnit 自己会给测试挂 AsyncTestSyncContext，所以必须显式 Capture 一个 null 进去，
    /// 否则测试进程里 UiThread 会误以为「已经有 UI 线程了」。
    /// </summary>
    private static void ClearCaptured()
    {
        var prev = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            UiThread.Capture();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }

    /// <summary>测试进程没有 UI 线程：未捕获上下文时必须同步执行，否则单测会挂住。</summary>
    [Fact(DisplayName = "未捕获上下文时 Post 同步执行")]
    public void 未捕获上下文时同步执行()
    {
        ClearCaptured();
        Assert.False(UiThread.IsCaptured);

        var ran = false;
        UiThread.Post(() => ran = true);
        Assert.True(ran);
        Assert.False(UiThread.IsCaptured);
    }

    /// <summary>捕获上下文后，从后台线程发起的 Post 只能排队，不能就地执行。</summary>
    [Fact(DisplayName = "捕获上下文后从后台线程调用只排队")]
    public async Task 捕获上下文后从后台线程只排队()
    {
        var prev = SynchronizationContext.Current;
        var ctx = new QueueContext();
        try
        {
            // 只在这两行里让 ctx 成为「当前上下文」，捕获完立刻还回去。
            // 不还回去的话，下面 await 的续体会被投递到 QueueContext，
            // 而它只排队不执行 → 测试永久挂死（本次实测踩过）。
            SynchronizationContext.SetSynchronizationContext(ctx);
            UiThread.Capture();
            SynchronizationContext.SetSynchronizationContext(prev);
            Assert.True(UiThread.IsCaptured);

            var ran = false;
            // 线程池线程上调用：此刻 Current 是 null，与 ctx 不同 → 必须排队
            await Task.Run(() => UiThread.Post(() => ran = true));

            Assert.False(ran);   // 只是排队，尚未执行
            ctx.RunPending();
            Assert.True(ran);
        }
        finally
        {
            // 先把 UiThread 的静态上下文复位成 null，再把 xUnit 的上下文还回去
            SynchronizationContext.SetSynchronizationContext(null);
            UiThread.Capture();
            SynchronizationContext.SetSynchronizationContext(prev);
        }
    }
}
