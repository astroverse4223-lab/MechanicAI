using MechanicAI.Infrastructure.Content;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>Applies migrations, prepares SQLite-specific structures, and seeds reference content.</summary>
public sealed class DatabaseInitializer(IServiceProvider services, ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sqliteFactory = services.GetService<IDbContextFactory<SqliteAppDbContext>>();
        if (sqliteFactory is not null)
        {
            await using var db = await sqliteFactory.CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
            var info = services.GetRequiredService<SqliteConnectionInfo>();
            await using var connection = new SqliteConnection(info.ConnectionString);
            await connection.OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " + SqliteKeywordIndex.CreateSql;
            await command.ExecuteNonQueryAsync(ct);
            await PurgeExpiredCacheAsync(db, ct);
        }

        var postgresFactory = services.GetService<IDbContextFactory<PostgresAppDbContext>>();
        if (postgresFactory is not null)
        {
            await using var db = await postgresFactory.CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
            await db.Database.ExecuteSqlRawAsync(PostgresKeywordIndex.CreateSql, ct);
        }

        var seeder = services.GetService<ReferenceDataSeeder>();
        if (seeder is not null)
        {
            try
            {
                await seeder.SeedAsync(ct: ct);
            }
            catch (Exception ex)
            {
                // Reference content failing to seed must not stop the app; the DTC database will
                // simply be empty and the UI says so.
                logger.LogError(ex, "Reference content seeding failed");
            }
        }
    }

    private static async Task PurgeExpiredCacheAsync(AppDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-180);
        await db.CachedResponses.Where(c => c.ExpiresUtc < cutoff && !c.Key.StartsWith("meta:")).ExecuteDeleteAsync(ct);
    }
}
