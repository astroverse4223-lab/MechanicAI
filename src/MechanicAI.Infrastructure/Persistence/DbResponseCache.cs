using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>Response cache backed by the CachedResponses table (survives restarts; usable offline).</summary>
public sealed class DbResponseCache(IAppDbContextFactory dbFactory) : IResponseCache
{
    public async Task<CacheEntry?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.CachedResponses.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, cancellationToken);
        return row is null ? null : new CacheEntry(row.Key, row.Provider, row.Payload, row.RetrievedUtc, row.ExpiresUtc);
    }

    public async Task SetAsync(string key, string provider, string payload, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.CachedResponses.FirstOrDefaultAsync(c => c.Key == key, cancellationToken);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new CachedResponse { Key = key };
            db.CachedResponses.Add(row);
        }

        row.Provider = provider;
        row.Payload = payload;
        row.RetrievedUtc = now;
        row.ExpiresUtc = now + timeToLive;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await db.CachedResponses.Where(c => c.Key == key).ExecuteDeleteAsync(cancellationToken);
    }
}
