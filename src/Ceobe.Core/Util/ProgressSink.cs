namespace Ceobe.Core.Util;

/// <summary>
/// 带阶段的进度上报（等价 Java 版 <c>provider/ProgressListener.update(String,long,long)</c>）。
/// total 为 -1 表示未知。用接口而非 <c>IProgress&lt;T&gt;</c> 是为了保住 stage 文本语义，
/// 同时让 GUI/CLI 都能把它接到进度条与日志行。
/// </summary>
public interface IProgressSink
{
    void Update(string stage, long done, long total);
}

/// <summary>默认空实现（CLI 非 verbose 模式与单测用）。</summary>
public sealed class NullProgress : IProgressSink
{
    public static readonly NullProgress Instance = new();
    public void Update(string stage, long done, long total) { }
}

/// <summary>把 <see cref="IProgressSink"/> 适配到 BCL 的 <c>IProgress&lt;(long,long)&gt;</c>（供 HttpUtil 用）。</summary>
public sealed class ProgressSinkAdapter(string stage, IProgressSink sink) : IProgress<(long Done, long Total)>
{
    public void Report((long Done, long Total) value) => sink.Update(stage, value.Done, value.Total);
}
