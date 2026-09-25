using MechanicAI.Domain.Entities;
using MechanicAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace MechanicAI.Infrastructure.Tests.Persistence;

public sealed class SqliteKeywordIndexTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SqliteKeywordIndex _index;
    private readonly string _connectionString;

    public SqliteKeywordIndexTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _temp.Combine("fts.db"), Pooling = false }.ToString();
        _index = new SqliteKeywordIndex(new SqliteConnectionInfo(_connectionString));
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = SqliteKeywordIndex.CreateSql;
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _temp.Dispose();

    private static DocumentChunk Chunk(Guid documentId, string heading, string text) =>
        new() { DocumentId = documentId, Heading = heading, Text = text };

    [Fact]
    public async Task SearchAsync_RanksMatchingChunksAndStemsTerms()
    {
        var manual = Guid.NewGuid();
        var bulletin = Guid.NewGuid();
        var misfire = Chunk(manual, "Misfire diagnosis", "Swap the ignition coil to another cylinder and watch the misfire counters.");
        var brakes = Chunk(manual, "Brake service", "Measure rotor runout with a dial indicator.");
        var coils = Chunk(bulletin, "Bulletin summary","Coils may fail when moisture enters the spark plug well.");
        await _index.IndexChunksAsync([misfire, brakes], "Service manual", CancellationToken.None);
        await _index.IndexChunksAsync([coils], "TSB 21-001", CancellationToken.None);

        var hits = await _index.SearchAsync("misfiring ignition coil", 10, CancellationToken.None);

        Assert.Equal(misfire.Id, hits[0].ChunkId);
        Assert.Equal(manual, hits[0].DocumentId);
        Assert.Contains(hits, h => h.ChunkId == coils.Id);
        Assert.DoesNotContain(hits, h => h.ChunkId == brakes.Id);
        Assert.True(hits[0].Score >= hits[^1].Score);
    }

    [Fact]
    public async Task SearchAsync_HandlesQuotesAndFtsSyntaxSafely()
    {
        await _index.IndexChunksAsync([Chunk(Guid.NewGuid(), "Wiring", "Check the ground strap for corrosion.")], "Doc", CancellationToken.None);

        Assert.NotEmpty(await _index.SearchAsync("\"ground\" AND (strap OR NEAR", 5, CancellationToken.None));
        Assert.Empty(await _index.SearchAsync("the and of", 5, CancellationToken.None));
        Assert.Empty(await _index.SearchAsync("   ", 5, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveDocumentAsync_DropsOnlyThatDocument()
    {
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        await _index.IndexChunksAsync([Chunk(keep, "A", "Fuel pump relay test procedure")], "Keep", CancellationToken.None);
        await _index.IndexChunksAsync([Chunk(drop, "B", "Fuel pump replacement procedure")], "Drop", CancellationToken.None);

        await _index.RemoveDocumentAsync(drop, CancellationToken.None);

        var hits = await _index.SearchAsync("fuel pump", 10, CancellationToken.None);
        var hit = Assert.Single(hits);
        Assert.Equal(keep, hit.DocumentId);
    }

    [Fact]
    public async Task DtcIndex_ReplacesPreviousContentAndFindsByDescription()
    {
        await _index.IndexDtcsAsync([new DtcDefinition { Code = "P0000", Description = "Old entry about oxygen" }], CancellationToken.None);
        await _index.IndexDtcsAsync(
        [
            new DtcDefinition { Code = "P0171", Description = "System Too Lean Bank 1", Subsystem = "Fuel and Air Metering", Causes = ["Vacuum leak"] },
            new DtcDefinition { Code = "P0133", Description = "O2 Sensor Circuit Slow Response", Subsystem = "Fuel and Air Metering", Symptoms = ["Oxygen sensor slow"] },
            new DtcDefinition { Code = "P0302", Description = "Cylinder 2 Misfire Detected", Subsystem = "Ignition System or Misfire" },
        ], CancellationToken.None);

        Assert.Equal(["P0171"], await _index.SearchDtcCodesAsync("lean vacuum", 10, CancellationToken.None));
        Assert.Equal(["P0133"], await _index.SearchDtcCodesAsync("oxygen", 10, CancellationToken.None));
        Assert.Equal(["P0302"], await _index.SearchDtcCodesAsync("misfires", 10, CancellationToken.None));
    }

    [Theory]
    [InlineData("misfire cylinder 2", "\"misfire\"* OR \"cylinder\"*")]
    [InlineData("how do I check the MAF", "\"maf\"")]
    [InlineData("P0171 bank-1 5.3l", "\"p0171\" OR \"bank-1\" OR \"5.3l\"")]
    [InlineData("coil coil COIL", "\"coil\"*")]
    public void BuildMatchExpression_QuotesTermsAndDropsStopWords(string query, string expected)
    {
        Assert.Equal(expected, SqliteKeywordIndex.BuildMatchExpression(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("the of and")]
    [InlineData("a b c")]
    public void BuildMatchExpression_NullWhenNothingSearchable(string query)
    {
        Assert.Null(SqliteKeywordIndex.BuildMatchExpression(query));
    }
}
