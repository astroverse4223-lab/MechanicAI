using System.Globalization;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.LiveData;
using MechanicAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public sealed record LiveFrame(DateTime TimestampUtc, int OffsetMs, IReadOnlyDictionary<string, double> Values);

public sealed record PidStatistics(string Key, string Name, string Unit, int Samples, double Min, double Max, double Mean, double StdDev, double First, double Last);

public sealed record LiveDataStatistics(
    Guid RecordingId,
    TimeSpan Duration,
    IReadOnlyList<PidStatistics> Pids,
    IReadOnlyList<string> Observations);

public sealed record RecordingData(LiveDataSession Session, IReadOnlyDictionary<string, IReadOnlyList<(int OffsetMs, double Value)>> Series);

/// <summary>
/// OBD-II live data and scan-tool functions over any <see cref="IObdProvider"/> (serial/USB,
/// Bluetooth SPP, or the labeled simulator). Streaming runs off the UI thread; frames are
/// raised as events and, while recording, batched into the database.
/// </summary>
public sealed class LiveDataService(
    IEnumerable<IObdProvider> providers,
    IAppDbContextFactory dbFactory,
    IAiRouter router,
    ILogger<LiveDataService> logger) : IAsyncDisposable
{
    private readonly List<IObdProvider> _providers = providers.ToList();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LiveDataSample> _pending = [];
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private Guid? _recordingId;
    private DateTime _recordingStartUtc;
    private DateTime _streamStartUtc;

    public IObdConnection? Connection { get; private set; }

    public bool IsConnected => Connection?.IsConnected == true;

    public bool IsStreaming => _streamTask is { IsCompleted: false };

    public bool IsRecording => _recordingId is not null;

    public Guid? RecordingId => _recordingId;

    public IReadOnlySet<string> SupportedPids { get; private set; } = new HashSet<string>();

    public event EventHandler<LiveFrame>? FrameReceived;

    public event EventHandler<string>? StatusChanged;

    public async Task<IReadOnlyList<ObdAdapterInfo>> DiscoverAsync(CancellationToken ct = default)
    {
        var all = new List<ObdAdapterInfo>();
        foreach (var provider in _providers)
        {
            try
            {
                all.AddRange(await provider.DiscoverAsync(ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Adapter discovery failed for {Provider}", provider.Name);
            }
        }

        return all;
    }

    public async Task<Result> ConnectAsync(ObdAdapterInfo adapter, CancellationToken ct = default)
    {
        await DisconnectAsync();
        var provider = _providers.FirstOrDefault(p => p.CanConnect(adapter));
        if (provider is null) return Error.Validation("No driver supports this adapter.");
        try
        {
            StatusChanged?.Invoke(this, $"Connecting to {adapter.DisplayName}…");
            Connection = await provider.ConnectAsync(adapter, ct);
            SupportedPids = await Connection.LiveData.GetSupportedPidsAsync(ct);
            StatusChanged?.Invoke(this, $"Connected — {Connection.Protocol ?? "protocol unknown"}{(adapter.IsSimulator ? " (SIMULATOR)" : string.Empty)}");
            logger.LogInformation("OBD connected via {Adapter} ({Protocol}); {Count} PIDs supported", adapter.DisplayName, Connection.Protocol, SupportedPids.Count);
            return Result.Success();
        }
        catch (ExternalServiceException ex)
        {
            StatusChanged?.Invoke(this, ex.UserMessage);
            return new Error(ex.Kind, ex.UserMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "OBD connection failed");
            StatusChanged?.Invoke(this, "Connection failed.");
            return new Error(ErrorKind.Unavailable, "Could not connect to the adapter. Check that it is plugged in, the ignition is on, and no other program is using it.");
        }
    }

    public async Task DisconnectAsync()
    {
        await StopStreamingAsync();
        if (Connection is not null)
        {
            try
            {
                await Connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error while closing adapter");
            }

            Connection = null;
            SupportedPids = new HashSet<string>();
            StatusChanged?.Invoke(this, "Disconnected");
        }
    }

    public void StartStreaming(IReadOnlyList<string> pidKeys, TimeSpan interval)
    {
        if (Connection is null) throw new InvalidOperationException("Connect to an adapter first.");
        if (IsStreaming) return;
        var keys = pidKeys.Where(k => ObdPids.Find(k) is not null && (SupportedPids.Count == 0 || SupportedPids.Contains(k))).Distinct().ToList();
        _streamCts = new CancellationTokenSource();
        _streamStartUtc = DateTime.UtcNow;
        var token = _streamCts.Token;
        _streamTask = Task.Run(() => StreamLoopAsync(keys, interval, token), token);
    }

    public async Task StopStreamingAsync()
    {
        if (_streamCts is null) return;
        await _streamCts.CancelAsync();
        try
        {
            if (_streamTask is not null) await _streamTask;
        }
        catch (OperationCanceledException)
        {
        }

        _streamCts.Dispose();
        _streamCts = null;
        _streamTask = null;
        await FlushAsync(CancellationToken.None);
    }

    private async Task StreamLoopAsync(IReadOnlyList<string> keys, TimeSpan interval, CancellationToken ct)
    {
        var failures = 0;
        var lastFlush = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && Connection is { } connection)
        {
            var started = DateTime.UtcNow;
            try
            {
                var readings = await connection.LiveData.ReadAsync(keys, ct);
                failures = 0;
                var now = DateTime.UtcNow;
                var offset = (int)(now - _streamStartUtc).TotalMilliseconds;
                var frame = new LiveFrame(now, offset, readings.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last().Value));
                FrameReceived?.Invoke(this, frame);

                if (_recordingId is { } recordingId)
                {
                    var recOffset = (int)(now - _recordingStartUtc).TotalMilliseconds;
                    lock (_pending)
                    {
                        _pending.AddRange(frame.Values.Select(v => new LiveDataSample { SessionId = recordingId, OffsetMs = recOffset, Pid = v.Key, Value = v.Value }));
                    }

                    if (now - lastFlush > TimeSpan.FromSeconds(2))
                    {
                        await FlushAsync(ct);
                        lastFlush = now;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogWarning(ex, "Live data read failed ({Failures} in a row)", failures);
                StatusChanged?.Invoke(this, failures >= 5 ? "Lost communication with the vehicle." : "Read error — retrying…");
                if (failures >= 5) break;
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }

            var elapsed = DateTime.UtcNow - started;
            if (elapsed < interval) await Task.Delay(interval - elapsed, ct);
        }
    }

    public async Task<Guid> StartRecordingAsync(Guid? vehicleId, Guid? sessionId, string? title, CancellationToken ct = default)
    {
        if (Connection is null) throw new InvalidOperationException("Connect to an adapter first.");
        await using var db = await dbFactory.CreateAsync(ct);
        var session = new LiveDataSession
        {
            VehicleId = vehicleId,
            DiagnosticSessionId = sessionId,
            Title = string.IsNullOrWhiteSpace(title) ? $"Live data {DateTime.Now:g}" : title.Trim(),
            AdapterDescription = Connection.Adapter.DisplayName + (Connection.AdapterVersion is null ? string.Empty : $" ({Connection.AdapterVersion})"),
            Protocol = Connection.Protocol,
            IsSimulated = Connection.Adapter.IsSimulator,
            Pids = SupportedPids.ToList(),
        };
        db.LiveDataSessions.Add(session);
        await db.SaveChangesAsync(ct);
        _recordingStartUtc = DateTime.UtcNow;
        _recordingId = session.Id;
        StatusChanged?.Invoke(this, "Recording…");
        return session.Id;
    }

    public async Task<Result<Guid>> StopRecordingAsync(CancellationToken ct = default)
    {
        if (_recordingId is not { } id) return Error.Validation("Not recording.");
        _recordingId = null;
        await FlushAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await db.LiveDataSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Error.NotFound("Recording");
        session.EndedUtc = DateTime.UtcNow;
        session.SampleCount = await db.LiveDataSamples.CountAsync(s => s.SessionId == id, ct);
        session.Pids = await db.LiveDataSamples.Where(s => s.SessionId == id).Select(s => s.Pid).Distinct().ToListAsync(ct);
        await db.SaveChangesAsync(ct);
        StatusChanged?.Invoke(this, $"Recording saved ({session.SampleCount:N0} samples)");
        return id;
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        List<LiveDataSample> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateAsync(ct);
            db.LiveDataSamples.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LiveDataSession>> ListRecordingsAsync(Guid? vehicleId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.LiveDataSessions.AsNoTracking();
        if (vehicleId is { } v) query = query.Where(s => s.VehicleId == v);
        return await query.OrderByDescending(s => s.StartedUtc).Take(200).ToListAsync(ct);
    }

    public async Task<RecordingData?> LoadRecordingAsync(Guid recordingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await db.LiveDataSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == recordingId, ct);
        if (session is null) return null;
        var samples = await db.LiveDataSamples.AsNoTracking().Where(s => s.SessionId == recordingId)
            .OrderBy(s => s.OffsetMs).Select(s => new { s.Pid, s.OffsetMs, s.Value }).ToListAsync(ct);
        var series = samples.GroupBy(s => s.Pid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(int, double)>)g.Select(s => (s.OffsetMs, s.Value)).ToList());
        return new RecordingData(session, series);
    }

    public async Task<Result> DeleteRecordingAsync(Guid recordingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await db.LiveDataSessions.FirstOrDefaultAsync(s => s.Id == recordingId, ct);
        if (session is null) return Error.NotFound("Recording");
        db.LiveDataSessions.Remove(session);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<LiveDataStatistics?> ComputeStatisticsAsync(Guid recordingId, CancellationToken ct = default)
    {
        var data = await LoadRecordingAsync(recordingId, ct);
        return data is null ? null : ComputeStatistics(data);
    }

    /// <summary>Per-PID statistics plus relationship observations (worded as observations, not verdicts).</summary>
    public static LiveDataStatistics ComputeStatistics(RecordingData data)
    {
        var stats = new List<PidStatistics>();
        foreach (var (key, points) in data.Series.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (points.Count == 0) continue;
            var values = points.Select(p => p.Value).ToList();
            var mean = values.Average();
            var std = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
            var def = ObdPids.Find(key);
            stats.Add(new PidStatistics(key, def?.Name ?? key, def?.Unit ?? string.Empty, values.Count, values.Min(), values.Max(),
                Math.Round(mean, 3), Math.Round(std, 3), values[0], values[^1]));
        }

        var observations = new List<string>();
        double? Avg(string key, Func<int, bool> when)
        {
            if (!data.Series.TryGetValue(key, out var series)) return null;
            if (!data.Series.TryGetValue("RPM", out var rpm)) return series.Count == 0 ? null : series.Average(s => s.Value);
            var rpmAt = rpm.ToDictionary(r => r.OffsetMs, r => r.Value);
            var selected = series.Where(s => rpmAt.TryGetValue(s.OffsetMs, out var r) && when((int)r)).Select(s => s.Value).ToList();
            return selected.Count < 3 ? null : selected.Average();
        }

        foreach (var bank in new[] { "1", "2" })
        {
            var idle = Avg("STFT" + bank, r => r is > 400 and < 1100) + Avg("LTFT" + bank, r => r is > 400 and < 1100);
            var cruise = Avg("STFT" + bank, r => r > 2000) + Avg("LTFT" + bank, r => r > 2000);
            if (idle is { } i)
            {
                observations.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Bank {bank} total fuel trim (STFT+LTFT) averaged {i:+0.0;-0.0}% at idle{(cruise is { } c ? $" and {c:+0.0;-0.0}% above 2,000 rpm" : string.Empty)}."));
                if (cruise is { } c2 && i - c2 > 8)
                {
                    observations.Add($"Bank {bank} trims are notably more positive at idle than at higher RPM — a pattern commonly associated with unmetered air (vacuum leak). This is a general pattern, not a diagnosis; confirm with a smoke test.");
                }
                else if (cruise is { } c3 && c3 - i > 8)
                {
                    observations.Add($"Bank {bank} trims become more positive as RPM/load rises — a pattern commonly associated with fuel delivery or airflow-measurement issues. Confirm with fuel pressure/volume and MAF plausibility tests.");
                }
            }
        }

        if (data.Series.TryGetValue("ECT", out var ect) && ect.Count > 5)
        {
            var max = ect.Max(e => e.Value);
            observations.Add(string.Create(CultureInfo.InvariantCulture, $"Coolant temperature ranged {ect.Min(e => e.Value):0}–{max:0} °C during the recording."));
        }

        if (data.Series.TryGetValue("VPWR", out var volts) && volts.Count > 5)
        {
            observations.Add(string.Create(CultureInfo.InvariantCulture, $"Control module voltage ranged {volts.Min(v => v.Value):0.00}–{volts.Max(v => v.Value):0.00} V."));
        }

        if (data.Series.TryGetValue("O2S1B1", out var o2) && o2.Count > 20)
        {
            var crossings = 0;
            for (var i = 1; i < o2.Count; i++)
            {
                if ((o2[i - 1].Value < 0.45) != (o2[i].Value < 0.45)) crossings++;
            }

            var seconds = Math.Max(1, (o2[^1].OffsetMs - o2[0].OffsetMs) / 1000.0);
            observations.Add(string.Create(CultureInfo.InvariantCulture,
                $"Upstream O2 sensor (B1S1) crossed 0.45 V {crossings} times in {seconds:0} s ({crossings / seconds:0.0}/s). Switching behavior is only meaningful in closed loop at steady operating conditions."));
        }

        var duration = data.Session.EndedUtc is { } end ? end - data.Session.StartedUtc : TimeSpan.Zero;
        return new LiveDataStatistics(data.Session.Id, duration, stats, observations);
    }

    public async Task<Result<string>> AnalyzeAsync(Guid recordingId, string? vehicleContext, Func<string, Task>? onText, CancellationToken ct = default)
    {
        var data = await LoadRecordingAsync(recordingId, ct);
        if (data is null) return Error.NotFound("Recording");
        var stats = ComputeStatistics(data);
        if (stats.Pids.Count == 0) return Error.Validation("This recording has no samples to analyze.");

        var route = await router.ResolveChatAsync(AiTask.LiveDataAnalysis, DataSensitivity.General, cancellationToken: ct);
        if (!route.IsAvailable) return Error.NotConfigured(route.UnavailableReason!);

        var prompt = new StringBuilder();
        prompt.Append("Vehicle: ").Append(string.IsNullOrWhiteSpace(vehicleContext) ? "not specified" : vehicleContext).Append('\n');
        if (data.Session.IsSimulated) prompt.Append("NOTE: This recording came from the built-in SIMULATOR, not a real vehicle. Say so in your analysis.\n");
        prompt.Append(string.Create(CultureInfo.InvariantCulture, $"Duration: {stats.Duration.TotalSeconds:0} s\n\nPID statistics (min / max / mean / std-dev):\n"));
        foreach (var p in stats.Pids)
        {
            prompt.Append(string.Create(CultureInfo.InvariantCulture, $"- {p.Name} [{p.Key}]: {p.Min:0.###} / {p.Max:0.###} / {p.Mean:0.###} / {p.StdDev:0.###} {p.Unit} ({p.Samples} samples)\n"));
        }

        prompt.Append("\nComputed observations:\n");
        foreach (var o in stats.Observations) prompt.Append("- ").Append(o).Append('\n');

        var sb = new StringBuilder();
        await foreach (var update in route.Model!.StreamAsync(new ChatRequest
                       {
                           SystemPrompt = Prompts.LiveDataAnalysis,
                           Messages = [ChatMessage.User(prompt.ToString())],
                           Temperature = 0.1,
                       }, ct))
        {
            if (update is not TextDeltaUpdate t) continue;
            sb.Append(t.Text);
            if (onText is not null) await onText(t.Text);
        }

        var analysis = $"_AI analysis ({route.Model.Provider} · {route.Model.Model}) — inference, not a diagnosis._\n\n{sb}";
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await db.LiveDataSessions.FirstOrDefaultAsync(s => s.Id == recordingId, ct);
        if (session is not null)
        {
            session.AnalysisMarkdown = analysis;
            await db.SaveChangesAsync(ct);
        }

        return analysis;
    }

    // ---------------------------------------------------------------- scan-tool functions

    public async Task<Result<IReadOnlyList<ScannedDtc>>> ReadAllDtcsAsync(CancellationToken ct = default)
    {
        if (Connection is null) return Error.Validation("Connect to an adapter first.");
        try
        {
            var list = new List<ScannedDtc>();
            list.AddRange(await Connection.Scanner.ReadStoredDtcsAsync(ct));
            list.AddRange(await Connection.Scanner.ReadPendingDtcsAsync(ct));
            list.AddRange(await Connection.Scanner.ReadPermanentDtcsAsync(ct));
            return list;
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }
    }

    public async Task<Result> ClearDtcsAsync(CancellationToken ct = default)
    {
        if (Connection is null) return Error.Validation("Connect to an adapter first.");
        try
        {
            var ok = await Connection.Scanner.ClearDtcsAsync(ct);
            logger.LogInformation("DTC clear requested via OBD (success={Success})", ok);
            return ok ? Result.Success() : Error.Validation("The vehicle did not confirm the clear request. Some vehicles require the engine off and key on.");
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }
    }

    public async Task<Result<FreezeFrameData?>> ReadFreezeFrameAsync(CancellationToken ct = default)
    {
        if (Connection is null) return Error.Validation("Connect to an adapter first.");
        try
        {
            return Result<FreezeFrameData?>.Success(await Connection.Scanner.ReadFreezeFrameAsync(ct));
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }
    }

    public async Task<Result<string?>> ReadVinAsync(CancellationToken ct = default)
    {
        if (Connection is null) return Error.Validation("Connect to an adapter first.");
        try
        {
            return Result<string?>.Success(await Connection.Scanner.ReadVinAsync(ct));
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }
    }

    public async Task<Result<MonitorStatusData?>> ReadMonitorsAsync(CancellationToken ct = default)
    {
        if (Connection is null) return Error.Validation("Connect to an adapter first.");
        try
        {
            return Result<MonitorStatusData?>.Success(await Connection.Scanner.ReadMonitorStatusAsync(ct));
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }
}
