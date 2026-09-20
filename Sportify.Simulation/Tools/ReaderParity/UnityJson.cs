using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// What Unity's JsonUtility does to a layout, on System.Text.Json, so the Unity project's layout readers can run without Unity.
///
///   - only public fields count, matched by exact name; unknown keys are ignored;
///   - numbers become float where the field is float (System.Text.Json parses them the same way); a JSON null, or a value of the wrong kind, for a number or
///     a bool leaves the default (0 / false) instead of failing;
///   - a nested class (and every element of an array) is never null after reading: a missing or null object is a default instance, a null string is "";
///   - an array or list that is missing is either null or empty: the documentation does not settle which, so both are emulated and a reader must give the
///     same answer under each (ArrayMode).
///
/// Checked against real Unity output by ReaderParity (fixtures/golden-unity) on every run.
/// </summary>
internal static class UnityJson
{
    public enum ArrayMode { Null, Empty }

    static readonly JsonSerializerOptions Options = Build();

    static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = false, ReadCommentHandling = JsonCommentHandling.Skip };
        o.Converters.Add(new Lenient<float>((ref Utf8JsonReader r) => r.GetSingle()));
        o.Converters.Add(new Lenient<double>((ref Utf8JsonReader r) => r.GetDouble()));
        o.Converters.Add(new Lenient<int>((ref Utf8JsonReader r) => r.TryGetInt32(out var v) ? v : (int)r.GetDouble()));
        o.Converters.Add(new Lenient<long>((ref Utf8JsonReader r) => r.TryGetInt64(out var v) ? v : (long)r.GetDouble()));
        o.Converters.Add(new LenientBool());
        return o;
    }

    delegate T NumberReader<T>(ref Utf8JsonReader reader);

    sealed class Lenient<T> : JsonConverter<T> where T : struct
    {
        readonly NumberReader<T> _read;
        public Lenient(NumberReader<T> read) { _read = read; }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number) return _read(ref reader);
            reader.Skip();                                     // null, a string, an object: the field keeps its default
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, typeof(T), new JsonSerializerOptions());
        }
    }

    sealed class LenientBool : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            reader.Skip();
            return false;
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    public static T FromJson<T>(string json, ArrayMode arrays) where T : class, new()
    {
        var obj = JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        Fill(obj, arrays, 0);
        return obj;
    }

    static bool IsPlainClass(Type t) => t.IsClass && t != typeof(string) && !t.IsArray && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) && t.GetConstructor(Type.EmptyTypes) != null;

    /// <summary>Makes an object look like what JsonUtility hands back: no null nested objects or strings, arrays as the mode says.</summary>
    static void Fill(object obj, ArrayMode arrays, int depth)
    {
        if (depth > 12) return;
        foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.IsStatic || f.IsInitOnly) continue;
            var t = f.FieldType;
            var value = f.GetValue(obj);
            if (t == typeof(string)) { if (value == null) f.SetValue(obj, ""); continue; }
            if (t.IsArray)
            {
                var elem = t.GetElementType()!;
                if (value == null)
                {
                    if (arrays == ArrayMode.Empty) f.SetValue(obj, Array.CreateInstance(elem, 0));
                    continue;
                }
                if (IsPlainClass(elem))
                    foreach (var e in (Array)value)
                        if (e != null) Fill(e, arrays, depth + 1);
                continue;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (value == null) { if (arrays == ArrayMode.Empty) f.SetValue(obj, Activator.CreateInstance(t)); continue; }
                foreach (var e in (System.Collections.IEnumerable)value)
                    if (e != null && IsPlainClass(e.GetType())) Fill(e, arrays, depth + 1);
                continue;
            }
            if (IsPlainClass(t))
            {
                if (value == null) { value = Activator.CreateInstance(t)!; f.SetValue(obj, value); }
                Fill(value, arrays, depth + 1);
            }
        }
    }
}
