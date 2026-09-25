using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

/// <summary>Recent searches, favorites, saved searches, and bookmarks.</summary>
public sealed class SearchHistoryService(IAppDbContextFactory dbFactory)
{
    public event EventHandler? Changed;

    public async Task RecordAsync(string query, SearchIntent intent, int resultCount, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        await using var db = await dbFactory.CreateAsync(ct);
        var normalized = query.Trim();
        var existing = await db.SearchHistory.FirstOrDefaultAsync(s => s.Query == normalized, ct);
        if (existing is null)
        {
            db.SearchHistory.Add(new SearchHistoryEntry { Query = Text.Truncate(normalized, 500), Intent = intent, ResultCount = resultCount });
        }
        else
        {
            existing.SearchedUtc = DateTime.UtcNow;
            existing.Intent = intent;
            existing.ResultCount = resultCount;
        }

        await db.SaveChangesAsync(ct);

        // Keep the history bounded (favorites are always kept).
        var stale = await db.SearchHistory.Where(s => !s.IsFavorite).OrderByDescending(s => s.SearchedUtc).Skip(300).ToListAsync(ct);
        if (stale.Count > 0)
        {
            db.SearchHistory.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> RecentAsync(int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.SearchHistory.AsNoTracking().OrderByDescending(s => s.SearchedUtc).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> FavoritesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.SearchHistory.AsNoTracking().Where(s => s.IsFavorite).OrderBy(s => s.Query).ToListAsync(ct);
    }

    public async Task SetFavoriteAsync(Guid id, bool favorite, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var entry = await db.SearchHistory.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entry is null) return;
        entry.IsFavorite = favorite;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        await db.SearchHistory.Where(s => !s.IsFavorite).ExecuteDeleteAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Guid> SaveSearchAsync(string name, string query, SearchIntent? intent, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var saved = new SavedSearch { Name = Text.Truncate(name.Trim(), 120), Query = Text.Truncate(query.Trim(), 500), Intent = intent };
        db.SavedSearches.Add(saved);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return saved.Id;
    }

    public async Task<IReadOnlyList<SavedSearch>> SavedSearchesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.SavedSearches.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task DeleteSavedSearchAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        await db.SavedSearches.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<Guid> AddBookmarkAsync(BookmarkKind kind, string title, string? url, string? targetKey, string? note, SourceType? sourceType,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var existing = await db.Bookmarks.FirstOrDefaultAsync(b => b.Kind == kind && ((url != null && b.Url == url) || (targetKey != null && b.TargetKey == targetKey)), ct);
        if (existing is not null) return existing.Id;
        var bookmark = new Bookmark
        {
            Kind = kind,
            Title = Text.Truncate(title, 500),
            Url = url,
            TargetKey = targetKey,
            Note = note,
            SourceType = sourceType,
        };
        db.Bookmarks.Add(bookmark);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return bookmark.Id;
    }

    public async Task<IReadOnlyList<Bookmark>> BookmarksAsync(BookmarkKind? kind = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Bookmarks.AsNoTracking();
        if (kind is { } k) query = query.Where(b => b.Kind == k);
        return await query.OrderByDescending(b => b.CreatedUtc).ToListAsync(ct);
    }

    public async Task<bool> IsBookmarkedAsync(BookmarkKind kind, string? url, string? targetKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Bookmarks.AnyAsync(b => b.Kind == kind && ((url != null && b.Url == url) || (targetKey != null && b.TargetKey == targetKey)), ct);
    }

    public async Task RemoveBookmarkAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        await db.Bookmarks.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
