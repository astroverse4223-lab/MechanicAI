namespace MechanicAI.Infrastructure.Persistence;

/// <summary>Connection string for the desktop SQLite database (used by raw FTS/vector queries).</summary>
public sealed record SqliteConnectionInfo(string ConnectionString);
