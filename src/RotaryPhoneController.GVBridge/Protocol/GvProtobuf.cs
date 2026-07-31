using System.Text.Json;

namespace RotaryPhoneController.GVBridge.Protocol;

public static class GvProtobuf
{
    public static string? GetString(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    public static int? GetInt(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    }

    public static long? GetLong(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetInt64() : null;
    }

    public static JsonElement? GetArray(JsonElement root, params int[] path)
    {
        var current = root;
        foreach (var index in path)
        {
            if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                return null;
            current = current[index];
        }
        return current.ValueKind == JsonValueKind.Array ? current : null;
    }

    /// <summary>
    /// Build a positional JSON array request body. Nested <c>object?[]</c> values are written as
    /// nested arrays (recursively), which the api2thread/list body needs for its trailing
    /// <c>[null,1,1,1]</c> flags element.
    /// </summary>
    public static string BuildArray(params object?[] values)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        WriteArray(writer, values);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteArray(Utf8JsonWriter writer, object?[] values)
    {
        writer.WriteStartArray();
        foreach (var val in values)
        {
            switch (val)
            {
                case null: writer.WriteNullValue(); break;
                case object?[] nested: WriteArray(writer, nested); break;
                case string s: writer.WriteStringValue(s); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case int i: writer.WriteNumberValue(i); break;
                case long l: writer.WriteNumberValue(l); break;
                case JsonElement el: el.WriteTo(writer); break;
                default: writer.WriteStringValue(val.ToString()); break;
            }
        }
        writer.WriteEndArray();
    }
}
