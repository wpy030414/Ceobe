using Ceobe.Core.Client;
using Xunit;

namespace Ceobe.Core.Tests.Client;

/// <summary>
/// 版本 JSON 的 natives 布局（2026-10-22 修 26.3 客户端启动崩溃时新增）。
/// 新格式把系统 native 放在 ${natives_directory}/java；老格式直接用根目录。
/// </summary>
public class ClientNativeLayoutTest
{
    [Fact]
    public void NewFormat_UsesJavaSubdirectory()
    {
        string[] jvm =
        [
            "-Djava.library.path=${natives_directory}/java",
            "-Djna.tmpdir=${natives_directory}/jna",
            "-Dorg.lwjgl.system.SharedLibraryExtractPath=${natives_directory}/lwjgl",
            "-Dio.netty.native.workdir=${natives_directory}/netty",
        ];

        Assert.Equal("java", ClientVersion.NativeLibrarySubdir(jvm));
        Assert.Equal(["java", "jna", "lwjgl", "netty"], ClientVersion.NativeSubdirs(jvm));
    }

    [Fact]
    public void OldFormat_UsesRoot()
    {
        string[] jvm = ["-Djava.library.path=${natives_directory}"];
        Assert.Null(ClientVersion.NativeLibrarySubdir(jvm));
        Assert.Empty(ClientVersion.NativeSubdirs(jvm));
    }

    [Fact]
    public void MissingJvmArgs_FallsBackToRoot()
    {
        Assert.Null(ClientVersion.NativeLibrarySubdir([]));
        Assert.Empty(ClientVersion.NativeSubdirs([]));
    }
}
