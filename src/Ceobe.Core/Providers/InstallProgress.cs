namespace Ceobe.Core.Providers;

/// <summary>
/// 安装进度上报载荷（对齐 Java <c>ProgressListener.update(stage, done, total)</c> 的三参数语义）。
/// </summary>
/// <param name="Stage">阶段描述（中文），面向用户展示。</param>
/// <param name="Done">已完成字节；-1 表示未知。</param>
/// <param name="Total">总量字节；-1 表示未知。</param>
/// <remarks>
/// 语义说明：任务要求 provider 的下载进度与 <c>HttpUtil</c> 的 <c>IProgress&lt;(long,long)&gt;</c> 对齐，
/// 但 Java 侧的进度回调带有「字符串消息形态」的 stage。按硬约束 #5「字符串形态保留为独立类型，别硬塞」，
/// 这里把 stage 与字节进度组成独立 record，而非塞进裸元组；对 HttpUtil 的桥接在 provider 内部完成。
/// </remarks>
public sealed record InstallProgress(string Stage, long Done, long Total);
