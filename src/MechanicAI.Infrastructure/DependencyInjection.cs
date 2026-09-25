using MechanicAI.Application.Abstractions;
using MechanicAI.Infrastructure.Content;
using MechanicAI.Infrastructure.Persistence;
using MechanicAI.Infrastructure.Platform;
using MechanicAI.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pgvector.EntityFrameworkCore;

namespace MechanicAI.Infrastructure;

public enum DatabaseProvider { Sqlite, Postgres }

public sealed class InfrastructureOptions
{
    public DatabaseProvider Database { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>PostgreSQL connection string, or a SQLite connection string override (tests, portable mode).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Root directory for local data; defaults to %LOCALAPPDATA%\MechanicAI.</summary>
    public string? DataRoot { get; set; }

    /// <summary>
    /// Registers workstation services: FTS/vector indexes, background processing, connectivity
    /// monitoring, and DPAPI secret storage. The shop server leaves this off.
    /// </summary>
    public bool Workstation { get; set; } = true;
}

public static partial class DependencyInjection
{
    public static IServiceCollection AddMechanicAiInfrastructure(this IServiceCollection services, Action<InfrastructureOptions>? configure = null)
    {
        var options = new InfrastructureOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        var paths = new AppPaths(options.DataRoot);
        services.TryAddSingleton<IAppPaths>(paths);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.TryAddSingleton<IFileStore, LocalFileStore>();
        services.TryAddSingleton<IAuditLogger, AuditLogger>();
        services.TryAddSingleton<IReferenceContentProvider, EmbeddedContentProvider>();
        services.TryAddTransient<ReferenceDataSeeder>();
        services.TryAddTransient<DatabaseInitializer>();

        if (OperatingSystem.IsWindows() && options.Workstation)
        {
            services.TryAddSingleton<ISecretStore, DpapiSecretStore>();
        }
        else
        {
            services.TryAddSingleton<ISecretStore, InMemorySecretStore>();
        }

        if (options.Database == DatabaseProvider.Sqlite)
        {
            var connectionString = options.ConnectionString ?? new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 30,
            }.ToString();

            services.AddSingleton(new SqliteConnectionInfo(connectionString));
            services.AddDbContextFactory<SqliteAppDbContext>(o => o
                .UseSqlite(connectionString, s => s.MigrationsHistoryTable("__EFMigrationsHistory"))
                .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll));
            services.TryAddSingleton<IAppDbContextFactory, EfAppDbContextFactory<SqliteAppDbContext>>();
            services.TryAddSingleton<IKeywordIndex, SqliteKeywordIndex>();
            services.TryAddSingleton<IVectorIndex, SqliteVectorIndex>();
        }
        else
        {
            var connectionString = options.ConnectionString
                                   ?? throw new InvalidOperationException("A PostgreSQL connection string is required (ConnectionStrings:MechanicAI).");
            services.AddDbContextFactory<PostgresAppDbContext>(o => o
                .UseNpgsql(connectionString, n =>
                {
                    n.UseVector();
                    n.EnableRetryOnFailure(3);
                }));
            services.TryAddSingleton<IAppDbContextFactory, EfAppDbContextFactory<PostgresAppDbContext>>();
        }

        if (options.Workstation)
        {
            services.TryAddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
            services.AddHostedService<BackgroundTaskProcessor>();
            services.AddHttpClient(nameof(ConnectivityMonitor), c => c.Timeout = TimeSpan.FromSeconds(8));
            services.TryAddSingleton<ConnectivityMonitor>();
            services.TryAddSingleton<IConnectivityMonitor>(sp => sp.GetRequiredService<ConnectivityMonitor>());
            services.AddHostedService(sp => sp.GetRequiredService<ConnectivityMonitor>());
        }

        AddExternalServices(services);
        return services;
    }

    /// <summary>HTTP-based providers (vehicle data, AI, web search). Implemented in partial files.</summary>
    static partial void AddExternalServices(IServiceCollection services);
}
