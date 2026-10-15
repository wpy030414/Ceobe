package im.xrl.ceobe.core.jdk;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

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
}
