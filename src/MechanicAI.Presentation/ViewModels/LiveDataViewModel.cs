using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.LiveData;
using MechanicAI.Application.Services;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>OBD-II through an ELM327 (or the labeled simulator): live PIDs, recordings, DTCs, freeze frame, monitors.</summary>
public sealed partial class LiveDataViewModel : ViewModelBase
{
    private readonly ILiveDataGateway _live;
    private readonly IDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settings;
    private readonly Dictionary<string, PidValueItem> _byKey;

    public LiveDataViewModel(ILiveDataGateway live, IDispatcher dispatcher, IDialogService dialogs, ISettingsStore settings)
    {
        _live = live;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _settings = settings;

        var defaults = new HashSet<string>(settings.Current.LiveData.DefaultPids, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in ObdPids.All)
        {
            Pids.Add(new PidValueItem(pid.Key, pid.Name, pid.Unit, pid.Min, pid.Max) { IsSelected = defaults.Contains(pid.Key) });
        }

        _byKey = Pids.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in Pids)
        {
            pid.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PidValueItem.IsSelected) or nameof(PidValueItem.IsSupported)) RefreshVisiblePids();
            };
        }

        RefreshVisiblePids();
        _live.FrameReceived += OnFrame;
        _live.StatusChanged += (_, status) => _dispatcher.Run(() =>
        {
            ConnectionStatus = status;
            SyncState();
        });
        SyncState();
    }

    public ObservableCollection<AdapterItem> Adapters { get; } = [];

    public ObservableCollection<PidValueItem> Pids { get; } = [];

    /// <summary>The selected, supported parameters shown as gauges.</summary>
    public ObservableCollection<PidValueItem> VisiblePids { get; } = [];

    public ObservableCollection<RecordingItem> Recordings { get; } = [];

    public ObservableCollection<PidStatItem> Statistics { get; } = [];

    public ObservableCollection<string> Observations { get; } = [];

    public ObservableCollection<LabeledValue> ScanResults { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial AdapterItem? SelectedAdapter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand), nameof(StartStreamingCommand), nameof(ReadDtcsCommand), nameof(ClearDtcsCommand),
        nameof(ReadFreezeFrameCommand), nameof(ReadVinCommand), nameof(ReadMonitorsCommand), nameof(StartRecordingCommand))]
    public partial bool IsConnected { get; set; }

    public bool IsDisconnected => !IsConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStreamingCommand), nameof(StopStreamingCommand), nameof(StartRecordingCommand))]
    public partial bool IsStreaming { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial bool IsSimulated { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "Not connected";

    [ObservableProperty]
    public partial string? RecordingTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeRecordingCommand), nameof(DeleteRecordingCommand))]
    public partial RecordingItem? SelectedRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysis))]
    public partial string? AnalysisText { get; set; }

    public bool HasAnalysis => !string.IsNullOrWhiteSpace(AnalysisText);

    [ObservableProperty]
    public partial string? ScanTitle { get; set; }

    public override Task OnNavigatedToAsync(object? parameter)
    {
        SyncState();
        return RunAsync(async ct =>
        {
            if (Adapters.Count == 0) await DiscoverCoreAsync(ct);
            await LoadRecordingsCoreAsync(ct);
        });
    }

    private void RefreshVisiblePids()
    {
        var wanted = Pids.Where(p => p.IsSelected && p.IsSupported).ToList();
        if (wanted.SequenceEqual(VisiblePids)) return;
        VisiblePids.Clear();
        foreach (var pid in wanted) VisiblePids.Add(pid);
    }

    private void SyncState()
    {
        IsConnected = _live.IsConnected;
        IsStreaming = _live.IsStreaming;
        IsRecording = _live.IsRecording;
        IsSimulated = _live.IsSimulated;
        if (IsConnected)
        {
            var supported = _live.SupportedPids;
            foreach (var pid in Pids) pid.IsSupported = supported.Count == 0 || supported.Contains(pid.Key);
        }
    }

    private void OnFrame(object? sender, LiveFrame frame) => _dispatcher.Run(() =>
    {
        foreach (var (key, value) in frame.Values)
        {
            if (_byKey.TryGetValue(key, out var item)) item.Value = value;
        }
    });

    [RelayCommand]
    private Task DiscoverAsync() => RunAsync(DiscoverCoreAsync, "Looking for adapters…");

    private async Task DiscoverCoreAsync(CancellationToken ct)
    {
        var adapters = await _live.DiscoverAsync(ct);
        Adapters.Clear();
        foreach (var a in adapters) Adapters.Add(new AdapterItem(a));
        var lastPort = _settings.Current.LiveData.LastPort;
        SelectedAdapter = Adapters.FirstOrDefault(a => lastPort is not null && a.Adapter.Port == lastPort)
                          ?? Adapters.FirstOrDefault(a => !a.Adapter.IsSimulator)
                          ?? Adapters.FirstOrDefault();
        if (Adapters.Count == 0) StatusMessage = "No adapters found. Plug in an ELM327 (USB or paired Bluetooth) and try again.";
    }

    private bool CanConnect() => SelectedAdapter is not null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => RunAsync(async ct =>
    {
        if (SelectedAdapter is not { } adapter) return;
        var result = await _live.ConnectAsync(adapter.Adapter, ct);
        SyncState();
        if (!Check(result)) return;
        if (adapter.Adapter.Port is { } port) await _settings.UpdateAsync(s => s.LiveData.LastPort = port, ct);
        if (adapter.Adapter.IsSimulator)
        {
            ShowNotice("Simulator", "You are connected to the built-in SIMULATOR. Values are synthetic and do not come from a vehicle.", NoticeSeverity.Warning);
        }
    }, "Connecting…");

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task DisconnectAsync() => RunAsync(async _ =>
    {
        await _live.DisconnectAsync();
        SyncState();
        foreach (var pid in Pids) pid.Value = null;
    });

    private bool CanStartStreaming() => IsConnected && !IsStreaming;

    [RelayCommand(CanExecute = nameof(CanStartStreaming))]
    private void StartStreaming()
    {
        var keys = Pids.Where(p => p.IsSelected && p.IsSupported).Select(p => p.Key).ToList();
        if (keys.Count == 0)
        {
            ShowError(Application.Common.Error.Validation("Select at least one supported parameter to stream."));
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.LiveData.PollIntervalMs, 50, 5000));
        try
        {
            _live.StartStreaming(keys, interval);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(Application.Common.Error.Validation(ex.Message));
        }

        SyncState();
    }

    [RelayCommand(CanExecute = nameof(IsStreaming))]
    private async Task StopStreamingAsync()
    {
        if (IsRecording) await StopRecordingAsync();
        await _live.StopStreamingAsync();
        SyncState();
    }

    private bool CanStartRecording() => IsConnected && IsStreaming && !IsRecording;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private Task StartRecordingAsync() => RunAsync(async ct =>
    {
        var s = _settings.Current;
        await _live.StartRecordingAsync(s.ActiveVehicleId, s.ActiveSessionId, Clean(RecordingTitle) ?? $"Capture {DateTime.Now:g}", ct);
        SyncState();
    });

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private Task StopRecordingAsync() => RunAsync(async ct =>
    {
        var result = await _live.StopRecordingAsync(ct);
        SyncState();
        if (!Check(result)) return;
        RecordingTitle = null;
        await LoadRecordingsCoreAsync(ct);
        SelectedRecording = Recordings.FirstOrDefault(r => r.Id == result.Value);
    });

    [RelayCommand]
    private Task LoadRecordingsAsync() => RunAsync(LoadRecordingsCoreAsync);

    private async Task LoadRecordingsCoreAsync(CancellationToken ct)
    {
        var list = await _live.ListRecordingsAsync(ct);
        Recordings.Clear();
        foreach (var r in list) Recordings.Add(RecordingItem.From(r));
    }

    partial void OnSelectedRecordingChanged(RecordingItem? value)
    {
        Statistics.Clear();
        Observations.Clear();
        AnalysisText = null;
        if (value is null) return;
        _ = RunAsync(async ct =>
        {
            var stats = await _live.ComputeStatisticsAsync(value.Id, ct);
            if (stats is null) return;
            foreach (var p in stats.Pids) Statistics.Add(PidStatItem.From(p));
            foreach (var o in stats.Observations) Observations.Add(o);
        });
    }

    private bool HasSelectedRecording() => SelectedRecording is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedRecording))]
    private Task AnalyzeRecordingAsync() => RunAsync(async ct =>
    {
        if (SelectedRecording is not { } recording) return;
        var buffer = new StringBuilder();
        AnalysisText = string.Empty;
        var result = await _live.AnalyzeAsync(recording.Id, null, text =>
        {
            lock (buffer) buffer.Append(text);
            _dispatcher.Run(() =>
            {
                lock (buffer) AnalysisText = buffer.ToString();
            });
            return Task.CompletedTask;
        }, ct);
        if (!Check(result))
        {
            AnalysisText = null;
            return;
        }

        AnalysisText = result.Value;
    }, "AI is reviewing the capture…");

    [RelayCommand(CanExecute = nameof(HasSelectedRecording))]
    private async Task DeleteRecordingAsync()
    {
        if (SelectedRecording is not { } recording) return;
        if (!await _dialogs.ConfirmAsync("Delete recording?", $"Delete \"{recording.Title}\"?", "Delete", "Cancel", destructive: true)) return;
        await RunAsync(async ct =>
        {
            if (!Check(await _live.DeleteRecordingAsync(recording.Id, ct))) return;
            SelectedRecording = null;
            await LoadRecordingsCoreAsync(ct);
        });
    }

    // ---------------------------------------------------------------- scan tools

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ReadDtcsAsync() => RunAsync(async ct =>
    {
        var result = await _live.ReadAllDtcsAsync(ct);
        if (!Check(result)) return;
        ScanTitle = "Trouble codes";
        ScanResults.Clear();
        if (result.Value!.Count == 0) ScanResults.Add(new LabeledValue("Result", "No trouble codes reported"));
        foreach (var dtc in result.Value) ScanResults.Add(new LabeledValue(dtc.Code, $"{dtc.Status}{(dtc.EcuAddress is null ? string.Empty : $" · ECU {dtc.EcuAddress}")}"));
    }, "Reading trouble codes…");

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task ClearDtcsAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync("Clear trouble codes?",
            "Clearing codes erases freeze-frame data and resets readiness monitors. Record everything you need first. Continue?",
            "Clear codes", "Cancel", destructive: true);
        if (!confirmed) return;
        await RunAsync(async ct =>
        {
            if (Check(await _live.ClearDtcsAsync(ct))) ShowSuccess("Trouble codes cleared. Monitors must re-run before an emissions test.");
        });
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ReadFreezeFrameAsync() => RunAsync(async ct =>
    {
        var result = await _live.ReadFreezeFrameAsync(ct);
        if (!Check(result)) return;
        ScanTitle = "Freeze frame";
        ScanResults.Clear();
        if (result.Value is not { } frame)
        {
            ScanResults.Add(new LabeledValue("Result", "No freeze frame stored"));
            return;
        }

        if (frame.TriggerDtc is not null) ScanResults.Add(new LabeledValue("Trigger DTC", frame.TriggerDtc));
        foreach (var (key, value) in frame.Values) ScanResults.Add(new LabeledValue(ObdPids.Find(key)?.Name ?? key, value));
    });

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ReadVinAsync() => RunAsync(async ct =>
    {
        var result = await _live.ReadVinAsync(ct);
        if (!Check(result)) return;
        ScanTitle = "VIN (Mode 09)";
        ScanResults.Clear();
        ScanResults.Add(new LabeledValue("VIN", result.Value ?? "The ECU did not report a VIN"));
    });

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private Task ReadMonitorsAsync() => RunAsync(async ct =>
    {
        var result = await _live.ReadMonitorsAsync(ct);
        if (!Check(result)) return;
        ScanTitle = "Readiness monitors";
        ScanResults.Clear();
        if (result.Value is not { } status)
        {
            ScanResults.Add(new LabeledValue("Result", "Monitor status not available"));
            return;
        }

        ScanResults.Add(new LabeledValue("MIL", status.MilOn ? "ON" : "Off"));
        ScanResults.Add(new LabeledValue("Stored DTCs", status.DtcCount.ToString(System.Globalization.CultureInfo.CurrentCulture)));
        foreach (var (monitor, available, complete) in status.Monitors)
        {
            ScanResults.Add(new LabeledValue(monitor, !available ? "Not supported" : complete ? "Complete" : "INCOMPLETE"));
        }
    });

    [RelayCommand]
    private void SelectDefaultPids()
    {
        var defaults = new HashSet<string>(_settings.Current.LiveData.DefaultPids, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in Pids) pid.IsSelected = defaults.Contains(pid.Key);
    }
}
