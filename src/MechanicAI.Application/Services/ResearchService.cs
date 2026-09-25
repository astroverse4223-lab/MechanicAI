using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Research;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public sealed record ResearchSource(
    string Title,
    string Url,
    string Domain,
    SourceType Type,
    string TypeLabel,
    string? Publisher,
    DateTime? PublishedUtc,
    string? AgeText,
    string? Snippet,
    double Relevance,
    string Provider,
    bool FromCache,
    DateTime RetrievedUtc);

public sealed record ResearchResults(
    string Query,
    string EffectiveQuery,
    IReadOnlyList<ResearchSource> Sources,
    bool FromCache,
    DateTime RetrievedUtc,
    string? Notice);

public sealed record ResearchSummary(string Markdown, IReadOnlyList<SourceCitation> Sources, IReadOnlyList<string> Warnings, string Model, IReadOnlyList<string> FetchFailures);

/// <summary>
/// Web research: searches the configured provider, classifies and ranks sources by
/// authority and relevance, caches results for offline use, and produces AI summaries in
/// which every claim cites a retrieved source.
/// </summary>
public sealed class ResearchService(
    IWebSearchProviderFactory providers,
    IWebPageFetcher fetcher,
    IResponseCache cache,
    IAiRouter router,
    IAppDbContextFactory dbFactory,
    ISettingsStore settings,
    ILogger<ResearchService> logger,
    IConnectivityMonitor? connectivity = null)
{
    public async Task<Result<ResearchResults>> SearchAsync(string query, string? vehicleContext = null, int? maxResults = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Error.Validation("Enter something to search for.");
        var s = settings.Current.Search;
        var effective = BuildQuery(query, vehicleContext);
        var cacheKey = "web:" + Text.Sha256Hex(s.Provider + "|" + effective.ToLowerInvariant())[..32];
        var count = Math.Clamp(maxResults ?? s.MaxResults, 1, 20);

        if (connectivity is { IsOnline: false })
        {
            var offline = await FromCacheAsync(cacheKey, query, effective, "You are offline. Showing cached results");
            return offline is not null ? offline : Error.Offline("Web search");
        }

        var (provider, reason) = await providers.GetActiveAsync(ct);
        if (provider is null) return Error.NotConfigured(reason ?? "Web search is not configured.");

        var cached = await cache.GetAsync(cacheKey, ct);
        if (cached is not null && cached.IsFresh(DateTime.UtcNow))
        {
            return Rank(Json.Deserialize<List<WebSearchHit>>(cached.Payload) ?? [], query, effective, true, cached.RetrievedUtc, null);
        }

        IReadOnlyList<WebSearchHit> hits;
        try
        {
            hits = await provider.SearchAsync(effective, Math.Min(20, count + 5), ct);
        }
        catch (ExternalServiceException ex)
        {
            logger.LogWarning("Web search via {Provider} failed: {Kind}", provider.DisplayName, ex.Kind);
            if (cached is not null)
            {
                return Rank(Json.Deserialize<List<WebSearchHit>>(cached.Payload) ?? [], query, effective, true, cached.RetrievedUtc,
                    $"{ex.UserMessage} Showing cached results");
            }

            return new Error(ex.Kind, ex.UserMessage);
        }

        await cache.SetAsync(cacheKey, provider.DisplayName, Json.Serialize(hits), TimeSpan.FromHours(Math.Max(1, s.CacheHours)), ct);
        var ranked = Rank(hits, query, effective, false, DateTime.UtcNow, null);
        await RememberSourcesAsync(ranked.Sources, query, provider.DisplayName, ct);
        return ranked with { Sources = ranked.Sources.Take(count).ToList() };
    }

    private async Task<ResearchResults?> FromCacheAsync(string key, string query, string effective, string notice)
    {
        var cached = await cache.GetAsync(key, CancellationToken.None);
        if (cached is null) return null;
        return Rank(Json.Deserialize<List<WebSearchHit>>(cached.Payload) ?? [], query, effective, true, cached.RetrievedUtc,
            $"{notice} from {cached.RetrievedUtc.ToLocalTime():g}. They may be out of date.");
    }

    private ResearchResults Rank(IReadOnlyList<WebSearchHit> hits, string query, string effective, bool fromCache, DateTime retrieved, string? notice)
    {
        var s = settings.Current.Search;
        var terms = Text.Tokenize(query).Where(t => t.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new List<ResearchSource>();
        var position = 0;
        foreach (var hit in hits)
        {
            position++;
            if (!Uri.TryCreate(hit.Url, UriKind.Absolute, out var uri)) continue;
            var domain = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            if (s.BlockedDomains.Any(b => domain.EndsWith(b.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            var classification = SourceClassifier.Classify(hit.Url, s.PreferredDomains);
            if (classification.Type == SourceType.Forum && !s.IncludeForums) continue;
            if (classification.Type == SourceType.SocialMedia && !s.IncludeSocialMedia) continue;

            var haystack = (hit.Title + " " + hit.Snippet).ToLowerInvariant();
            var overlap = terms.Count == 0 ? 0.5 : terms.Count(t => haystack.Contains(t, StringComparison.Ordinal)) / (double)terms.Count;
            var positionScore = 1.0 / (1 + (position - 1) * 0.35);
            var relevance = (0.45 * overlap) + (0.30 * positionScore) + (0.25 * classification.Weight);
            sources.Add(new ResearchSource(hit.Title, hit.Url, domain, classification.Type, classification.Label, classification.Publisher,
                hit.PublishedUtc, hit.AgeText, hit.Snippet, Math.Round(relevance, 3), hit.Provider, fromCache, retrieved));
        }

        return new ResearchResults(query, effective, sources.OrderByDescending(x => x.Relevance).ToList(), fromCache, retrieved, notice);
    }

    /// <summary>
    /// Reads the top sources and asks the AI for a cited summary. Pages that fail to load are
    /// reported; the summary only uses what was actually retrieved.
    /// </summary>
    public async Task<Result<ResearchSummary>> SummarizeAsync(string question, IReadOnlyList<ResearchSource> sources, Func<string, Task>? onText,
        CancellationToken ct = default)
    {
        if (sources.Count == 0) return Error.Validation("There are no sources to summarize.");
        var route = await router.ResolveChatAsync(AiTask.WebSummary, DataSensitivity.General, cancellationToken: ct);
        if (!route.IsAvailable) return Error.NotConfigured(route.UnavailableReason!);

        var registry = new SourceRegistry();
        var s = settings.Current.Search;
        var toRead = sources.Take(Math.Clamp(s.PagesToFetch, 1, 8)).ToList();
        var failures = new List<string>();
        var context = new StringBuilder();

        var pages = await Task.WhenAll(toRead.Select(async source =>
        {
            if (!s.FetchPages || connectivity is { IsOnline: false }) return (source, (FetchedPage?)null, (string?)null);
            try
            {
                return (source, await fetcher.FetchAsync(source.Url, ct), (string?)null);
            }
            catch (ExternalServiceException ex)
            {
                return (source, (FetchedPage?)null, ex.UserMessage);
            }
        }));

        foreach (var (source, page, error) in pages)
        {
            if (error is not null) failures.Add($"{source.Domain}: {error}");
            var text = page?.Text is { Length: > 200 } body ? Text.Truncate(body, 6000) : source.Snippet ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) continue;
            var citation = registry.Register(SourceRegistry.Web(source.Title, source.Url, source.Type, source.Publisher,
                page?.PublishedUtc ?? source.PublishedUtc, Text.Truncate(text, 400), source.FromCache, source.RetrievedUtc));
            context.Append('[').Append(citation.Label).Append("] ").Append(source.Title).Append('\n')
                .Append("Authority: ").Append(source.TypeLabel).Append(" | Site: ").Append(source.Domain)
                .Append(source.PublishedUtc is { } d ? $" | Published: {d:yyyy-MM-dd}" : string.Empty)
                .Append(page is null ? " | (search snippet only)" : string.Empty).Append('\n')
                .Append(text).Append("\n\n");
        }

        if (context.Length == 0) return Error.Validation("None of the sources could be read.");

        var request = new ChatRequest
        {
            SystemPrompt = Prompts.WebSummary,
            Messages = [ChatMessage.User($"Technician's question: {question}\n\nSources:\n{context}")],
            Temperature = 0.1,
        };

        var sb = new StringBuilder();
        await foreach (var update in route.Model!.StreamAsync(request, ct))
        {
            if (update is not TextDeltaUpdate t) continue;
            sb.Append(t.Text);
            if (onText is not null) await onText(t.Text);
        }

        var report = CitationValidator.Validate(sb.ToString(), registry);
        return new ResearchSummary(report.CleanedText, registry.Sources, report.Warnings, $"{route.Model.Provider} · {route.Model.Model}", failures);
    }

    public async Task<IReadOnlyList<WebSource>> RecentSourcesAsync(int take = 50, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.WebSources.AsNoTracking().OrderByDescending(w => w.RetrievedUtc).Take(take).ToListAsync(ct);
    }

    private async Task RememberSourcesAsync(IReadOnlyList<ResearchSource> sources, string query, string provider, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var hashes = sources.Select(s => Text.Sha256Hex(s.Url)).ToList();
            var existing = await db.WebSources.Where(w => hashes.Contains(w.UrlHash)).ToDictionaryAsync(w => w.UrlHash, ct);
            foreach (var source in sources)
            {
                var hash = Text.Sha256Hex(source.Url);
                if (!existing.TryGetValue(hash, out var row))
                {
                    row = new WebSource { Url = source.Url, UrlHash = hash };
                    db.WebSources.Add(row);
                    existing[hash] = row;
                }

                row.Title = Text.Truncate(source.Title, 500);
                row.Domain = source.Domain;
                row.SourceType = source.Type;
                row.PublishedUtc = source.PublishedUtc;
                row.RetrievedUtc = DateTime.UtcNow;
                row.Snippet = Text.Truncate(source.Snippet, 1000);
                row.LastQuery = Text.Truncate(query, 500);
                row.SearchProvider = provider;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not store web sources");
        }
    }

    private static string BuildQuery(string query, string? vehicleContext)
    {
        if (string.IsNullOrWhiteSpace(vehicleContext)) return query.Trim();
        var parsed = Search.QueryParser.Parse(query);
        return parsed.HasVehicle ? query.Trim() : $"{vehicleContext.Trim()} {query.Trim()}";
    }
}
