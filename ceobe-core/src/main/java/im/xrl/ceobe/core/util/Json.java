package im.xrl.ceobe.core.util;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;
import com.fasterxml.jackson.datatype.jsr310.JavaTimeModule;

/** 全局 Jackson 单例。 */
public final class Json {
    private static final ObjectMapper MAPPER = new ObjectMapper()
            .registerModule(new JavaTimeModule())
            .disable(SerializationFeature.WRITE_DATES_AS_TIMESTAMPS)
            .enable(SerializationFeature.INDENT_OUTPUT);

    public static ObjectMapper mapper() {
        return MAPPER;
    }

    public static JsonNode parse(String text) {
        try {
            return MAPPER.readTree(text);
        } catch (Exception e) {
            throw new CeobeException("JSON 解析失败: " + e.getMessage(), e);
        }
    }

    public static <T> T parse(String text, Class<T> type) {
        try {
            return MAPPER.readValue(text, type);
        } catch (Exception e) {
            throw new CeobeException("JSON 反序列化失败: " + e.getMessage(), e);
        }
    }

    public static String write(Object value) {
        try {
            return MAPPER.writeValueAsString(value);
        } catch (Exception e) {
            throw new CeobeException("JSON 序列化失败: " + e.getMessage(), e);
        }
    }

    private Json() {
    }
}
