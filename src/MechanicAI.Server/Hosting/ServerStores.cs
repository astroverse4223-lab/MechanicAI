using System.Collections.Concurrent;
using System.Reflection;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Settings;

namespace MechanicAI.Server.Hosting;

/// <summary>
/// Server-side <see cref="ISettingsStore"/>: application settings (AI mode, web search, vehicle
/// data) come from the <c>App</c> configuration section. Runtime updates made by services
/// (e.g. "active vehicle") are per-workstation concepts, so they are kept in memory only and
/// never written back to configuration.
/// </summary>
public sealed class ConfigurationSettingsStore : ISettingsStore
{
    public const string Section = "App";

    private readonly Lock _lock = new();
    private AppSettings _current;

    public ConfigurationSettingsStore(IConfiguration configuration)
    {
        var settings = new AppSettings();
        configuration.GetSection(Section).Bind(settings);
        settings.FirstRunCompleted = true;
        _current = settings;
    }

    public AppSettings Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        lock (_lock) _current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        AppSettings updated;
        lock (_lock)
        {
            updated = _current.Clone();
            mutate(updated);
            _current = updated;
        }

        Changed?.Invoke(this, updated);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Server-side <see cref="ISecretStore"/>: API keys are read from the <c>Secrets</c> configuration
/// section (environment variables such as <c>Secrets__AnthropicApiKey</c>, user-secrets, or a
/// secrets manager provider), keyed by the constant names in <see cref="SecretNames"/>.
/// Values set at runtime live in memory only.
/// </summary>
public sealed class ConfigurationSecretStore : ISecretStore
{
    public const string Section = "Secrets";

    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public ConfigurationSecretStore(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        foreach (var field in typeof(SecretNames).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            var value = section[field.Name];
            if (!string.IsNullOrWhiteSpace(value)) _values[(string)field.GetRawConstantValue()!] = value;
        }
    }

    public Task<string?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);

    public Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        _values[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        _values.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_values.Keys.Order(StringComparer.Ordinal).ToList());
}
