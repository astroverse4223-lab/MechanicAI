using System.Text.Json;
using System.Text.Json.Nodes;
using MechanicAI.Infrastructure.Content;

namespace MechanicAI.Infrastructure.Tests.Content;

/// <summary>
/// Reads the embedded content JSON directly (bypassing the loader's validation, which silently
/// repairs or drops bad entries) so integrity tests see exactly what ships. Everything is
/// discovered from the manifest, so newly added content files are covered automatically.
/// </summary>
internal static class RawContent
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly System.Reflection.Assembly Assembly = typeof(EmbeddedContentProvider).Assembly;

    public static IReadOnlyList<string> ResourceNames(string folder, string extension = ".json") =>
        Assembly.GetManifestResourceNames()
            .Where(n => n.Replace('\\', '/').StartsWith($"Content/{folder}/", StringComparison.Ordinal)
                        && n.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<(string File, JsonObject Root)> Files(string folder) =>
        ResourceNames(folder).Select(name => (FileName(name), Parse(name))).ToList();

    public static string FileName(string resourceName) => resourceName.Replace('\\', '/').Split('/')[^1];

    private static JsonObject Parse(string resourceName)
    {
        using var stream = Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Missing resource {resourceName}");
        return JsonNode.Parse(stream, documentOptions: Options)?.AsObject()
               ?? throw new InvalidOperationException($"{resourceName} is empty");
    }

    public static IEnumerable<(string File, JsonObject Playbook)> Playbooks() =>
        Files("Playbooks").SelectMany(f => Array(f.Root, "playbooks").Select(p => (f.File, p.AsObject())));

    public static IEnumerable<(string File, JsonObject Code)> DtcCodes() =>
        Files("Dtc").SelectMany(f => Array(f.Root, "codes").Select(c => (f.File, c.AsObject())));

    public static IEnumerable<JsonNode> Array(JsonObject obj, string property) =>
        obj[property] is JsonArray array ? array.Where(n => n is not null).Select(n => n!) : [];

    public static IEnumerable<string> Strings(JsonObject obj, string property) =>
        Array(obj, property).Select(n => n.GetValue<string>());

    public static string Str(JsonObject obj, string property) => obj[property]?.GetValue<string>() ?? string.Empty;
}
