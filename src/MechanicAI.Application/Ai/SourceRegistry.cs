using System.Globalization;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Ai;

/// <summary>
/// Assigns citation labels ([S1], [S2]...) to sources as tools return them. Only sources
/// registered here — i.e. produced by real tool output — can be cited in an answer.
/// </summary>
public sealed class SourceRegistry
{
    private readonly List<SourceCitation> _sources = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<SourceCitation> Sources
    {
        get
        {
            lock (_lock) return _sources.ToList();
        }
    }

    public SourceCitation Register(SourceCitation source)
    {
        lock (_lock)
        {
            var existing = _sources.FirstOrDefault(s => SameSource(s, source));
            if (existing is not null) return existing;
            var copy = source.Clone();
            copy.Label = "S" + (_sources.Count + 1).ToString(CultureInfo.InvariantCulture);
            _sources.Add(copy);
            return copy;
        }
    }

    public SourceCitation? Find(string label)
    {
        lock (_lock) return _sources.FirstOrDefault(s => string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase));
    }

    public bool ContainsUrl(string url)
    {
        var normalized = NormalizeUrl(url);
        lock (_lock) return _sources.Any(s => s.Url is not null && NormalizeUrl(s.Url) == normalized);
    }

    public static SourceCitation Web(string title, string url, SourceType type, string? publisher, DateTime? published, string? excerpt, bool fromCache, DateTime retrieved) =>
        new()
        {
            Title = title,
            Url = url,
            Type = type,
            Publisher = publisher,
            PublishedUtc = published,
            Excerpt = excerpt,
            FromCache = fromCache,
            RetrievedUtc = retrieved,
        };

    internal static string NormalizeUrl(string url)
    {
        var u = url.Trim().TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return (host + uri.PathAndQuery).TrimEnd('/').ToLowerInvariant();
        }

        return u.TrimEnd('/').ToLowerInvariant();
    }

    private static bool SameSource(SourceCitation a, SourceCitation b)
    {
        if (a.Url is not null && b.Url is not null) return NormalizeUrl(a.Url) == NormalizeUrl(b.Url);
        if (a.ChunkId is not null && b.ChunkId is not null) return a.ChunkId == b.ChunkId;
        if (a.DocumentId is not null && b.DocumentId is not null) return a.DocumentId == b.DocumentId && a.PageNumber == b.PageNumber;
        return a.Url is null && b.Url is null && a.DocumentId is null && b.DocumentId is null &&
               string.Equals(a.Title, b.Title, StringComparison.Ordinal) && a.Type == b.Type;
    }
}
