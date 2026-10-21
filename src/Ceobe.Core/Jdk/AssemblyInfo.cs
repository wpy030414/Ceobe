using System.Runtime.CompilerServices;

// Jdk 层的 internal 成员（解压流水线、平台映射）暴露给测试工程直接喂假归档验证，
// 避免为了单测把内部实现提为 public 污染 API 面（任务约束：不下载真实 JDK）。
[assembly: InternalsVisibleTo("Ceobe.Core.Tests")]
