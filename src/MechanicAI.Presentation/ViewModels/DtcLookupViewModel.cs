using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.ValueObjects;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Offline DTC reference: search by code, partial code, or description; definitions, playbooks, and safety.</summary>
public sealed partial class DtcLookupViewModel(
    IDtcGateway dtcs,
    IVehicleGateway vehicles,
    ISettingsStore settings,
    INavigationService navigation,
    IClipboard clipboard) : ViewModelBase
{
    public ObservableCollection<DtcResultItem> Results { get; } = [];

    public ObservableCollection<DefinitionItem> Definitions { get; } = [];

    public ObservableCollection<string> Symptoms { get; } = [];

    public ObservableCollection<string> PossibleCauses { get; } = [];

    public ObservableCollection<string> RelatedCodes { get; } = [];

    public ObservableCollection<PlaybookItem> Playbooks { get; } = [];

    public ObservableCollection<WarningItem> SafetyWarnings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Make { get; set; }

    [ObservableProperty]
    public partial string DatabaseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DtcResultItem? SelectedResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    [NotifyCanExecuteChangedFor(nameof(StartDiagnosisCommand), nameof(CopyCodeCommand))]
    public partial string? DetailCode { get; set; }

    public bool HasDetail => !string.IsNullOrEmpty(DetailCode);

    [ObservableProperty]
    public partial string DetailDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailSystem { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailScope { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SeenText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUnknownCode { get; set; }

    [ObservableProperty]
    public partial bool HasResults { get; set; }

    [ObservableProperty]
    public partial bool HasPlaybooks { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(async ct =>
        {
            var count = await dtcs.CountAsync(ct);
            DatabaseText = $"{count:N0} definitions · works offline";
            if (string.IsNullOrWhiteSpace(Make) && settings.Current.ActiveVehicleId is { } vid)
            {
                Make = (await vehicles.GetAsync(vid, ct))?.Make;
            }
        });

        if (parameter is string code && !string.IsNullOrWhiteSpace(code))
        {
            Query = code;
            await SearchAsync();
            await OpenCodeAsync(code);
        }
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => RunAsync(async ct =>
    {
        var results = await dtcs.SearchAsync(Query.Trim(), 50, ct);
        Results.Clear();
        foreach (var r in results) Results.Add(DtcResultItem.From(r));
        HasResults = Results.Count > 0;
        if (Results.Count == 0)
        {
            ShowNotice("No matches", DtcCode.TryParse(Query, out var code, assumePowertrain: true)
                ? $"{code.Value} is not in the offline database. It may be manufacturer-specific — check the OEM service information."
                : "No trouble codes match that search.", NoticeSeverity.Informational);
        }
        else if (Results.Count == 1)
        {
            await LoadDetailAsync(Results[0].Code, ct);
        }
    });

    partial void OnSelectedResultChanged(DtcResultItem? value)
    {
        if (value is not null && value.Code != DetailCode) _ = OpenCodeAsync(value.Code);
    }

    [RelayCommand]
    private Task OpenCodeAsync(string? code) =>
        string.IsNullOrWhiteSpace(code) ? Task.CompletedTask : RunAsync(ct => LoadDetailAsync(code, ct));

    private async Task LoadDetailAsync(string code, CancellationToken ct)
    {
        var detail = await dtcs.GetDetailAsync(code, Clean(Make), ct);
        Definitions.Clear();
        Symptoms.Clear();
        PossibleCauses.Clear();
        RelatedCodes.Clear();
        Playbooks.Clear();
        SafetyWarnings.Clear();
        if (detail is null)
        {
            DetailCode = null;
            ShowError(Error.Validation($"'{code}' is not a valid trouble code."));
            return;
        }

        DetailCode = detail.Code;
        IsUnknownCode = !detail.IsKnown;
        DetailDescription = detail.Primary?.Description ?? "Not in the offline database — consult the manufacturer's service information.";
        DetailSystem = $"{detail.System} · {detail.SubsystemFromCode}";
        DetailScope = detail.IsGeneric ? "Generic (SAE J2012) — same meaning on all vehicles" : "Manufacturer-controlled — meaning depends on the make";
        SeenText = detail.TimesSeenInSessions == 0 ? "Not seen in your sessions yet" : $"Seen in {detail.TimesSeenInSessions} of your diagnostic sessions";

        foreach (var d in detail.Definitions)
        {
            Definitions.Add(new DefinitionItem(d.Manufacturer ?? "Generic", d.Description, d.IsUserDefined ? $"{d.Source} (your entry)" : d.Source, d.Notes));
            foreach (var s in d.Symptoms.Where(s => !Symptoms.Contains(s))) Symptoms.Add(s);
            foreach (var c in d.Causes.Where(c => !PossibleCauses.Contains(c))) PossibleCauses.Add(c);
        }

        foreach (var r in detail.Related) RelatedCodes.Add($"{r.Code} — {r.Description}");
        foreach (var p in detail.Playbooks) Playbooks.Add(PlaybookItem.From(p));
        HasPlaybooks = Playbooks.Count > 0;
        foreach (var w in detail.Safety) SafetyWarnings.Add(WarningItem.From(w));
    }

    [RelayCommand(CanExecute = nameof(HasDetail))]
    private void StartDiagnosis()
    {
        if (DetailCode is { } code)
        {
            navigation.NavigateTo(PageKey.Diagnostics, new NavigationParameters.StartDiagnosisFromText(code, settings.Current.ActiveVehicleId));
        }
    }

    [RelayCommand(CanExecute = nameof(HasDetail))]
    private void CopyCode()
    {
        if (DetailCode is { } code) clipboard.SetText($"{code} — {DetailDescription}");
    }

    [RelayCommand]
    private Task OpenRelatedAsync(string? related) =>
        string.IsNullOrWhiteSpace(related) ? Task.CompletedTask : OpenCodeAsync(related.Split(' ', 2)[0]);
}
