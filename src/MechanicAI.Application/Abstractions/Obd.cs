namespace MechanicAI.Application.Abstractions;

public enum ObdAdapterKind { SerialUsb, BluetoothSerial, BluetoothLe, Wifi, CanInterface, Simulator }

public sealed record ObdAdapterInfo(string Id, string DisplayName, ObdAdapterKind Kind, string? Port, bool IsSimulator);

/// <summary>
/// Byte-level link to an adapter (serial port, Bluetooth SPP, Wi-Fi socket, BLE GATT, or the simulator).
/// The ELM327 protocol layer sits on top of any transport.
/// </summary>
public interface IObdTransport : IAsyncDisposable
{
    string Description { get; }

    bool IsSimulated { get; }

    bool IsOpen { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    Task WriteLineAsync(string command, CancellationToken cancellationToken);

    /// <summary>Reads until the adapter's '&gt;' prompt, returning the raw response without the prompt.</summary>
    Task<string> ReadUntilPromptAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Discovers adapters of one kind and opens connections to them.</summary>
public interface IObdProvider
{
    string Name { get; }

    Task<IReadOnlyList<ObdAdapterInfo>> DiscoverAsync(CancellationToken cancellationToken);

    bool CanConnect(ObdAdapterInfo adapter);

    Task<IObdConnection> ConnectAsync(ObdAdapterInfo adapter, CancellationToken cancellationToken);
}

/// <summary>An initialized session with a vehicle through an adapter.</summary>
public interface IObdConnection : IAsyncDisposable
{
    ObdAdapterInfo Adapter { get; }

    string? AdapterVersion { get; }

    string? Protocol { get; }

    bool IsConnected { get; }

    IDiagnosticScanner Scanner { get; }

    ILiveDataProvider LiveData { get; }

    Task<string> SendRawAsync(string command, CancellationToken cancellationToken);
}

public sealed record ScannedDtc(string Code, string Status, string? EcuAddress);

public sealed record FreezeFrameData(string? TriggerDtc, IReadOnlyDictionary<string, string> Values);

public sealed record MonitorStatusData(bool MilOn, int DtcCount, IReadOnlyList<(string Monitor, bool Available, bool Complete)> Monitors);

/// <summary>Trouble-code services (SAE J1979 modes 01/02/03/04/07/09/0A).</summary>
public interface IDiagnosticScanner
{
    Task<IReadOnlyList<ScannedDtc>> ReadStoredDtcsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ScannedDtc>> ReadPendingDtcsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ScannedDtc>> ReadPermanentDtcsAsync(CancellationToken cancellationToken);

    /// <summary>Clears DTCs and resets monitors (Mode 04). Callers must confirm with the technician first.</summary>
    Task<bool> ClearDtcsAsync(CancellationToken cancellationToken);

    Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken);

    Task<string?> ReadVinAsync(CancellationToken cancellationToken);

    Task<MonitorStatusData?> ReadMonitorStatusAsync(CancellationToken cancellationToken);
}

public sealed record PidReading(string Key, double Value, DateTime TimestampUtc);

/// <summary>Live data (Mode 01 PIDs).</summary>
public interface ILiveDataProvider
{
    Task<IReadOnlySet<string>> GetSupportedPidsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PidReading>> ReadAsync(IReadOnlyList<string> pidKeys, CancellationToken cancellationToken);
}
