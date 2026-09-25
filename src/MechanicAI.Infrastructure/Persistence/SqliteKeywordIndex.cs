using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>
/// SQLite FTS5 full-text index (BM25 ranking, Porter stemming) over document chunks and DTC
/// definitions. Works fully offline and without any AI model.
/// </summary>
public sealed class SqliteKeywordIndex(SqliteConnectionInfo connectionInfo) : IKeywordIndex
{
    public const string CreateSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS ChunkFts USING fts5(
            ChunkId UNINDEXED, DocumentId UNINDEXED, Title, Heading, Content,
            tokenize = 'porter unicode61');
        CREATE VIRTUAL TABLE IF NOT EXISTS DtcFts USING fts5(
            Code UNINDEXED, Description, Subsystem, Details,
            tokenize = 'porter unicode61');
        """;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is", "it",
        "my", "of", "on", "or", "the", "this", "to", "what", "when", "where", "which", "with", "should", "check", "find",
    };

    public async Task IndexChunksAsync(IReadOnlyList<DocumentChunk> chunks, string documentTitle, CancellationToken ct)
    {
        if (chunks.Count == 0) return;
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO ChunkFts (ChunkId, DocumentId, Title, Heading, Content) VALUES ($c, $d, $t, $h, $x)";
        var pc = command.Parameters.Add("$c", SqliteType.Text);
        var pd = command.Parameters.Add("$d", SqliteType.Text);
        var pt = command.Parameters.Add("$t", SqliteType.Text);
        var ph = command.Parameters.Add("$h", SqliteType.Text);
        var px = command.Parameters.Add("$x", SqliteType.Text);
        foreach (var chunk in chunks)
        {
            pc.Value = chunk.Id.ToString();
            pd.Value = chunk.DocumentId.ToString();
            pt.Value = documentTitle;
            ph.Value = (object?)chunk.Heading ?? DBNull.Value;
            px.Value = chunk.Text;
            await command.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task RemoveDocumentAsync(Guid documentId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ChunkFts WHERE DocumentId = $d";
        command.Parameters.AddWithValue("$d", documentId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var match = BuildMatchExpression(query);
        if (match is null) return [];
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        // bm25 weights per column: ChunkId, DocumentId, Title, Heading, Content. Lower bm25 = better.
        command.CommandText = """
            SELECT ChunkId, DocumentId, bm25(ChunkFts, 0.0, 0.0, 1.5, 3.0, 1.0) AS score
            FROM ChunkFts WHERE ChunkFts MATCH $q ORDER BY score LIMIT $n
            """;
        command.Parameters.AddWithValue("$q", match);
        command.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 500));
        var hits = new List<KeywordHit>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hits.Add(new KeywordHit(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), -reader.GetDouble(2)));
            }
        }
        catch (SqliteException)
        {
            // Malformed FTS expression despite sanitizing — return no hits rather than failing the search.
            return [];
        }

        return hits;
    }

    public async Task IndexDtcsAsync(IReadOnlyList<DtcDefinition> definitions, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var clear = connection.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = "DELETE FROM DtcFts";
        await clear.ExecuteNonQueryAsync(ct);

        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO DtcFts (Code, Description, Subsystem, Details) VALUES ($c, $d, $s, $x)";
        var pc = command.Parameters.Add("$c", SqliteType.Text);
        var pd = command.Parameters.Add("$d", SqliteType.Text);
        var ps = command.Parameters.Add("$s", SqliteType.Text);
        var px = command.Parameters.Add("$x", SqliteType.Text);
        foreach (var d in definitions)
        {
            pc.Value = d.Code;
            pd.Value = d.Description;
            ps.Value = d.Subsystem;
            px.Value = string.Join(". ", d.Symptoms.Concat(d.Causes).Append(d.Notes ?? string.Empty).Append(d.Manufacturer ?? string.Empty));
            await command.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> SearchDtcCodesAsync(string query, int limit, CancellationToken ct)
    {
        var match = BuildMatchExpression(query);
        if (match is null) return [];
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Code FROM DtcFts WHERE DtcFts MATCH $q ORDER BY bm25(DtcFts, 0.0, 4.0, 1.0, 1.0) LIMIT $n";
        command.Parameters.AddWithValue("$q", match);
        command.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 200));
        var codes = new List<string>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) codes.Add(reader.GetString(0));
        }
        catch (SqliteException)
        {
            return [];
        }

        return codes.Distinct().ToList();
    }

    /// <summary>
    /// Turns free text into a safe FTS5 expression: every term is quoted (so user input can
    /// never inject FTS syntax), stop words are dropped, and terms are OR-ed with prefix matching
    /// so "misfire cylinder 2" still ranks chunks containing most terms highest via BM25.
    /// </summary>
    internal static string? BuildMatchExpression(string query)
    {
        var terms = Text.Tokenize(query)
            .Select(t => t.Trim('.', '-', '/'))
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct()
            .Take(16)
            .ToList();
        if (terms.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var term in terms)
        {
            if (sb.Length > 0) sb.Append(" OR ");
            var safe = term.Replace("\"", string.Empty, StringComparison.Ordinal);
            sb.Append('"').Append(safe).Append('"');
            if (safe.Length >= 4 && safe.All(char.IsLetter)) sb.Append('*');
        }

        return sb.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(connectionInfo.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
