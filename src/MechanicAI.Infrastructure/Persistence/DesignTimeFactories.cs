using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pgvector.EntityFrameworkCore;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to create SQLite migrations.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

/// <summary>Used only by <c>dotnet ef</c> to create PostgreSQL migrations (no connection is opened).</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("MECHANICAI_DESIGN_PG") ?? "Host=localhost;Database=mechanicai;Username=mechanicai",
                n => n.UseVector())
            .Options);
}
