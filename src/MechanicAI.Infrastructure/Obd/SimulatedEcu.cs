using System.Globalization;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.LiveData;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Obd;

public enum SimulatorScenario { HealthyWarmUp, LeanVacuumLeak }

/// <summary>
/// A virtual ELM327 adapter attached to a simulated gasoline V6. It exists so the live-data
/// pipeline (protocol parsing, decoding, graphing, recording, analysis) can be used and tested
/// without hardware. Everything it produces is labeled SIMULATED in the UI and in recordings,
/// and it never reports a VIN — it does not pretend to be a real vehicle.
/// </summary>
public sealed class SimulatedEcuTransport(SimulatorScenario scenario) : IObdTransport
{
    private readonly DateTime _start = DateTime.UtcNow;
    private readonly Random _random = new(42);
    private string _pending = string.Empty;
    private bool _open;
    private bool _codesCleared;
    private DateTime _clearedAt;

    public string Description => scenario == SimulatorScenario.LeanVacuumLeak ? "Simulator (lean condition)" : "Simulator (healthy engine)";

    public bool IsSimulated => true;

    public bool IsOpen => _open;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        _open = true;
        return Task.CompletedTask;
    }

    public async Task WriteLineAsync(string command, CancellationToken cancellationToken)
    {
        await Task.Delay(15, cancellationToken);
        _pending = Respond(command.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    public async Task<string> ReadUntilPromptAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await Task.Delay(20, cancellationToken);
        var response = _pending + "\r\r>";
        _pending = string.Empty;
        return response;
    }

    public ValueTask DisposeAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }

    private double Seconds => (DateTime.UtcNow - _start).TotalSeconds;

    /// <summary>Periodic "rev" to 2,500 rpm for 6 s every 30 s so graphs and trim-vs-RPM analysis have something to show.</summary>
    private bool Revving => Seconds % 30 is > 18 and < 24;

    private IEnumerable<(byte Pid, byte[] Data)> SupportedData()
    {
        var t = Seconds;
        var noise = (_random.NextDouble() - 0.5) * 2;
        var rpm = Revving ? 2500 + noise * 40 : 690 + 15 * Math.Sin(t * 0.7) + noise * 10;
        var ect = Math.Min(92, 45 + t * 0.35);
        var load = Revving ? 34 + noise : 21 + noise;
        var maf = Revving ? 13.5 + noise * 0.3 : 3.6 + noise * 0.1;
        var map = Revving ? 46 + noise : 31 + noise;
        var tps = Revving ? 24 + noise : 14.5;
        var lean = scenario == SimulatorScenario.LeanVacuumLeak;
        // Lean scenario: unmetered air has a large effect at idle and a small one at higher airflow.
        var stftBase = lean ? (Revving ? 3 : 11) : 0;
        var ltftBase = lean ? (Revving ? 6 : 14) : 2;
        var stft1 = stftBase + 3 * Math.Sin(t * 5) + noise;
        var stft2 = stftBase + 3 * Math.Sin(t * 5 + 1.3) + noise;
        var o2 = 0.45 + 0.4 * Math.Sign(Math.Sin(t * Math.PI * (Revving ? 2.2 : 1.1))) * Math.Min(1, Math.Abs(Math.Sin(t * Math.PI * 1.1)) * 3);
        var o2b2 = 0.45 + 0.4 * Math.Sign(Math.Sin(t * Math.PI * 1.1 + 0.8));

        byte B(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        byte[] Word(double v) => [(byte)((int)Math.Clamp(v, 0, 65535) >> 8), (byte)((int)Math.Clamp(v, 0, 65535) & 0xFF)];
        byte Trim(double pct) => B(pct * 128 / 100 + 128);

        yield return (0x04, [B(load * 255 / 100)]);
        yield return (0x05, [B(ect + 40)]);
        yield return (0x06, [Trim(stft1)]);
        yield return (0x07, [Trim(ltftBase)]);
        yield return (0x08, [Trim(stft2)]);
        yield return (0x09, [Trim(ltftBase)]);
        yield return (0x0B, [B(map)]);
        yield return (0x0C, Word(rpm * 4));
        yield return (0x0D, [0]);
        yield return (0x0E, [B(((Revving ? 24 : 12) + noise + 64) * 2)]);
        yield return (0x0F, [B(28 + 40)]);
        yield return (0x10, Word(maf * 100));
        yield return (0x11, [B(tps * 255 / 100)]);
        yield return (0x14, [B(Math.Clamp(o2, 0.05, 0.95) * 200), 0xFF]);
        yield return (0x15, [B(0.62 * 200 + noise * 4), 0xFF]);
        yield return (0x18, [B(Math.Clamp(o2b2, 0.05, 0.95) * 200), 0xFF]);
        yield return (0x19, [B(0.64 * 200 + noise * 4), 0xFF]);
        yield return (0x1F, Word(t));
        yield return (0x2F, [B(62 * 255 / 100)]);
        yield return (0x33, [100]);
        yield return (0x42, Word((14.2 + noise * 0.05) * 1000));
        yield return (0x46, [B(22 + 40)]);
    }

    private string Respond(string cmd)
    {
        if (cmd.StartsWith("AT", StringComparison.Ordinal))
        {
            return cmd switch
            {
                "ATZ" => "ELM327 v1.5",
                "ATI" => "ELM327 v1.5 (Mechanic AI SIMULATOR)",
                "ATDP" => "ISO 15765-4 (CAN 11/500) - SIMULATED",
                "ATDPN" => "A6",
                "ATRV" => "14.2V",
                _ => "OK",
            };
        }

        if (cmd.Length < 2) return "?";
        var service = cmd[..2];
        switch (service)
        {
            case "01":
            {
                if (cmd.Length < 4) return "?";
                var pid = byte.Parse(cmd.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var data = SupportedData().ToDictionary(d => d.Pid, d => d.Data);
                if (pid % 0x20 == 0) return $"41{pid:X2}{Convert.ToHexString(SupportBitmap(pid, data.Keys))}";
                if (pid == 0x01)
                {
                    var codes = StoredCodes().Count;
                    var a = (byte)((codes > 0 ? 0x80 : 0) | codes);
                    return $"4101{a:X2}07E500";
                }

                return data.TryGetValue(pid, out var bytes) ? $"41{pid:X2}{Convert.ToHexString(bytes)}" : "NO DATA";
            }

            case "03":
                return Codes("43", StoredCodes());
            case "07":
                return Codes("47", PendingCodes());
            case "0A":
                return Codes("4A", scenario == SimulatorScenario.LeanVacuumLeak ? ["0171"] : []);
            case "04":
                _codesCleared = true;
                _clearedAt = DateTime.UtcNow;
                return "44";
            case "02":
            {
                if (StoredCodes().Count == 0 || cmd.Length < 4) return "NO DATA";
                var pid = byte.Parse(cmd.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (pid == 0x02) return "42020001 71".Replace(" ", string.Empty, StringComparison.Ordinal);
                var snapshot = SupportedData().ToDictionary(d => d.Pid, d => d.Data);
                return snapshot.TryGetValue(pid, out var bytes) ? $"42{pid:X2}00{Convert.ToHexString(bytes)}" : "NO DATA";
            }

            case "09":
                return "NO DATA";
            default:
                return "?";
        }
    }

    private List<string> StoredCodes()
    {
        if (scenario != SimulatorScenario.LeanVacuumLeak) return [];
        // After a clear, the fault must re-mature before the code is stored again.
        return _codesCleared && (DateTime.UtcNow - _clearedAt).TotalSeconds < 120 ? [] : ["0171"];
    }

    private List<string> PendingCodes() =>
        scenario == SimulatorScenario.LeanVacuumLeak && _codesCleared && (DateTime.UtcNow - _clearedAt).TotalSeconds is > 45 and < 120 ? ["0171"] : [];

    private static string Codes(string header, List<string> codes) =>
        $"{header}{codes.Count:X2}{string.Concat(codes)}".PadRight(header.Length + 2 + 4, '0');

    private static byte[] SupportBitmap(byte basePid, IEnumerable<byte> pids)
    {
        var bitmap = new byte[4];
        foreach (var pid in pids.Append((byte)0x01).Append((byte)(basePid + 0x20)))
        {
            var offset = pid - basePid - 1;
            if (offset is < 0 or > 31) continue;
            if (pid == basePid + 0x20 && basePid >= 0x40) continue;
            bitmap[offset / 8] |= (byte)(0x80 >> (offset % 8));
        }

        return bitmap;
    }
}

/// <summary>Offers the simulator as a clearly labeled adapter.</summary>
public sealed class SimulatorObdProvider(ILogger<SimulatorObdProvider> logger) : IObdProvider
{
    public string Name => "Simulator";

    public Task<IReadOnlyList<ObdAdapterInfo>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ObdAdapterInfo>>(
        [
            new ObdAdapterInfo("sim:healthy", "SIMULATOR — healthy engine warming up", ObdAdapterKind.Simulator, null, true),
            new ObdAdapterInfo("sim:lean", "SIMULATOR — lean condition (P0171 demo)", ObdAdapterKind.Simulator, null, true),
        ]);

    public bool CanConnect(ObdAdapterInfo adapter) => adapter.Id.StartsWith("sim:", StringComparison.Ordinal);

    public async Task<IObdConnection> ConnectAsync(ObdAdapterInfo adapter, CancellationToken cancellationToken)
    {
        var scenario = adapter.Id == "sim:lean" ? SimulatorScenario.LeanVacuumLeak : SimulatorScenario.HealthyWarmUp;
        return await Elm327Connection.OpenAsync(new SimulatedEcuTransport(scenario), adapter, logger, cancellationToken);
    }
}
