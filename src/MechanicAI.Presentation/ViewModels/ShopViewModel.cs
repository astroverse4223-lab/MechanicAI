using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Shop tools: customers, estimates with live totals, and multi-point inspections.</summary>
public sealed partial class ShopViewModel(IShopGateway shop, IVehicleGateway vehicles, IDialogService dialogs, ISettingsStore settings) : ViewModelBase
{
    public static readonly IReadOnlyList<string> EstimateStatusLabels = Enum.GetNames<EstimateStatus>();

    public ObservableCollection<CustomerItem> Customers { get; } = [];

    public ObservableCollection<EstimateItem> Estimates { get; } = [];

    public ObservableCollection<InspectionSummaryItem> Inspections { get; } = [];

    public ObservableCollection<VehicleItem> VehicleChoices { get; } = [];

    /// <summary>Customers for the estimate editor ("No customer" first).</summary>
    public ObservableCollection<string> CustomerChoices { get; } = [];

    public ObservableCollection<EstimateLineItem> EstimateLines { get; } = [];

    public ObservableCollection<InspectionPointItem> InspectionPoints { get; } = [];

    public IReadOnlyList<string> EstimateStatuses => EstimateStatusLabels;

    public override Task OnNavigatedToAsync(object? parameter) => RunAsync(LoadAllAsync);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadAllAsync);

    private async Task LoadAllAsync(CancellationToken ct)
    {
        await LoadCustomersAsync(ct);
        await LoadEstimatesAsync(ct);
        await LoadInspectionsAsync(ct);
        var list = await vehicles.ListAsync(null, ct);
        VehicleChoices.Clear();
        foreach (var v in list) VehicleChoices.Add(VehicleItem.From(v));
    }

    // ================================================================= customers

    [ObservableProperty]
    public partial string? CustomerSearch { get; set; }

    [ObservableProperty]
    public partial CustomerItem? SelectedCustomer { get; set; }

    [ObservableProperty]
    public partial bool IsCustomerEditorOpen { get; set; }

    [ObservableProperty]
    public partial Guid? EditingCustomerId { get; set; }

    [ObservableProperty]
    public partial string? CustomerFirstName { get; set; }

    [ObservableProperty]
    public partial string? CustomerLastName { get; set; }

    [ObservableProperty]
    public partial string? CustomerCompany { get; set; }

    [ObservableProperty]
    public partial string? CustomerPhone { get; set; }

    [ObservableProperty]
    public partial string? CustomerEmail { get; set; }

    [ObservableProperty]
    public partial string? CustomerAddress { get; set; }

    [ObservableProperty]
    public partial string? CustomerCity { get; set; }

    [ObservableProperty]
    public partial string? CustomerRegion { get; set; }

    [ObservableProperty]
    public partial string? CustomerPostalCode { get; set; }

    [ObservableProperty]
    public partial string? CustomerNotes { get; set; }

    [ObservableProperty]
    public partial string? CustomerError { get; set; }

    private async Task LoadCustomersAsync(CancellationToken ct)
    {
        var list = await shop.ListCustomersAsync(Clean(CustomerSearch), ct);
        Customers.Clear();
        foreach (var c in list) Customers.Add(CustomerItem.From(c));
        CustomerChoices.Clear();
        CustomerChoices.Add("No customer");
        foreach (var c in Customers) CustomerChoices.Add(c.DisplayName);
    }

    [RelayCommand]
    private Task SearchCustomersAsync() => RunAsync(LoadCustomersAsync);

    [RelayCommand]
    private void NewCustomer()
    {
        EditingCustomerId = null;
        CustomerFirstName = CustomerLastName = CustomerCompany = CustomerPhone = CustomerEmail = null;
        CustomerAddress = CustomerCity = CustomerRegion = CustomerPostalCode = CustomerNotes = CustomerError = null;
        IsCustomerEditorOpen = true;
    }

    [RelayCommand]
    private void EditCustomer(CustomerItem? item)
    {
        var c = (item ?? SelectedCustomer)?.Customer;
        if (c is null) return;
        EditingCustomerId = c.Id;
        CustomerFirstName = c.FirstName;
        CustomerLastName = c.LastName;
        CustomerCompany = c.CompanyName;
        CustomerPhone = c.Phone;
        CustomerEmail = c.Email;
        CustomerAddress = c.AddressLine1;
        CustomerCity = c.City;
        CustomerRegion = c.Region;
        CustomerPostalCode = c.PostalCode;
        CustomerNotes = c.Notes;
        CustomerError = null;
        IsCustomerEditorOpen = true;
    }

    [RelayCommand]
    private void CancelCustomer() => IsCustomerEditorOpen = false;

    internal string? ValidateCustomer()
    {
        if (string.IsNullOrWhiteSpace(CustomerFirstName) && string.IsNullOrWhiteSpace(CustomerLastName) && string.IsNullOrWhiteSpace(CustomerCompany))
        {
            return "Enter a name or a company.";
        }

        if (!string.IsNullOrWhiteSpace(CustomerEmail) && !MailAddress.TryCreate(CustomerEmail.Trim(), out _)) return "The email address is not valid.";
        return null;
    }

    [RelayCommand]
    private Task SaveCustomerAsync() => RunAsync(async ct =>
    {
        CustomerError = ValidateCustomer();
        if (CustomerError is not null) return;
        var input = new CustomerInput(EditingCustomerId, CustomerFirstName?.Trim() ?? string.Empty, CustomerLastName?.Trim() ?? string.Empty, Clean(CustomerCompany),
            Clean(CustomerPhone), Clean(CustomerEmail), Clean(CustomerAddress), Clean(CustomerCity), Clean(CustomerRegion), Clean(CustomerPostalCode), Clean(CustomerNotes));
        var result = await shop.SaveCustomerAsync(input, ct);
        if (!result.IsSuccess)
        {
            CustomerError = result.Error?.Message;
            return;
        }

        IsCustomerEditorOpen = false;
        await LoadCustomersAsync(ct);
        SelectedCustomer = Customers.FirstOrDefault(c => c.Id == result.Value);
    });

    [RelayCommand]
    private async Task DeleteCustomerAsync(CustomerItem? item)
    {
        var customer = item ?? SelectedCustomer;
        if (customer is null) return;
        if (!await dialogs.ConfirmAsync("Delete customer?", $"Delete {customer.DisplayName}? Their vehicles are kept.", "Delete", "Cancel", destructive: true)) return;
        await RunAsync(async ct =>
        {
            if (Check(await shop.DeleteCustomerAsync(customer.Id, ct))) await LoadCustomersAsync(ct);
        });
    }

    // ================================================================= estimates

    [ObservableProperty]
    public partial EstimateItem? SelectedEstimate { get; set; }

    [ObservableProperty]
    public partial bool IsEstimateEditorOpen { get; set; }

    [ObservableProperty]
    public partial Guid? EditingEstimateId { get; set; }

    [ObservableProperty]
    public partial string EstimateTitle { get; set; } = "New estimate";

    [ObservableProperty]
    public partial int EstimateCustomerIndex { get; set; }

    [ObservableProperty]
    public partial int EstimateVehicleIndex { get; set; } = -1;

    [ObservableProperty]
    public partial int EstimateStatusIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaxText), nameof(TotalText))]
    public partial string TaxRatePercent { get; set; } = "0";

    [ObservableProperty]
    public partial string? EstimateNotes { get; set; }

    [ObservableProperty]
    public partial string? EstimateError { get; set; }

    public decimal Subtotal => EstimateLines.Sum(l => l.Total);

    public decimal Tax
    {
        get
        {
            var rate = (Format.ParseDecimal(TaxRatePercent) ?? 0m) / 100m;
            return decimal.Round(EstimateLines.Where(l => l.Taxable).Sum(l => l.Total) * rate, 2, MidpointRounding.AwayFromZero);
        }
    }

    public string SubtotalText => Format.Money(Subtotal);

    public string TaxText => Format.Money(Tax);

    public string TotalText => Format.Money(Subtotal + Tax);

    private async Task LoadEstimatesAsync(CancellationToken ct)
    {
        var list = await shop.ListEstimatesAsync(ct);
        Estimates.Clear();
        foreach (var e in list) Estimates.Add(EstimateItem.From(e));
    }

    [RelayCommand]
    private void NewEstimate()
    {
        EditingEstimateId = null;
        EstimateTitle = "New estimate";
        EstimateCustomerIndex = 0;
        var active = settings.Current.ActiveVehicleId;
        EstimateVehicleIndex = active is { } id ? VehicleChoices.ToList().FindIndex(v => v.Id == id) : -1;
        EstimateStatusIndex = 0;
        TaxRatePercent = "0";
        EstimateNotes = null;
        EstimateError = null;
        ClearLines();
        AddLine();
        IsEstimateEditorOpen = true;
    }

    [RelayCommand]
    private Task EditEstimateAsync(EstimateItem? item) => RunAsync(async ct =>
    {
        var target = item ?? SelectedEstimate;
        if (target is null) return;
        var e = await shop.GetEstimateAsync(target.Id, ct);
        if (e is null)
        {
            ShowError(Application.Common.Error.NotFound("Estimate"));
            return;
        }

        EditingEstimateId = e.Id;
        EstimateTitle = $"Estimate {e.Number}";
        EstimateCustomerIndex = e.CustomerId is { } cid ? Customers.ToList().FindIndex(c => c.Id == cid) + 1 : 0;
        EstimateVehicleIndex = e.VehicleId is { } vid ? VehicleChoices.ToList().FindIndex(v => v.Id == vid) : -1;
        EstimateStatusIndex = (int)e.Status;
        TaxRatePercent = (e.TaxRate * 100m).ToString("0.###", CultureInfo.CurrentCulture);
        EstimateNotes = e.Notes;
        EstimateError = null;
        ClearLines();
        foreach (var line in e.Lines)
        {
            AttachLine(new EstimateLineItem
            {
                KindIndex = (int)line.Kind,
                Description = line.Description,
                PartNumber = line.PartNumber,
                QuantityText = line.Quantity.ToString("0.##", CultureInfo.CurrentCulture),
                UnitPriceText = line.UnitPrice.ToString("0.00", CultureInfo.CurrentCulture),
                Taxable = line.Taxable,
            });
        }

        NotifyTotals();
        IsEstimateEditorOpen = true;
    });

    [RelayCommand]
    private void AddLine()
    {
        AttachLine(new EstimateLineItem());
        NotifyTotals();
    }

    [RelayCommand]
    private void RemoveLine(EstimateLineItem? line)
    {
        if (line is null) return;
        line.PropertyChanged -= OnLineChanged;
        EstimateLines.Remove(line);
        NotifyTotals();
    }

    private void AttachLine(EstimateLineItem line)
    {
        line.PropertyChanged += OnLineChanged;
        EstimateLines.Add(line);
    }

    private void ClearLines()
    {
        foreach (var line in EstimateLines) line.PropertyChanged -= OnLineChanged;
        EstimateLines.Clear();
        NotifyTotals();
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e) => NotifyTotals();

    private void NotifyTotals()
    {
        OnPropertyChanged(nameof(SubtotalText));
        OnPropertyChanged(nameof(TaxText));
        OnPropertyChanged(nameof(TotalText));
    }

    [RelayCommand]
    private void CancelEstimate() => IsEstimateEditorOpen = false;

    /// <summary>Validates the editor and converts lines. Returns an error message or null.</summary>
    internal string? ValidateEstimate(out decimal taxRate, out List<EstimateLineInput> lines)
    {
        taxRate = 0;
        lines = [];
        var rate = Format.ParseDecimal(TaxRatePercent);
        if (rate is null or < 0 or > 50) return "Tax rate must be a percentage between 0 and 50.";
        taxRate = rate.Value / 100m;
        var used = EstimateLines.Where(l => !string.IsNullOrWhiteSpace(l.Description)).ToList();
        if (used.Count == 0) return "Add at least one line with a description.";
        foreach (var line in used)
        {
            if (line.Quantity is not { } quantity || quantity < 0) return $"\"{line.Description}\": quantity must be a positive number.";
            if (line.UnitPrice is not { } price || price < 0) return $"\"{line.Description}\": price must be a positive number.";
            lines.Add(new EstimateLineInput(line.Kind, line.Description.Trim(), Clean(line.PartNumber), quantity, price, line.Taxable));
        }

        return null;
    }

    [RelayCommand]
    private Task SaveEstimateAsync() => RunAsync(async ct =>
    {
        EstimateError = ValidateEstimate(out var taxRate, out var lines);
        if (EstimateError is not null) return;
        Guid? customerId = EstimateCustomerIndex > 0 && EstimateCustomerIndex <= Customers.Count ? Customers[EstimateCustomerIndex - 1].Id : null;
        Guid? vehicleId = EstimateVehicleIndex >= 0 && EstimateVehicleIndex < VehicleChoices.Count ? VehicleChoices[EstimateVehicleIndex].Id : null;
        var status = (EstimateStatus)Math.Clamp(EstimateStatusIndex, 0, EstimateStatusLabels.Count - 1);
        var result = await shop.SaveEstimateAsync(EditingEstimateId, customerId, vehicleId, taxRate, Clean(EstimateNotes), lines, status, ct);
        if (!result.IsSuccess)
        {
            EstimateError = result.Error?.Message;
            return;
        }

        IsEstimateEditorOpen = false;
        await LoadEstimatesAsync(ct);
        ShowSuccess("Estimate saved.");
    });

    [RelayCommand]
    private async Task DeleteEstimateAsync(EstimateItem? item)
    {
        var estimate = item ?? SelectedEstimate;
        if (estimate is null) return;
        if (!await dialogs.ConfirmAsync("Delete estimate?", $"Delete estimate {estimate.Number}?", "Delete", "Cancel", destructive: true)) return;
        await RunAsync(async ct =>
        {
            if (Check(await shop.DeleteEstimateAsync(estimate.Id, ct))) await LoadEstimatesAsync(ct);
        });
    }

    // ================================================================= inspections

    [ObservableProperty]
    public partial InspectionSummaryItem? SelectedInspection { get; set; }

    [ObservableProperty]
    public partial int InspectionVehicleIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string? InspectionMileage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompleteInspectionCommand), nameof(SaveInspectionCommand))]
    public partial bool IsInspectionEditable { get; set; }

    private async Task LoadInspectionsAsync(CancellationToken ct)
    {
        var selected = SelectedInspection?.Id;
        var list = await shop.ListInspectionsAsync(ct);
        Inspections.Clear();
        foreach (var i in list) Inspections.Add(InspectionSummaryItem.From(i));
        _suppressInspectionLoad = true;
        SelectedInspection = Inspections.FirstOrDefault(i => i.Id == selected);
        _suppressInspectionLoad = false;
    }

    private bool _suppressInspectionLoad;

    partial void OnSelectedInspectionChanged(InspectionSummaryItem? value)
    {
        if (_suppressInspectionLoad) return;
        InspectionPoints.Clear();
        IsInspectionEditable = false;
        if (value is null) return;
        _ = RunAsync(ct => LoadInspectionPointsAsync(value.Id, ct));
    }

    private async Task LoadInspectionPointsAsync(Guid id, CancellationToken ct)
    {
        var inspection = (await shop.ListInspectionsAsync(ct)).FirstOrDefault(i => i.Id == id);
        InspectionPoints.Clear();
        if (inspection is null) return;
        foreach (var item in inspection.Items.OrderBy(i => i.SortOrder)) InspectionPoints.Add(new InspectionPointItem(item));
        IsInspectionEditable = inspection.Status == InspectionStatus.InProgress;
    }

    [RelayCommand]
    private Task StartInspectionAsync() => RunAsync(async ct =>
    {
        if (InspectionVehicleIndex < 0 || InspectionVehicleIndex >= VehicleChoices.Count)
        {
            ShowError(Application.Common.Error.Validation("Choose the vehicle to inspect."));
            return;
        }

        int? mileage = null;
        if (!string.IsNullOrWhiteSpace(InspectionMileage))
        {
            mileage = Format.ParseInt(InspectionMileage);
            if (mileage is null or < 0)
            {
                ShowError(Application.Common.Error.Validation("Mileage must be a whole number."));
                return;
            }
        }

        var result = await shop.StartInspectionAsync(VehicleChoices[InspectionVehicleIndex].Id, mileage, Clean(settings.Current.Diagnostics.TechnicianName), ct);
        if (!Check(result)) return;
        InspectionMileage = null;
        await LoadInspectionsAsync(ct);
        _suppressInspectionLoad = true;
        SelectedInspection = Inspections.FirstOrDefault(i => i.Id == result.Value);
        _suppressInspectionLoad = false;
        await LoadInspectionPointsAsync(result.Value, ct);
    });

    [RelayCommand(CanExecute = nameof(IsInspectionEditable))]
    private Task SaveInspectionAsync() => RunAsync(async ct =>
    {
        foreach (var point in InspectionPoints)
        {
            if (!Check(await shop.UpdateInspectionItemAsync(point.Id, point.Rating, Clean(point.Measurement), Clean(point.Notes), ct))) return;
        }

        StatusMessage = "Inspection saved.";
    });

    [RelayCommand(CanExecute = nameof(IsInspectionEditable))]
    private async Task CompleteInspectionAsync()
    {
        if (SelectedInspection is not { } inspection) return;
        var notInspected = InspectionPoints.Count(p => p.Rating == InspectionRating.NotInspected);
        if (notInspected > 0 &&
            !await dialogs.ConfirmAsync("Complete inspection?", $"{notInspected} item(s) are not inspected. Complete anyway?", "Complete", "Keep inspecting"))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            foreach (var point in InspectionPoints)
            {
                if (!Check(await shop.UpdateInspectionItemAsync(point.Id, point.Rating, Clean(point.Measurement), Clean(point.Notes), ct))) return;
            }

            if (!Check(await shop.CompleteInspectionAsync(inspection.Id, ct))) return;
            await LoadInspectionsAsync(ct);
            await LoadInspectionPointsAsync(inspection.Id, ct);
            ShowSuccess("Inspection completed.");
        });
    }
}
