using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL full-text keyword index (shop server). Chunks and DTC definitions are read
/// straight from their tables through expression GIN indexes, so there is no separate index
/// to keep in sync: the write-side methods are no-ops.
/// </summary>
public sealed class PostgresKeywordIndex(IDbContextFactory<PostgresAppDbContext> dbFactory) : IKeywordIndex
{
    /// <summary>Expression indexes backing the queries below (idempotent; run after migrations).</summary>
    public const string CreateSql = """
        CREATE INDEX IF NOT EXISTS "IX_DocumentChunks_Fts" ON "DocumentChunks"
            USING GIN (to_tsvector('english', coalesce("Heading", '') || ' ' || "Text"));
        CREATE INDEX IF NOT EXISTS "IX_DTCs_Fts" ON "DTCs"
            USING GIN (to_tsvector('english', "Description" || ' ' || "Subsystem"));
        """;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is", "it",
        "my", "of", "on", "or", "the", "this", "to", "what", "when", "where", "which", "with", "should", "check", "find",
    };

    public Task IndexChunksAsync(IReadOnlyList<DocumentChunk> chunks, string documentTitle, CancellationToken ct) => Task.CompletedTask;

    public Task RemoveDocumentAsync(Guid documentId, CancellationToken ct) => Task.CompletedTask;

    public Task IndexDtcsAsync(IReadOnlyList<DtcDefinition> definitions, CancellationToken ct) => Task.CompletedTask;

    public async Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var tsQuery = BuildTsQuery(query);
        if (tsQuery is null) return [];
        var take = Math.Clamp(limit, 1, 500);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<ChunkRow>($"""
            SELECT c."Id" AS "ChunkId", c."DocumentId" AS "DocumentId",
                   ts_rank_cd(to_tsvector('english', coalesce(c."Heading", '') || ' ' || c."Text"), q)::double precision AS "Score"
            FROM "DocumentChunks" c, to_tsquery('english', {tsQuery}) q
            WHERE to_tsvector('english', coalesce(c."Heading", '') || ' ' || c."Text") @@ q
            ORDER BY "Score" DESC
            LIMIT {take}
            """).ToListAsync(ct);
        return rows.Select(r => new KeywordHit(r.ChunkId, r.DocumentId, r.Score)).ToList();
    }

    public async Task<IReadOnlyList<string>> SearchDtcCodesAsync(string query, int limit, CancellationToken ct)
    {
        var tsQuery = BuildTsQuery(query);
        if (tsQuery is null) return [];
        var take = Math.Clamp(limit, 1, 200);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var codes = await db.Database.SqlQuery<string>($"""
            SELECT d."Code" AS "Value"
            FROM "DTCs" d, to_tsquery('english', {tsQuery}) q
            WHERE to_tsvector('english', d."Description" || ' ' || d."Subsystem") @@ q
            ORDER BY ts_rank_cd(to_tsvector('english', d."Description" || ' ' || d."Subsystem"), q) DESC
            LIMIT {take}
            """).ToListAsync(ct);
        return codes.Distinct().ToList();
    }

    /// <summary>
    /// Builds a safe tsquery: terms are reduced to letters and digits (so user input can never
    /// inject tsquery syntax), stop words are dropped, and terms are OR-ed with prefix matching.
    /// </summary>
    internal static string? BuildTsQuery(string query)
    {
        var terms = Text.Tokenize(query)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
        if (terms.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var term in terms)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(term);
            if (term.Length >= 4 && term.All(char.IsLetter)) sb.Append(":*");
        }

        return sb.ToString();
    }

    internal sealed class ChunkRow
    {
        public Guid ChunkId { get; set; }

        public Guid DocumentId { get; set; }

        public double Score { get; set; }
    }
}

/// <summary>Nearest-neighbor search over chunk embeddings with pgvector (cosine distance).</summary>
public sealed class PostgresVectorIndex(IDbContextFactory<PostgresAppDbContext> dbFactory) : IVectorIndex
{
    // Embeddings are written through EF; pgvector queries the table directly, so there is no cache.
    public Task UpsertAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken ct) => Task.CompletedTask;

    public Task RemoveDocumentAsync(Guid documentId, CancellationToken ct) => Task.CompletedTask;

    public void Invalidate()
    {
    }

    public async Task<int> CountAsync(string model, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Embeddings.CountAsync(e => e.Model == model, ct);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, string model, int limit, CancellationToken ct)
    {
        if (query.Length == 0) return [];
        var vector = new Pgvector.Vector(query);
        var dimensions = query.Length;
        var take = Math.Clamp(limit, 1, 500);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Database.SqlQuery<VectorRow>($"""
            SELECT e."ChunkId" AS "ChunkId", e."DocumentId" AS "DocumentId", (1 - (e."Vector" <=> {vector}))::double precision AS "Similarity"
            FROM "Embeddings" e
            WHERE e."Model" = {model} AND e."Dimensions" = {dimensions}
            ORDER BY e."Vector" <=> {vector}
            LIMIT {take}
            """).ToListAsync(ct);
        return rows.Select(r => new VectorHit(r.ChunkId, r.DocumentId, r.Similarity)).ToList();
    }

    internal sealed class VectorRow
    {
        public Guid ChunkId { get; set; }

        public Guid DocumentId { get; set; }

        public double Similarity { get; set; }
    }
}
