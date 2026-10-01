using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace X4MP.Protocol.Tests;

/// <summary>
/// Language-neutral dump of a decoded FlatBuffers object-API value, embedded in index.json as each frame
/// vector's <c>fields</c> so other implementations (C++, Python) can compare decoded values field by field.
/// <para>
/// Rules (the C++ reflection walker in protocol/cpp/tests implements the same ones): keys are the
/// object-API property names (the .fbs field name in UpperCamelCase); bool as JSON bool; every integer and
/// enum as its numeric value; float and double as the exact double value; string as string; <c>[ubyte]</c>
/// as a lower-case hex string; vectors as arrays; structs and tables as objects; a union as
/// <c>{"type": n, "value": {...}}</c> (value omitted for type NONE). A null field (absent vector, string,
/// table or union) is omitted.
/// </para>
/// </summary>
public static class CanonicalFields
{
    public static string Serialize(object value)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            Write(w, value);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(Utf8JsonWriter w, object v)
    {
        switch (v)
        {
            case bool b: w.WriteBooleanValue(b); return;
            case string s: w.WriteStringValue(s); return;
            case byte[] bytes: w.WriteStringValue(Convert.ToHexString(bytes).ToLowerInvariant()); return;
            case IList<byte> bl: w.WriteStringValue(Convert.ToHexString(bl.ToArray()).ToLowerInvariant()); return;
            case float f: w.WriteNumberValue((double)f); return;
            case double d: w.WriteNumberValue(d); return;
            case Enum e: Write(w, Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()))); return;
            case sbyte x: w.WriteNumberValue(x); return;
            case byte x: w.WriteNumberValue(x); return;
            case short x: w.WriteNumberValue(x); return;
            case ushort x: w.WriteNumberValue(x); return;
            case int x: w.WriteNumberValue(x); return;
            case uint x: w.WriteNumberValue(x); return;
            case long x: w.WriteNumberValue(x); return;
            case ulong x: w.WriteNumberValue(x); return;
            case IList list:
                w.WriteStartArray();
                foreach (var item in list)
                    Write(w, item);
                w.WriteEndArray();
                return;
        }

        var t = v.GetType();
        w.WriteStartObject();
        if (t.Name.EndsWith("Union", StringComparison.Ordinal))
        {
            var type = t.GetProperty("Type")!.GetValue(v)!;
            w.WritePropertyName("type");
            Write(w, type);
            var value = t.GetProperty("Value")!.GetValue(v);
            if (value is not null)
            {
                w.WritePropertyName("value");
                Write(w, value);
            }
        }
        else
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (p.GetIndexParameters().Length != 0)
                    continue;
                var value = p.GetValue(v);
                if (value is null)
                    continue;
                w.WritePropertyName(p.Name);
                Write(w, value);
            }
        }
        w.WriteEndObject();
    }
}
