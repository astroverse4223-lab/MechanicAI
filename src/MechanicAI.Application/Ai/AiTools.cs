using System.Text.Json;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;

namespace MechanicAI.Application.Ai;

/// <summary>Context shared by the tools during one agent run.</summary>
public sealed class ToolContext
{
    public required SourceRegistry Sources { get; init; }

    public Guid? VehicleId { get; init; }

    public Guid? SessionId { get; init; }

    public Guid? ConversationId { get; init; }

    /// <summary>Images attached to the user's message (referenced by index from analyze_image).</summary>
    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    /// <summary>True when the model producing tool calls is local (affects what private data tools may return).</summary>
    public bool ModelIsLocal { get; init; }
}

public sealed record ToolExecutionResult(string ContentJson, bool IsError, string Summary)
{
    public static ToolExecutionResult Ok(object payload, string summary) => new(Json.Serialize(payload), false, summary);

    public static ToolExecutionResult Fail(string message) =>
        new(Json.Serialize(new { error = message }), true, message);
}

/// <summary>
/// A capability the AI can invoke. Tools return real data (or an explicit error) — the
/// agent loop never lets the model supply tool output itself.
/// </summary>
public interface IAiTool
{
    string Name { get; }

    string Description { get; }

    JsonElement ParametersSchema { get; }

    /// <summary>True if the tool can return private knowledge-base content.</summary>
    bool ReturnsPrivateData => false;

    Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken);
}

/// <summary>Minimal JSON-Schema validation for tool inputs (required fields, primitive types, enums).</summary>
public static class ToolInputValidator
{
    public static string? Validate(JsonElement schema, string argumentsJson, out JsonElement arguments)
    {
        arguments = default;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException)
        {
            return "Tool input was not valid JSON.";
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "Tool input must be a JSON object.";
            arguments = doc.RootElement.Clone();
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()).OfType<string>())
            {
                if (!arguments.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return $"Missing required field '{name}'.";
                }
            }
        }

        if (!schema.TryGetProperty("properties", out var properties)) return null;
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var definition)) continue;
            if (definition.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                var ok = type.GetString() switch
                {
                    "string" => property.Value.ValueKind == JsonValueKind.String,
                    "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out _),
                    "number" => property.Value.ValueKind == JsonValueKind.Number,
                    "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "array" => property.Value.ValueKind == JsonValueKind.Array,
                    "object" => property.Value.ValueKind == JsonValueKind.Object,
                    _ => true,
                };
                if (!ok && property.Value.ValueKind != JsonValueKind.Null) return $"Field '{property.Name}' must be of type {type.GetString()}.";
            }

            if (definition.TryGetProperty("enum", out var allowed) && property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                if (!allowed.EnumerateArray().Any(a => a.GetString() == value))
                {
                    return $"Field '{property.Name}' must be one of: {string.Join(", ", allowed.EnumerateArray().Select(a => a.GetString()))}.";
                }
            }
        }

        return null;
    }
}

public static class ToolArgs
{
    public static string? String(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static int? Int(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : null;

    public static bool? Bool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    public static Guid? Guid(JsonElement args, string name) =>
        System.Guid.TryParse(String(args, name), out var g) ? g : null;

    public static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema, Json.Options);
}
