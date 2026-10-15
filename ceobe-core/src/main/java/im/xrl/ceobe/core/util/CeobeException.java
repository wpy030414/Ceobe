package im.xrl.ceobe.core.util;

/** 统一业务异常：message 面向用户可直接展示。 */
public class CeobeException extends RuntimeException {
    public CeobeException(String message) {
        super(message);
    }

    public CeobeException(String message, Throwable cause) {
        super(message, cause);
    }

    public static CeobeException of(String fmt, Object... args) {
        return new CeobeException(fmt.formatted(args));
    }
}
