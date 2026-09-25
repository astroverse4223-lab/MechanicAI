using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;

namespace MechanicAI.Infrastructure.Security;

/// <summary>
/// Stores credentials encrypted with Windows DPAPI (current-user scope) plus app-specific
/// entropy. The file is useless on another machine or Windows account. Values are never
/// logged or written in plaintext.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(IAppPaths paths) : ISecretStore
{
    private static readonly byte[] Entropy = "MechanicAI.Secrets.v1"u8.ToArray();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        if (!all.TryGetValue(name, out var protectedValue)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Created by another Windows account or machine; treat as missing.
            return null;
        }
    }

    public async Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAllUnlockedAsync(ct);
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            all[name] = Convert.ToBase64String(encrypted);
            await WriteAllUnlockedAsync(all, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAllUnlockedAsync(ct);
            if (all.Remove(name)) await WriteAllUnlockedAsync(all, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default) =>
        (await ReadAllAsync(ct)).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadAllUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAllUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(paths.SecretsFile)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var json = await File.ReadAllTextAsync(paths.SecretsFile, ct);
        return Json.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task WriteAllUnlockedAsync(Dictionary<string, string> all, CancellationToken ct)
    {
        var temp = paths.SecretsFile + ".tmp";
        await File.WriteAllTextAsync(temp, Json.Serialize(all), ct);
        File.Move(temp, paths.SecretsFile, overwrite: true);
    }
}

/// <summary>Process-lifetime secret store for tests and non-Windows hosts (secrets come from configuration there).</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_values.GetValueOrDefault(name));
    }

    public Task SetAsync(string name, string value, CancellationToken ct = default)
    {
        lock (_lock) _values[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        lock (_lock) _values.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<string>>(_values.Keys.ToList());
    }
}
