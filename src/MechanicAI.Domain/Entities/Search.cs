using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Domain.Entities;

public class SearchHistoryEntry : Entity
{
    public string Query { get; set; } = string.Empty;

    public SearchIntent Intent { get; set; }

    public int ResultCount { get; set; }

    public DateTime SearchedUtc { get; set; } = DateTime.UtcNow;

    public bool IsFavorite { get; set; }
}

public class SavedSearch : Entity
{
    public string Name { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;

    public SearchIntent? Intent { get; set; }
}

/// <summary>A bookmarked item: a web source, document, diagnostic session, DTC, lesson, etc.</summary>
public class Bookmark : Entity
{
    public BookmarkKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Url { get; set; }

    /// <summary>Id or key of the bookmarked record (entity id, DTC code, lesson key...).</summary>
    public string? TargetKey { get; set; }

    public string? Note { get; set; }

    public SourceType? SourceType { get; set; }
}

/// <summary>A web page that was returned by search or fetched for research.</summary>
public class WebSource : Entity
{
    public string Url { get; set; } = string.Empty;

    public string UrlHash { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;

    public SourceType SourceType { get; set; } = SourceType.Unknown;

    public DateTime? PublishedUtc { get; set; }

    public DateTime RetrievedUtc { get; set; } = DateTime.UtcNow;

    public string? Snippet { get; set; }

    /// <summary>Cleaned main text of the page (truncated) for offline reference and summarization.</summary>
    public string? ContentExcerpt { get; set; }

    public DateTime? ContentFetchedUtc { get; set; }

    public string? LastQuery { get; set; }

    public string? SearchProvider { get; set; }
}

/// <summary>Generic response cache for external services (VIN decodes, recalls, searches) used offline.</summary>
public class CachedResponse
{
    public string Key { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTime RetrievedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresUtc { get; set; }
}
