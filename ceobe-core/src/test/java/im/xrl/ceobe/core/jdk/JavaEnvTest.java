package im.xrl.ceobe.core.jdk;

import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Java 主版本解析。 */
class JavaEnvTest {

    @Test
    void 解析主版本() {
        assertEquals(25, JavaEnv.parseMajor("25.0.1"));
        assertEquals(21, JavaEnv.parseMajor("21.0.12.1"));
        assertEquals(17, JavaEnv.parseMajor("17.0.20.1"));
        assertEquals(8, JavaEnv.parseMajor("1.8.0_402"));
        assertEquals(21, JavaEnv.parseMajor("21-ea"));
        assertEquals(0, JavaEnv.parseMajor("abc"));
        assertEquals(0, JavaEnv.parseMajor(null));
    }

    @Test
    void 仅带java启动器的目录才可作运行时() throws Exception {
        // 当前测试 JVM 的 java.home 自带 bin/java，必须判定可用
        assertTrue(JavaEnv.hasLauncher(JavaEnv.runtimeHome()));

        // jpackage 打包镜像会剥离 bin/java：只有空 bin 目录时不可用
        Path stripped = Files.createTempDirectory("ceobe-jdk-stripped");
        Files.createDirectories(stripped.resolve("bin"));
        assertFalse(JavaEnv.hasLauncher(stripped));
        assertFalse(JavaEnv.hasLauncher(stripped.resolve("nope")));
        assertFalse(JavaEnv.hasLauncher(null));
    }
}
