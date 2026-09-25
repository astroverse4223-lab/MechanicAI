using System.Globalization;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.LiveData;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Obd;

/// <summary>
/// ELM327-compatible adapter driver (the command set used by most USB, Bluetooth, and Wi-Fi
/// OBD-II adapters). Works over any <see cref="IObdTransport"/>. Implements SAE J1979
/// services: 01 (live data, monitors), 02 (freeze frame), 03/07/0A (stored/pending/permanent
/// DTCs), 04 (clear), and 09 (VIN).
/// </summary>
public sealed class Elm327Connection : IObdConnection, IDiagnosticScanner, ILiveDataProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);
    private readonly IObdTransport _transport;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _io = new(1, 1);
    private HashSet<byte>? _supported;

    private Elm327Connection(IObdTransport transport, ObdAdapterInfo adapter, ILogger logger)
    {
        _transport = transport;
        Adapter = adapter;
        _logger = logger;
    }

    public ObdAdapterInfo Adapter { get; }

    public string? AdapterVersion { get; private set; }

    public string? Protocol { get; private set; }

    public bool IsConnected => _transport.IsOpen;

    public IDiagnosticScanner Scanner => this;

    public ILiveDataProvider LiveData => this;

    private bool IsCan => Protocol?.Contains("CAN", StringComparison.OrdinalIgnoreCase) == true || Protocol?.Contains("15765", StringComparison.Ordinal) == true;

    public static async Task<Elm327Connection> OpenAsync(IObdTransport transport, ObdAdapterInfo adapter, ILogger logger, CancellationToken ct)
    {
        await transport.OpenAsync(ct);
        var connection = new Elm327Connection(transport, adapter, logger);
        try
        {
            await connection.InitializeAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        var reset = await SendRawAsync("ATZ", TimeSpan.FromSeconds(5), ct);
        if (!reset.Contains("ELM", StringComparison.OrdinalIgnoreCase) && !reset.Contains("OBD", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalServiceException("OBD adapter", ErrorKind.Unavailable,
                "The device did not respond like an ELM327-compatible OBD-II adapter. Check the port and baud rate.");
        }

        foreach (var command in new[] { "ATE0", "ATL0", "ATS0", "ATH0", "ATAT1", "ATSP0" })
        {
            await SendRawAsync(command, DefaultTimeout, ct);
        }

        AdapterVersion = Clean(await SendRawAsync("ATI", DefaultTimeout, ct)).FirstOrDefault();

        // Trigger the automatic protocol search; this can take several seconds.
        var probe = await SendRawAsync("0100", TimeSpan.FromSeconds(20), ct);
        if (probe.Contains("UNABLE TO CONNECT", StringComparison.OrdinalIgnoreCase) || probe.Contains("NO DATA", StringComparison.OrdinalIgnoreCase) ||
            probe.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalServiceException("OBD adapter", ErrorKind.Unavailable,
                "The adapter could not communicate with the vehicle. Turn the ignition on (or start the engine) and check the adapter is fully seated.");
        }

        Protocol = Clean(await SendRawAsync("ATDP", DefaultTimeout, ct)).FirstOrDefault()?.Replace("AUTO, ", string.Empty, StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("ELM327 initialized: {Version}, protocol {Protocol}", AdapterVersion, Protocol);
    }

    public async Task<string> SendRawAsync(string command, CancellationToken cancellationToken) =>
        string.Join('\n', Clean(await SendRawAsync(command, DefaultTimeout, cancellationToken)));

    private async Task<string> SendRawAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            await _transport.WriteLineAsync(command, ct);
            var response = await _transport.ReadUntilPromptAsync(timeout, ct);
            return response;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>Removes echoes, status chatter, and blank lines from a raw adapter response.</summary>
    internal static IReadOnlyList<string> Clean(string raw) =>
        raw.Replace(">", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("SEARCHING", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("BUS INIT", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Parses hex responses into payloads. Single-frame lines become one payload each (one per
    /// responding ECU); ISO-TP multi-frame responses ("0:", "1:", ...) are reassembled.
    /// </summary>
    internal static IReadOnlyList<byte[]> ParsePayloads(string raw)
    {
        var lines = Clean(raw).Where(l => l != "OK").ToList();
        if (lines.Any(l => l.Contains("NO DATA", StringComparison.OrdinalIgnoreCase) || l == "?" || l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            return [];
        }

        var frames = lines.Where(l => l.Length > 2 && l[1] == ':' && Uri.IsHexDigit(l[0])).ToList();
        if (frames.Count > 0)
        {
            var hex = new StringBuilder();
            foreach (var frame in frames.OrderBy(f => Convert.ToInt32(f[..1], 16)))
            {
                hex.Append(frame[2..].Replace(" ", string.Empty, StringComparison.Ordinal));
            }

            var bytes = HexToBytes(hex.ToString());
            // The first line (without "N:") holds the total byte count.
            var lengthLine = lines.FirstOrDefault(l => l.Length <= 3 && l.All(Uri.IsHexDigit));
            if (lengthLine is not null && int.TryParse(lengthLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) && length <= bytes.Length)
            {
                bytes = bytes[..length];
            }

            return [bytes];
        }

        return lines.Select(l => HexToBytes(l.Replace(" ", string.Empty, StringComparison.Ordinal))).Where(b => b.Length > 0).ToList();
    }

    internal static byte[] HexToBytes(string hex)
    {
        if (hex.Length < 2 || hex.Any(c => !Uri.IsHexDigit(c))) return [];
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private async Task<byte[]?> QueryAsync(string request, byte responseService, byte? pid, CancellationToken ct, TimeSpan? timeout = null)
    {
        var raw = await SendRawAsync(request, timeout ?? DefaultTimeout, ct);
        foreach (var payload in ParsePayloads(raw))
        {
            if (payload.Length == 0 || payload[0] != responseService) continue;
            if (pid is { } p && (payload.Length < 2 || payload[1] != p)) continue;
            return payload;
        }

        return null;
    }

    // ---------------------------------------------------------------- live data

    public async Task<IReadOnlySet<string>> GetSupportedPidsAsync(CancellationToken cancellationToken)
    {
        _supported = await ReadSupportedAsync(cancellationToken);
        return ObdPids.All.Where(p => _supported.Contains(p.Pid)).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HashSet<byte>> ReadSupportedAsync(CancellationToken ct)
    {
        var supported = new HashSet<byte>();
        for (byte basePid = 0x00; basePid <= 0xC0; basePid += 0x20)
        {
            var payload = await QueryAsync($"01{basePid:X2}", 0x41, basePid, ct);
            if (payload is null || payload.Length < 6) break;
            foreach (var pid in ObdPids.DecodeSupportBitmap(basePid, payload[2..6])) supported.Add(pid);
            if (!supported.Contains((byte)(basePid + 0x20))) break;
            if (basePid == 0xC0) break;
        }

        return supported;
    }

    public async Task<IReadOnlyList<PidReading>> ReadAsync(IReadOnlyList<string> pidKeys, CancellationToken cancellationToken)
    {
        var readings = new List<PidReading>();
        var definitions = pidKeys.Select(ObdPids.Find).OfType<ObdPid>().ToList();
        foreach (var group in definitions.GroupBy(d => d.Pid))
        {
            if (_supported is not null && !_supported.Contains(group.Key)) continue;
            var payload = await QueryAsync($"01{group.Key:X2}", 0x41, group.Key, cancellationToken);
            if (payload is null) continue;
            var data = payload[2..];
            var now = DateTime.UtcNow;
            foreach (var definition in group)
            {
                if (data.Length < Math.Min(definition.DataBytes, 1)) continue;
                readings.Add(new PidReading(definition.Key, Math.Round(definition.Decode(data), 3), now));
            }
        }

        return readings;
    }

    // ---------------------------------------------------------------- DTC services

    public Task<IReadOnlyList<ScannedDtc>> ReadStoredDtcsAsync(CancellationToken cancellationToken) => ReadDtcsAsync("03", 0x43, "Stored", cancellationToken);

    public Task<IReadOnlyList<ScannedDtc>> ReadPendingDtcsAsync(CancellationToken cancellationToken) => ReadDtcsAsync("07", 0x47, "Pending", cancellationToken);

    public Task<IReadOnlyList<ScannedDtc>> ReadPermanentDtcsAsync(CancellationToken cancellationToken) => ReadDtcsAsync("0A", 0x4A, "Permanent", cancellationToken);

    private async Task<IReadOnlyList<ScannedDtc>> ReadDtcsAsync(string request, byte service, string status, CancellationToken ct)
    {
        var raw = await SendRawAsync(request, TimeSpan.FromSeconds(8), ct);
        return ParseDtcPayloads(ParsePayloads(raw), service, IsCan, status);
    }

    internal static IReadOnlyList<ScannedDtc> ParseDtcPayloads(IReadOnlyList<byte[]> payloads, byte service, bool isCan, string status)
    {
        var codes = new List<ScannedDtc>();
        foreach (var payload in payloads)
        {
            if (payload.Length < 1 || payload[0] != service) continue;
            // CAN responses carry a DTC count byte after the service id; legacy protocols do not.
            var start = isCan ? 2 : 1;
            for (var i = start; i + 1 < payload.Length; i += 2)
            {
                if (payload[i] == 0 && payload[i + 1] == 0) continue;
                var code = ObdPids.DecodeDtc(payload[i], payload[i + 1]);
                if (codes.All(c => c.Code != code)) codes.Add(new ScannedDtc(code, status, null));
            }
        }

        return codes;
    }

    public async Task<bool> ClearDtcsAsync(CancellationToken cancellationToken)
    {
        var raw = await SendRawAsync("04", TimeSpan.FromSeconds(10), cancellationToken);
        return ParsePayloads(raw).Any(p => p.Length > 0 && p[0] == 0x44);
    }

    public async Task<FreezeFrameData?> ReadFreezeFrameAsync(CancellationToken cancellationToken)
    {
        var trigger = await QueryAsync("020200", 0x42, 0x02, cancellationToken);
        string? triggerDtc = null;
        if (trigger is { Length: >= 5 } && (trigger[3] != 0 || trigger[4] != 0)) triggerDtc = ObdPids.DecodeDtc(trigger[3], trigger[4]);
        if (triggerDtc is null) return null;

        var values = new Dictionary<string, string>();
        foreach (var key in new[] { "RPM", "SPEED", "ECT", "LOAD", "STFT1", "LTFT1", "STFT2", "LTFT2", "MAP", "MAF", "TPS", "IAT", "TIMING" })
        {
            var def = ObdPids.Find(key)!;
            var payload = await QueryAsync($"02{def.Pid:X2}00", 0x42, def.Pid, cancellationToken);
            if (payload is null || payload.Length < 4) continue;
            values[def.Name] = string.Create(CultureInfo.InvariantCulture, $"{def.Decode(payload[3..]):0.##} {def.Unit}");
        }

        return new FreezeFrameData(triggerDtc, values);
    }

    public async Task<string?> ReadVinAsync(CancellationToken cancellationToken)
    {
        var raw = await SendRawAsync("0902", TimeSpan.FromSeconds(8), cancellationToken);
        var payloads = ParsePayloads(raw).Where(p => p.Length > 2 && p[0] == 0x49 && p[1] == 0x02).ToList();
        if (payloads.Count == 0) return null;
        var bytes = new List<byte>();
        foreach (var p in payloads)
        {
            // CAN: 49 02 01 <17 bytes>; legacy: several lines 49 02 NN <4 bytes>.
            bytes.AddRange(p.Skip(3));
        }

        var text = new string(bytes.Where(b => b is >= 0x30 and <= 0x5A).Select(b => (char)b).ToArray());
        if (text.Length < 17) return null;
        var vin = text[^17..];
        return Domain.ValueObjects.Vin.TryParse(vin, out var parsed) ? parsed.Value : null;
    }

    public async Task<MonitorStatusData?> ReadMonitorStatusAsync(CancellationToken cancellationToken)
    {
        var payload = await QueryAsync("0101", 0x41, 0x01, cancellationToken);
        if (payload is null || payload.Length < 6) return null;
        byte a = payload[2], b = payload[3], c = payload[4], d = payload[5];
        var monitors = new List<(string, bool, bool)>
        {
            ("Misfire", (b & 0x01) != 0, (b & 0x10) == 0),
            ("Fuel system", (b & 0x02) != 0, (b & 0x20) == 0),
            ("Comprehensive components", (b & 0x04) != 0, (b & 0x40) == 0),
        };
        if ((b & 0x08) == 0)
        {
            // Spark ignition monitors (byte C = available, byte D = incomplete).
            string[] names = ["Catalyst", "Heated catalyst", "EVAP system", "Secondary air", "A/C refrigerant", "Oxygen sensor", "Oxygen sensor heater", "EGR/VVT system"];
            for (var i = 0; i < 8; i++)
            {
                if ((c & (1 << i)) != 0) monitors.Add((names[i], true, (d & (1 << i)) == 0));
            }
        }
        else
        {
            string[] names = ["NMHC catalyst", "NOx/SCR", "Reserved", "Boost pressure", "Reserved", "Exhaust gas sensor", "PM filter", "EGR/VVT system"];
            for (var i = 0; i < 8; i++)
            {
                if ((c & (1 << i)) != 0 && names[i] != "Reserved") monitors.Add((names[i], true, (d & (1 << i)) == 0));
            }
        }

        return new MonitorStatusData((a & 0x80) != 0, a & 0x7F, monitors);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_transport.IsOpen)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _transport.WriteLineAsync("ATPC", cts.Token);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Protocol close failed");
        }

        await _transport.DisposeAsync();
        _io.Dispose();
    }
}
