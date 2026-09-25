using MechanicAI.Domain.Enums;

namespace MechanicAI.Domain.ValueObjects;

/// <summary>
/// A reference to where a piece of information came from. Citations are only ever
/// created from real tool output (a fetched URL, an uploaded document page, a database
/// record) — never from model-generated text.
/// </summary>
public sealed class SourceCitation
{
    /// <summary>Short label used in text, e.g. "S1" for web sources or "D2" for documents.</summary>
    public string Label { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Url { get; set; }

    public SourceType Type { get; set; } = SourceType.Unknown;

    public string? Publisher { get; set; }

    public DateTime? PublishedUtc { get; set; }

    public DateTime RetrievedUtc { get; set; } = DateTime.UtcNow;

    public string? Excerpt { get; set; }

    public Guid? DocumentId { get; set; }

    public int? PageNumber { get; set; }

    public Guid? ChunkId { get; set; }

    /// <summary>True when the source was served from the offline cache rather than fetched live.</summary>
    public bool FromCache { get; set; }

    public SourceCitation Clone() => (SourceCitation)MemberwiseClone();
}
