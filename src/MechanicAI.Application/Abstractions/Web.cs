using MechanicAI.Application.Settings;

namespace MechanicAI.Application.Abstractions;

public sealed record WebSearchHit(
    string Title,
    string Url,
    string? Snippet,
    DateTime? PublishedUtc,
    string? AgeText,
    string Provider,
    double? ProviderScore);

/// <summary>A web search API (Brave, Tavily, SearXNG...). Implementations never fabricate results.</summary>
public interface IWebSearchProvider
{
    WebSearchProviderKind Kind { get; }

    string DisplayName { get; }

    Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken cancellationToken);
}

/// <summary>Resolves the configured provider (or explains why none is available).</summary>
public interface IWebSearchProviderFactory
{
    Task<(IWebSearchProvider? Provider, string? UnavailableReason)> GetActiveAsync(CancellationToken cancellationToken);
}

public sealed record FetchedPage(
    string RequestedUrl,
    string FinalUrl,
    string? Title,
    string Text,
    DateTime? PublishedUtc,
    string ContentType,
    int StatusCode);

/// <summary>Downloads a page and extracts its readable main text (size- and time-limited).</summary>
public interface IWebPageFetcher
{
    Task<FetchedPage> FetchAsync(string url, CancellationToken cancellationToken);
}
