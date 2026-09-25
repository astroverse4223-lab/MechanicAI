using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MechanicAI.Application.Common;

/// <summary>Shared serializer options so every layer reads and writes JSON the same way.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);

    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    /// <summary>Case-insensitive options for reading third-party payloads and content files.</summary>
    public static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    private static JsonSerializerOptions Create(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, Lenient);
    }

    public static bool TryDeserialize<T>(string? json, out T? value)
    {
        try
        {
            value = Deserialize<T>(json);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Extracts the first complete JSON object or array from model output that may be
    /// wrapped in prose or markdown code fences. Returns null if none is found.
    /// </summary>
    public static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = s.IndexOf('\n', fence);
            var end = start < 0 ? -1 : s.IndexOf("```", start, StringComparison.Ordinal);
            if (start >= 0 && end > start)
            {
                var inner = s[(start + 1)..end].Trim();
                if (inner.StartsWith('{') || inner.StartsWith('[')) s = inner;
            }
        }

        var open = s.IndexOfAny(['{', '[']);
        if (open < 0) return null;
        var openChar = s[open];
        var closeChar = openChar == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == openChar) depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0) return s[open..(i + 1)];
            }
        }

        return null;
    }
}
