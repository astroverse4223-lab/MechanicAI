using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>A vehicle's service story, plus "shop knowledge": confirmed diagnoses across all vehicles.</summary>
public sealed partial class HistoryViewModel(IHistoryGateway history, IVehicleGateway vehicles, ISettingsStore settings, INavigationService navigation) : ViewModelBase
{
    public ObservableCollection<VehicleItem> Vehicles { get; } = [];

    public ObservableCollection<HistoryEntryItem> Entries { get; } = [];

    public ObservableCollection<DtcOccurrenceItem> DtcHistory { get; } = [];

    public ObservableCollection<ConfirmedDiagnosisItem> ConfirmedDiagnoses { get; } = [];

    [ObservableProperty]
    public partial VehicleItem? SelectedVehicle { get; set; }

    [ObservableProperty]
    public partial string? Filter { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchKnowledgeCommand))]
    public partial string? KnowledgeQuery { get; set; }

    [ObservableProperty]
    public partial bool HasEntries { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(async ct =>
        {
            var list = await vehicles.ListAsync(null, ct);
            Vehicles.Clear();
            foreach (var v in list) Vehicles.Add(VehicleItem.From(v));
        });

        var target = parameter as Guid? ?? SelectedVehicle?.Id ?? settings.Current.ActiveVehicleId;
        var match = Vehicles.FirstOrDefault(v => v.Id == target);
        if (match is not null && match != SelectedVehicle) SelectedVehicle = match;
        else if (SelectedVehicle is not null) await RunAsync(LoadHistoryAsync);
    }

    partial void OnSelectedVehicleChanged(VehicleItem? value) => _ = RunAsync(LoadHistoryAsync);

    [RelayCommand]
    private Task ApplyFilterAsync() => RunAsync(LoadHistoryAsync);

    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        Entries.Clear();
        DtcHistory.Clear();
        Summary = string.Empty;
        HasEntries = false;
        if (SelectedVehicle is not { } vehicle) return;
        var result = await history.GetVehicleHistoryAsync(vehicle.Id, Clean(Filter), ct);
        if (result is null)
        {
            ShowError(Application.Common.Error.NotFound("Vehicle"));
            return;
        }

        foreach (var item in result.Items) Entries.Add(HistoryEntryItem.From(item));
        foreach (var (code, occurrences, lastSeen) in result.DtcHistory) DtcHistory.Add(new DtcOccurrenceItem(code, occurrences, Format.Local(lastSeen)));
        HasEntries = Entries.Count > 0;
        Summary = $"{result.SessionCount} diagnostic session(s) · {result.RepairCount} repair(s) · {result.OpenRecallCount} open recall(s)";
    }

    private bool CanSearchKnowledge() => !string.IsNullOrWhiteSpace(KnowledgeQuery);

    [RelayCommand(CanExecute = nameof(CanSearchKnowledge))]
    private Task SearchKnowledgeAsync() => RunAsync(async ct =>
    {
        var results = await history.SearchConfirmedDiagnosesAsync(KnowledgeQuery!.Trim(), ct);
        ConfirmedDiagnoses.Clear();
        foreach (var r in results)
        {
            ConfirmedDiagnoses.Add(new ConfirmedDiagnosisItem(r.SessionId, r.Vehicle, r.Diagnosis, r.Complaint, Format.Join(r.Codes, "No codes"), Format.Local(r.WhenUtc)));
        }

        if (ConfirmedDiagnoses.Count == 0) StatusMessage = "No confirmed diagnoses match yet.";
    });

    [RelayCommand]
    private void OpenEntry(HistoryEntryItem? entry)
    {
        if (entry?.ReferenceId is not { } id) return;
        if (entry.Kind.Contains("Diagnos", StringComparison.OrdinalIgnoreCase) || entry.Kind.Contains("Session", StringComparison.OrdinalIgnoreCase))
        {
            navigation.NavigateTo(PageKey.Diagnostics, id);
        }
        else if (entry.Kind.Contains("Document", StringComparison.OrdinalIgnoreCase))
        {
            navigation.NavigateTo(PageKey.KnowledgeBase, new NavigationParameters.OpenDocument(id));
        }
    }

    [RelayCommand]
    private void OpenSession(ConfirmedDiagnosisItem? item)
    {
        if (item is not null) navigation.NavigateTo(PageKey.Diagnostics, item.SessionId);
    }
}
