namespace MechanicAI.Application.Abstractions;

public sealed record CacheEntry(string Key, string Provider, string Payload, DateTime RetrievedUtc, DateTime ExpiresUtc)
{
    public bool IsFresh(DateTime nowUtc) => ExpiresUtc > nowUtc;
}

/// <summary>
/// Durable cache of external responses (VIN decodes, recalls, search results). Stale
/// entries are kept so the app can still show — clearly labeled with their retrieval date —
/// previously fetched data while offline.
/// </summary>
public interface IResponseCache
{
    Task<CacheEntry?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string provider, string payload, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
