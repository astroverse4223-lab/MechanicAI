using MechanicAI.Application.Settings;

namespace MechanicAI.Application.Abstractions;

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Well-known local directories. All paths are created on first use.</summary>
public interface IAppPaths
{
    string DataRoot { get; }
    string DatabasePath { get; }
    string DocumentsDirectory { get; }
    string MediaDirectory { get; }
    string ExportsDirectory { get; }
    string LogsDirectory { get; }
    string CacheDirectory { get; }
    string SettingsFile { get; }
    string SecretsFile { get; }
}

/// <summary>
/// Encrypted credential storage (DPAPI, current-user scope, on Windows). API keys and
/// tokens are only ever held here — never in settings files, logs, or source code.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);

    Task SetAsync(string name, string value, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default);
}

public static class SecretNames
{
    public const string AnthropicApiKey = "ai.anthropic.apiKey";
    public const string OpenAiApiKey = "ai.openai.apiKey";
    public const string BraveApiKey = "search.brave.apiKey";
    public const string TavilyApiKey = "search.tavily.apiKey";
    public const string SearxngApiKey = "search.searxng.apiKey";
    public const string ShopServerRefreshToken = "shop.refreshToken";
    public const string LocalPasswordHash = "security.localPasswordHash";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        [AnthropicApiKey] = "Anthropic API key",
        [OpenAiApiKey] = "OpenAI-compatible API key",
        [BraveApiKey] = "Brave Search API key",
        [TavilyApiKey] = "Tavily API key",
        [SearxngApiKey] = "SearXNG API key",
        [ShopServerRefreshToken] = "Shop server session",
        [LocalPasswordHash] = "Workstation sign-in password",
    };
}

public interface ISettingsStore
{
    AppSettings Current { get; }

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default);

    event EventHandler<AppSettings>? Changed;
}

/// <summary>Reports internet availability so features can degrade honestly when offline.</summary>
public interface IConnectivityMonitor
{
    bool IsOnline { get; }

    DateTime? LastChangedUtc { get; }

    event EventHandler<bool>? ConnectivityChanged;

    Task<bool> CheckNowAsync(CancellationToken ct = default);
}

/// <summary>Queue for long-running work (document indexing) that must not block the UI.</summary>
public interface IBackgroundTaskQueue
{
    ValueTask QueueAsync(BackgroundWorkItem item, CancellationToken ct = default);

    ValueTask<BackgroundWorkItem> DequeueAsync(CancellationToken ct);

    int PendingCount { get; }

    event EventHandler<BackgroundProgress>? ProgressChanged;

    void ReportProgress(BackgroundProgress progress);
}

public sealed record BackgroundWorkItem(string Name, Func<IServiceProvider, CancellationToken, Task> Work, Guid? CorrelationId = null);

public sealed record BackgroundProgress(Guid? CorrelationId, string Name, string Stage, double? Fraction, bool Completed = false, bool Failed = false);

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerification Verify(string hash, string password);
}

public enum PasswordVerification { Failed, Success, SuccessRehashNeeded }

public interface IAuditLogger
{
    Task LogAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
        bool succeeded = true, CancellationToken ct = default);
}

/// <summary>Stores uploaded files (documents, photos) in the local data directory.</summary>
public interface IFileStore
{
    Task<StoredFile> SaveAsync(Stream content, string originalFileName, FileArea area, CancellationToken ct);

    Stream OpenRead(string storedFileName, FileArea area);

    string GetFullPath(string storedFileName, FileArea area);

    Task DeleteAsync(string storedFileName, FileArea area, CancellationToken ct);
}

public enum FileArea { Documents, Media, Exports }

public sealed record StoredFile(string StoredFileName, long SizeBytes, string Sha256);
