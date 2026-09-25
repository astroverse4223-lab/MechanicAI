using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Settings;
using MechanicAI.Presentation.Abstractions;

namespace MechanicAI.Presentation.Tests;

/// <summary>Runs dispatched work inline (tests have no UI thread).</summary>
internal sealed class ImmediateDispatcher : IDispatcher
{
    public bool HasThreadAccess => true;

    public void Run(Action action) => action();
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Current { get; private set; } = new();

    public int SaveCount { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Current = settings;
        SaveCount++;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var copy = Current.Clone();
        mutate(copy);
        return SaveAsync(copy, ct);
    }
}

internal sealed class FixedClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; } = utcNow;
}

internal static class TestFiles
{
    public static PickedFile File(string name, byte[] content) =>
        new(name, null, content.LongLength, _ => Task.FromResult<Stream>(new MemoryStream(content)));
}
