using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Dashboard: quick diagnosis entry, recent vehicles, open sessions, and system status.</summary>
public sealed partial class HomeViewModel(
    IVehicleGateway vehicles,
    IDiagnosticsGateway diagnostics,
    IDtcGateway dtcs,
    IKnowledgeBaseGateway knowledgeBase,
    ISystemGateway system,
    IConnectivityMonitor connectivity,
    ISettingsStore settings,
    INavigationService navigation,
    IClock clock) : ViewModelBase
{
    public ObservableCollection<VehicleItem> RecentVehicles { get; } = [];

    public ObservableCollection<SessionItem> OpenSessions { get; } = [];

    public ObservableCollection<string> AiNotes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartDiagnosisCommand))]
    public partial string QuickText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome";

    [ObservableProperty]
    public partial string AiStatusText { get; set; } = "Checking AI…";

    [ObservableProperty]
    public partial bool IsAiReady { get; set; }

    [ObservableProperty]
    public partial string DtcDatabaseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KnowledgeBaseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectivityText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowWelcome { get; set; }

    [ObservableProperty]
    public partial bool HasRecentVehicles { get; set; }

    [ObservableProperty]
    public partial bool HasOpenSessions { get; set; }

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    [RelayCommand]
    public Task LoadAsync() => RunAsync(LoadCoreAsync);

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        var s = settings.Current;
        Greeting = string.IsNullOrWhiteSpace(s.Diagnostics.TechnicianName) ? "Welcome" : $"Welcome, {s.Diagnostics.TechnicianName}";
        ShowWelcome = !s.FirstRunCompleted;
        ConnectivityText = connectivity.IsOnline ? "Online" : "Offline — web research and cloud AI are unavailable";

        var recent = await vehicles.RecentAsync(6, ct);
        RecentVehicles.Clear();
        foreach (var v in recent) RecentVehicles.Add(VehicleItem.From(v));
        HasRecentVehicles = RecentVehicles.Count > 0;

        var sessions = await diagnostics.ListAsync(8, null, includeClosed: false, ct);
        OpenSessions.Clear();
        var now = clock.UtcNow;
        foreach (var session in sessions) OpenSessions.Add(SessionItem.From(session, now));
        HasOpenSessions = OpenSessions.Count > 0;

        var count = await dtcs.CountAsync(ct);
        DtcDatabaseText = count > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{count:N0} trouble code definitions available offline")
            : "The trouble-code database is empty. Reference content may have failed to load — see the log.";

        var (documents, ready, chunks, _) = await knowledgeBase.GetStatsAsync(ct);
        KnowledgeBaseText = documents == 0
            ? "No documents yet — add service manuals, TSBs, and wiring diagrams to the knowledge base."
            : string.Create(CultureInfo.CurrentCulture, $"{documents:N0} documents ({ready:N0} ready, {chunks:N0} passages indexed)");

        var ai = await system.GetAiStatusAsync(refresh: false, ct);
        IsAiReady = ai.AnyChatAvailable;
        AiStatusText = ai.Mode == Application.Settings.AiMode.Disabled
            ? "AI features are turned off. Deterministic diagnostics still work."
            : ai.AnyChatAvailable
                ? $"AI ready — {ai.Mode} mode ({ai.LocalChatModel ?? ai.CloudModel})"
                : "No AI model available. Configure Ollama or a cloud provider in Settings.";
        AiNotes.Clear();
        foreach (var note in ai.Notes) AiNotes.Add(note);
    }

    [RelayCommand(CanExecute = nameof(CanStartDiagnosis))]
    private void StartDiagnosis()
    {
        var text = QuickText.Trim();
        QuickText = string.Empty;
        navigation.NavigateTo(PageKey.Diagnostics, new NavigationParameters.StartDiagnosisFromText(text, settings.Current.ActiveVehicleId));
    }

    private bool CanStartDiagnosis() => !string.IsNullOrWhiteSpace(QuickText);

    [RelayCommand]
    private void OpenVehicle(VehicleItem? vehicle)
    {
        if (vehicle is not null) navigation.NavigateTo(PageKey.Vehicles, vehicle.Id);
    }

    [RelayCommand]
    private void OpenSession(SessionItem? session)
    {
        if (session is not null) navigation.NavigateTo(PageKey.Diagnostics, session.Id);
    }

    [RelayCommand]
    private void GoTo(PageKey page) => navigation.NavigateTo(page);

    [RelayCommand]
    private Task InstallSampleDataAsync() => RunAsync(async ct =>
    {
        var result = await system.InstallSampleDataAsync(ct);
        if (!Check(result)) return;
        await settings.UpdateAsync(s =>
        {
            s.SampleDataInstalled = true;
            s.FirstRunCompleted = true;
        }, ct);
        ShowSuccess($"Installed {result.Value} sample records. Sample data is labeled everywhere it appears.");
        await LoadCoreAsync(ct);
    });

    [RelayCommand]
    private async Task DismissWelcomeAsync()
    {
        ShowWelcome = false;
        await settings.UpdateAsync(s => s.FirstRunCompleted = true);
    }
}
