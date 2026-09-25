using System.Numerics.Tensors;
using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>
/// Exact nearest-neighbor search over chunk embeddings stored in SQLite. Vectors for the
/// active model are loaded once into memory (L2-normalized) and scored with SIMD dot
/// products — fast for the hundreds of thousands of chunks a workstation knowledge base holds.
/// </summary>
public sealed class SqliteVectorIndex(SqliteConnectionInfo connectionInfo) : IVectorIndex
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _loadedModel;
    private List<(Guid ChunkId, Guid DocumentId, float[] Vector)> _vectors = [];

    public Task UpsertAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken ct)
    {
        // Persistence happens through EF; the cache is simply invalidated.
        Invalidate();
        return Task.CompletedTask;
    }

    public Task RemoveDocumentAsync(Guid documentId, CancellationToken ct)
    {
        Invalidate();
        return Task.CompletedTask;
    }

    public void Invalidate()
    {
        _loadedModel = null;
        _vectors = [];
    }

    public async Task<int> CountAsync(string model, CancellationToken ct)
    {
        await EnsureLoadedAsync(model, ct);
        return _vectors.Count;
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, string model, int limit, CancellationToken ct)
    {
        if (query.Length == 0) return [];
        await EnsureLoadedAsync(model, ct);
        var vectors = _vectors;
        if (vectors.Count == 0) return [];

        var q = Normalize(query);
        var top = new PriorityQueue<(Guid, Guid, float), float>();
        foreach (var (chunkId, documentId, vector) in vectors)
        {
            if (vector.Length != q.Length) continue;
            var score = TensorPrimitives.Dot(q, vector);
            if (top.Count < limit)
            {
                top.Enqueue((chunkId, documentId, score), score);
            }
            else if (top.TryPeek(out _, out var min) && score > min)
            {
                top.DequeueEnqueue((chunkId, documentId, score), score);
            }
        }

        var results = new List<VectorHit>(top.Count);
        while (top.TryDequeue(out var item, out _)) results.Add(new VectorHit(item.Item1, item.Item2, item.Item3));
        results.Reverse();
        return results;
    }

    private async Task EnsureLoadedAsync(string model, CancellationToken ct)
    {
        if (_loadedModel == model) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_loadedModel == model) return;
            var list = new List<(Guid, Guid, float[])>();
            await using var connection = new SqliteConnection(connectionInfo.ConnectionString);
            await connection.OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT ChunkId, DocumentId, Vector FROM Embeddings WHERE Model = $m";
            command.Parameters.AddWithValue("$m", model);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var bytes = (byte[])reader.GetValue(2);
                list.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Normalize(VectorBlob.FromBytes(bytes))));
            }

            _vectors = list;
            _loadedModel = model;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static float[] Normalize(float[] vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm <= 0) return vector;
        var result = new float[vector.Length];
        TensorPrimitives.Divide(vector, norm, result);
        return result;
    }
}
