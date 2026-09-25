using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Platform;

/// <summary>Persists non-secret settings to settings.json with atomic replace.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _path = paths.SettingsFile;
        _logger = logger;
        _current = Load();
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await WriteAsync(settings, ct);
            _current = settings;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        AppSettings updated;
        await _gate.WaitAsync(ct);
        try
        {
            updated = _current.Clone();
            mutate(updated);
            await WriteAsync(updated, ct);
            _current = updated;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, updated);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = Json.Deserialize<AppSettings>(File.ReadAllText(_path));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // A corrupt settings file must never prevent the app from starting.
            _logger.LogWarning(ex, "Settings file could not be read; defaults will be used");
            TryBackupCorruptFile();
        }

        return new AppSettings();
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, Json.Serialize(settings, indented: true), ct);
        File.Move(temp, _path, overwrite: true);
    }
}
