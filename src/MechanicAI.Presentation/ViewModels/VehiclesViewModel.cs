using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Search;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Vehicle records: VIN decode, manual entry, specifications, recalls, and the active vehicle.</summary>
public sealed partial class VehiclesViewModel : ViewModelBase
{
    private readonly IVehicleGateway _vehicles;
    private readonly INavigationService _navigation;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settings;
    private readonly IClipboard _clipboard;
    private VinLookup? _lastLookup;

    public VehiclesViewModel(IVehicleGateway vehicles, INavigationService navigation, IDialogService dialogs, ISettingsStore settings, IClipboard clipboard)
    {
        _vehicles = vehicles;
        _navigation = navigation;
        _dialogs = dialogs;
        _settings = settings;
        _clipboard = clipboard;
    }

    public ObservableCollection<VehicleItem> Vehicles { get; } = [];

    public ObservableCollection<LabeledValue> Specifications { get; } = [];

    public ObservableCollection<RecallItem> Recalls { get; } = [];

    public ObservableCollection<LabeledValue> DecodedFields { get; } = [];

    public IReadOnlyList<string> MileageUnits { get; } = new[] { "Miles", "Kilometers" };

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVehicleCommand), nameof(EditVehicleCommand), nameof(RefreshRecallsCommand), nameof(RedecodeCommand),
        nameof(SetActiveCommand), nameof(StartDiagnosisCommand), nameof(OpenHistoryCommand), nameof(ToggleFavoriteCommand))]
    public partial VehicleItem? SelectedVehicle { get; set; }

    public bool HasSelection => SelectedVehicle is not null;

    [ObservableProperty]
    public partial string DetailTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailSubtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? DetailVin { get; set; }

    [ObservableProperty]
    public partial string? DetailNotes { get; set; }

    [ObservableProperty]
    public partial string DetailSource { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActiveVehicle { get; set; }

    // ---------------------------------------------------------------- VIN decode

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecodeVinCommand))]
    [NotifyPropertyChangedFor(nameof(VinCharacterCount))]
    public partial string VinInput { get; set; } = string.Empty;

    public string VinCharacterCount => $"{Vin.Normalize(VinInput).Length}/17";

    [ObservableProperty]
    public partial string? VinMileage { get; set; }

    [ObservableProperty]
    public partial string? VinValidationMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecodeResult))]
    public partial string? DecodeSummary { get; set; }

    public bool HasDecodeResult => !string.IsNullOrWhiteSpace(DecodeSummary);

    [ObservableProperty]
    public partial string? DecodeDetail { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDecodedVehicleCommand))]
    public partial bool CanSaveDecoded { get; set; }

    // ---------------------------------------------------------------- editor

    [ObservableProperty]
    public partial bool IsEditorOpen { get; set; }

    [ObservableProperty]
    public partial Guid? EditingId { get; set; }

    [ObservableProperty]
    public partial string EditorTitle { get; set; } = "Add vehicle";

    [ObservableProperty]
    public partial string? EditYear { get; set; }

    [ObservableProperty]
    public partial string? EditMake { get; set; }

    [ObservableProperty]
    public partial string? EditModel { get; set; }

    [ObservableProperty]
    public partial string? EditTrim { get; set; }

    [ObservableProperty]
    public partial string? EditEngine { get; set; }

    [ObservableProperty]
    public partial string? EditVin { get; set; }

    [ObservableProperty]
    public partial string? EditMileage { get; set; }

    [ObservableProperty]
    public partial int EditMileageUnitIndex { get; set; }

    [ObservableProperty]
    public partial string? EditPlate { get; set; }

    [ObservableProperty]
    public partial string? EditColor { get; set; }

    [ObservableProperty]
    public partial string? EditNotes { get; set; }

    [ObservableProperty]
    public partial string? EditorError { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadAsync();
        switch (parameter)
        {
            case Guid id:
                SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == id);
                if (SelectedVehicle is null)
                {
                    await RunAsync(ct => LoadDetailAsync(id, ct));
                }

                break;
            case NavigationParameters.DecodeVin decode:
                VinInput = decode.Vin;
                if (DecodeVinCommand.CanExecute(null)) await DecodeVinCommand.ExecuteAsync(null);
                break;
            case NavigationParameters.AddVehicle add:
                var parsed = QueryParser.Parse(add.Description);
                NewVehicle();
                EditYear = parsed.Year?.ToString(CultureInfo.InvariantCulture);
                EditMake = parsed.Make;
                EditModel = parsed.Model;
                EditEngine = parsed.Engine;
                EditMileage = parsed.Mileage?.ToString(CultureInfo.CurrentCulture);
                break;
        }
    }

    [RelayCommand]
    public Task LoadAsync() => RunAsync(LoadListAsync);

    [RelayCommand]
    private Task SearchAsync() => RunAsync(LoadListAsync);

    private async Task LoadListAsync(CancellationToken ct)
    {
        var selectedId = SelectedVehicle?.Id;
        var list = await _vehicles.ListAsync(Clean(SearchText), ct);
        Vehicles.Clear();
        foreach (var v in list) Vehicles.Add(VehicleItem.From(v));
        if (selectedId is { } id) SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == id);
    }

    partial void OnSelectedVehicleChanged(VehicleItem? value)
    {
        if (value is null)
        {
            ClearDetail();
            return;
        }

        _ = RunAsync(ct => LoadDetailAsync(value.Id, ct));
    }

    private void ClearDetail()
    {
        DetailTitle = string.Empty;
        DetailSubtitle = string.Empty;
        DetailVin = null;
        DetailNotes = null;
        DetailSource = string.Empty;
        IsActiveVehicle = false;
        Specifications.Clear();
        Recalls.Clear();
    }

    private async Task LoadDetailAsync(Guid id, CancellationToken ct)
    {
        var vehicle = await _vehicles.GetAsync(id, ct);
        if (vehicle is null)
        {
            ClearDetail();
            ShowError(Application.Common.Error.NotFound("Vehicle"));
            return;
        }

        DetailTitle = vehicle.DisplayName;
        var subtitle = new List<string>();
        if (!string.IsNullOrWhiteSpace(vehicle.Engine)) subtitle.Add(vehicle.Engine);
        if (!string.IsNullOrWhiteSpace(vehicle.Transmission)) subtitle.Add(vehicle.Transmission);
        if (!string.IsNullOrWhiteSpace(vehicle.Drivetrain)) subtitle.Add(vehicle.Drivetrain);
        if (vehicle.Mileage is { } m) subtitle.Add(vehicle.MileageUnit == DistanceUnit.Kilometers ? $"{m:N0} km" : $"{m:N0} mi");
        if (vehicle.Customer is { } c) subtitle.Add(c.DisplayName);
        DetailSubtitle = string.Join(" · ", subtitle);
        DetailVin = vehicle.Vin;
        DetailNotes = vehicle.Notes;
        DetailSource = vehicle.DataSource switch
        {
            VehicleDataSource.NhtsaVpic => $"Identity verified by NHTSA vPIC decode ({Format.Local(vehicle.DecodedUtc)})",
            VehicleDataSource.Sample => "Sample vehicle (fictional data for demonstration)",
            VehicleDataSource.ObdScan => "Identified from an OBD-II scan",
            _ => "Entered manually by the technician (not verified)",
        };
        IsActiveVehicle = _settings.Current.ActiveVehicleId == vehicle.Id;

        Specifications.Clear();
        foreach (var spec in vehicle.Specifications.OrderBy(s => s.Category).ThenBy(s => s.Name))
        {
            var suffix = spec.Verification == VerificationLevel.Verified ? string.Empty : " (unverified)";
            Specifications.Add(new LabeledValue($"{spec.Category} — {spec.Name}", spec.Value + suffix));
        }

        Recalls.Clear();
        foreach (var recall in vehicle.Recalls.OrderByDescending(r => r.ReportReceivedDate)) Recalls.Add(RecallItem.From(recall));
        await _vehicles.MarkAccessedAsync(vehicle.Id, ct);
    }

    // ---------------------------------------------------------------- VIN

    private bool CanDecodeVin() => Vin.Normalize(VinInput).Length == 17;

    [RelayCommand(CanExecute = nameof(CanDecodeVin))]
    private Task DecodeVinAsync() => RunAsync(async ct =>
    {
        DecodedFields.Clear();
        DecodeSummary = null;
        DecodeDetail = null;
        CanSaveDecoded = false;
        _lastLookup = null;
        if (!Vin.TryParse(VinInput, out _, out var error))
        {
            VinValidationMessage = error;
            return;
        }

        VinValidationMessage = null;
        var result = await _vehicles.DecodeVinAsync(VinInput, ct);
        if (!Check(result)) return;
        var lookup = result.Value!;
        _lastLookup = lookup;

        var checks = new List<string>();
        checks.Add(lookup.CheckDigitValid ? "check digit valid" : lookup.CheckDigitRequired ? "CHECK DIGIT INVALID — verify the VIN" : "check digit not used for this region");
        if (lookup.EstimatedModelYear is { } year) checks.Add($"model year ≈ {year}");
        checks.Add(lookup.Region);

        if (lookup.Decode is { } decoded)
        {
            var d = decoded.Value;
            DecodeSummary = d.IsUsable ? $"{d.ModelYear} {d.Make} {d.Model} {d.Trim}".Trim() : "The decoder did not identify this vehicle.";
            DecodeDetail = $"{string.Join(" · ", checks)} · source: {decoded.SourceName}{(decoded.FromCache ? " (cached)" : string.Empty)}";
            foreach (var field in d.Fields) DecodedFields.Add(new LabeledValue($"{field.Category} — {field.Name}", field.Value));
            foreach (var warning in d.Warnings) DecodedFields.Add(new LabeledValue("Decoder warning", warning));
            CanSaveDecoded = d.IsUsable;
        }
        else
        {
            DecodeSummary = "Checked locally only — the online decode is unavailable.";
            DecodeDetail = $"{string.Join(" · ", checks)}{(lookup.DecodeError is null ? string.Empty : " · " + lookup.DecodeError)}";
        }
    });

    [RelayCommand(CanExecute = nameof(CanSaveDecoded))]
    private Task SaveDecodedVehicleAsync() => RunAsync(async ct =>
    {
        if (_lastLookup is null) return;
        int? mileage = null;
        if (!string.IsNullOrWhiteSpace(VinMileage))
        {
            mileage = Format.ParseInt(VinMileage);
            if (mileage is null or < 0)
            {
                VinValidationMessage = "Mileage must be a whole number.";
                return;
            }
        }

        var result = await _vehicles.CreateFromVinAsync(_lastLookup.Vin, mileage, ct);
        if (!Check(result)) return;
        VinInput = string.Empty;
        VinMileage = null;
        DecodeSummary = null;
        DecodedFields.Clear();
        CanSaveDecoded = false;
        await LoadListAsync(ct);
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == result.Value);
        ShowSuccess("Vehicle saved with verified specifications.");
    });

    // ---------------------------------------------------------------- editor

    [RelayCommand]
    private void NewVehicle()
    {
        EditingId = null;
        EditorTitle = "Add vehicle";
        EditYear = EditMake = EditModel = EditTrim = EditEngine = EditVin = EditMileage = EditPlate = EditColor = EditNotes = null;
        EditMileageUnitIndex = 0;
        EditorError = null;
        IsEditorOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditVehicleAsync() => RunAsync(async ct =>
    {
        if (SelectedVehicle is null) return;
        var v = await _vehicles.GetAsync(SelectedVehicle.Id, ct);
        if (v is null) return;
        FillEditor(v);
        IsEditorOpen = true;
    });

    private void FillEditor(Vehicle v)
    {
        EditingId = v.Id;
        EditorTitle = $"Edit {v.DisplayName}";
        EditYear = v.Year?.ToString(CultureInfo.InvariantCulture);
        EditMake = v.Make;
        EditModel = v.Model;
        EditTrim = v.Trim;
        EditEngine = v.Engine;
        EditVin = v.Vin;
        EditMileage = v.Mileage?.ToString(CultureInfo.CurrentCulture);
        EditMileageUnitIndex = v.MileageUnit == DistanceUnit.Kilometers ? 1 : 0;
        EditPlate = v.LicensePlate;
        EditColor = v.Color;
        EditNotes = v.Notes;
        EditorError = null;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        EditorError = null;
    }

    /// <summary>Validates the editor fields. Returns an error message, or null when valid.</summary>
    internal string? ValidateEditor(out VehicleInput? input)
    {
        input = null;
        int? year = null;
        if (!string.IsNullOrWhiteSpace(EditYear))
        {
            year = Format.ParseInt(EditYear);
            if (year is null || year < 1950 || year > DateTime.UtcNow.Year + 2) return "Enter a model year between 1950 and next year.";
        }

        if (string.IsNullOrWhiteSpace(EditMake)) return "Make is required.";
        if (string.IsNullOrWhiteSpace(EditModel)) return "Model is required.";
        int? mileage = null;
        if (!string.IsNullOrWhiteSpace(EditMileage))
        {
            mileage = Format.ParseInt(EditMileage);
            if (mileage is null or < 0) return "Mileage must be a whole number.";
        }

        string? vin = null;
        if (!string.IsNullOrWhiteSpace(EditVin))
        {
            if (!Vin.TryParse(EditVin, out var parsed, out var vinError)) return vinError;
            vin = parsed.Value;
        }

        input = new VehicleInput
        {
            Id = EditingId,
            Vin = vin,
            Year = year,
            Make = EditMake.Trim(),
            Model = EditModel.Trim(),
            Trim = Clean(EditTrim),
            Engine = Clean(EditEngine),
            Mileage = mileage,
            MileageUnit = EditMileageUnitIndex == 1 ? DistanceUnit.Kilometers : DistanceUnit.Miles,
            LicensePlate = Clean(EditPlate),
            Color = Clean(EditColor),
            Notes = Clean(EditNotes),
        };
        return null;
    }

    [RelayCommand]
    private Task SaveVehicleAsync() => RunAsync(async ct =>
    {
        EditorError = ValidateEditor(out var input);
        if (EditorError is not null || input is null) return;
        var result = await _vehicles.SaveAsync(input, ct);
        if (!result.IsSuccess)
        {
            EditorError = result.Error?.Message;
            return;
        }

        IsEditorOpen = false;
        await LoadListAsync(ct);
        SelectedVehicle = Vehicles.FirstOrDefault(v => v.Id == result.Value);
        ShowSuccess("Vehicle saved.");
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteVehicleAsync()
    {
        if (SelectedVehicle is not { } vehicle) return;
        var confirmed = await _dialogs.ConfirmAsync("Delete vehicle?",
            $"Delete {vehicle.DisplayName}? Its diagnostic sessions, notes, and history will also be removed. This cannot be undone.",
            "Delete", "Cancel", destructive: true);
        if (!confirmed) return;
        await RunAsync(async ct =>
        {
            if (!Check(await _vehicles.DeleteAsync(vehicle.Id, ct))) return;
            if (_settings.Current.ActiveVehicleId == vehicle.Id) await _settings.UpdateAsync(s => s.ActiveVehicleId = null, ct);
            SelectedVehicle = null;
            await LoadListAsync(ct);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleFavoriteAsync() => RunAsync(async ct =>
    {
        if (SelectedVehicle is not { } vehicle) return;
        if (!Check(await _vehicles.SetFavoriteAsync(vehicle.Id, !vehicle.IsFavorite, ct))) return;
        await LoadListAsync(ct);
    });

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RefreshRecallsAsync() => RunAsync(async ct =>
    {
        if (SelectedVehicle is not { } vehicle) return;
        var result = await _vehicles.RefreshRecallsAsync(vehicle.Id, ct);
        if (!Check(result)) return;
        await LoadDetailAsync(vehicle.Id, ct);
        ShowNotice("Recalls updated", result.Value!.Count == 0 ? "NHTSA lists no recalls for this year/make/model." : $"{result.Value.Count} recall campaign(s) found. NHTSA data is not VIN-specific — confirm completion with the manufacturer.",
            Models.NoticeSeverity.Informational);
    }, "Checking NHTSA recalls…");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RedecodeAsync() => RunAsync(async ct =>
    {
        if (SelectedVehicle is not { } vehicle) return;
        if (!Check(await _vehicles.RedecodeAsync(vehicle.Id, ct))) return;
        await LoadDetailAsync(vehicle.Id, ct);
        ShowSuccess("Specifications refreshed from the VIN decode.");
    }, "Decoding VIN…");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SetActiveAsync()
    {
        if (SelectedVehicle is not { } vehicle) return;
        await _settings.UpdateAsync(s => s.ActiveVehicleId = vehicle.Id);
        IsActiveVehicle = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void StartDiagnosis()
    {
        if (SelectedVehicle is { } vehicle) _navigation.NavigateTo(PageKey.Diagnostics, new NavigationParameters.NewDiagnosisForVehicle(vehicle.Id));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenHistory()
    {
        if (SelectedVehicle is { } vehicle) _navigation.NavigateTo(PageKey.History, vehicle.Id);
    }

    [RelayCommand]
    private void CopyVin()
    {
        if (!string.IsNullOrWhiteSpace(DetailVin)) _clipboard.SetText(DetailVin);
    }
}
