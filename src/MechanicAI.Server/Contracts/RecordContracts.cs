using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Server.Contracts;

public sealed record CreatedResponse(Guid Id);

// ---------------------------------------------------------------- vehicles

public sealed record VehicleDto(
    Guid Id,
    string DisplayName,
    string Description,
    string? Vin,
    int? Year,
    string Make,
    string Model,
    string? Trim,
    string? Engine,
    decimal? DisplacementLiters,
    int? Cylinders,
    string? FuelType,
    string? Drivetrain,
    string? Transmission,
    string? BodyClass,
    int? Mileage,
    DistanceUnit MileageUnit,
    string? Color,
    string? LicensePlate,
    Guid? CustomerId,
    string? CustomerName,
    string? Notes,
    VehicleDataSource DataSource,
    DateTime? DecodedUtc,
    DateTime? RecallsCheckedUtc,
    bool IsFavorite,
    bool IsSample,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<VehicleSpecificationDto> Specifications,
    IReadOnlyList<RecallDto> Recalls)
{
    public static VehicleDto From(Vehicle v) => new(
        v.Id, v.DisplayName, v.Description, v.Vin, v.Year, v.Make, v.Model, v.Trim, v.Engine, v.DisplacementLiters, v.Cylinders,
        v.FuelType, v.Drivetrain, v.Transmission, v.BodyClass, v.Mileage, v.MileageUnit, v.Color, v.LicensePlate, v.CustomerId,
        v.Customer?.DisplayName, v.Notes, v.DataSource, v.DecodedUtc, v.RecallsCheckedUtc, v.IsFavorite, v.IsSample, v.CreatedUtc, v.UpdatedUtc,
        v.Specifications.Select(VehicleSpecificationDto.From).ToList(), v.Recalls.Select(RecallDto.From).ToList());
}

public sealed record VehicleSpecificationDto(string Category, string Name, string Value, string Source, DateTime RetrievedUtc, VerificationLevel Verification)
{
    public static VehicleSpecificationDto From(VehicleSpecification s) => new(s.Category, s.Name, s.Value, s.Source, s.RetrievedUtc, s.Verification);
}

public sealed record RecallDto(
    Guid Id,
    string CampaignNumber,
    string? Manufacturer,
    string? Component,
    string? Summary,
    string? Consequence,
    string? Remedy,
    string? Notes,
    DateTime? ReportReceivedDate,
    bool ParkIt,
    bool ParkOutside,
    bool OverTheAirUpdate,
    RecallStatus Status,
    DateTime RetrievedUtc,
    string? SourceUrl)
{
    public static RecallDto From(Recall r) => new(r.Id, r.CampaignNumber, r.Manufacturer, r.Component, r.Summary, r.Consequence, r.Remedy,
        r.Notes, r.ReportReceivedDate, r.ParkIt, r.ParkOutside, r.OverTheAirUpdate, r.Status, r.RetrievedUtc, r.SourceUrl);
}

public sealed record CreateVehicleFromVinRequest(string Vin, int? Mileage, Guid? CustomerId);

public sealed record SetRecallStatusRequest(RecallStatus Status);

public sealed record FavoriteRequest(bool Favorite);

// ---------------------------------------------------------------- customers

public sealed record CustomerDto(
    Guid Id,
    string DisplayName,
    string FirstName,
    string LastName,
    string? CompanyName,
    string? Phone,
    string? Email,
    string? AddressLine1,
    string? City,
    string? Region,
    string? PostalCode,
    string? Notes,
    bool IsSample,
    IReadOnlyList<CustomerVehicleDto> Vehicles,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    public static CustomerDto From(Customer c) => new(c.Id, c.DisplayName, c.FirstName, c.LastName, c.CompanyName, c.Phone, c.Email,
        c.AddressLine1, c.City, c.Region, c.PostalCode, c.Notes, c.IsSample,
        c.Vehicles.Select(v => new CustomerVehicleDto(v.Id, v.DisplayName, v.Vin)).ToList(), c.CreatedUtc, c.UpdatedUtc);
}

public sealed record CustomerVehicleDto(Guid Id, string DisplayName, string? Vin);

public sealed record CustomerRequest(string FirstName, string LastName, string? CompanyName, string? Phone, string? Email,
    string? AddressLine1, string? City, string? Region, string? PostalCode, string? Notes)
{
    public CustomerInput ToInput(Guid? id) => new(id, FirstName ?? string.Empty, LastName ?? string.Empty, CompanyName, Phone, Email,
        AddressLine1, City, Region, PostalCode, Notes);
}

// ---------------------------------------------------------------- history

public sealed record VehicleHistoryDto(
    Guid VehicleId,
    string VehicleName,
    IReadOnlyList<VehicleHistoryItem> Items,
    IReadOnlyList<DtcOccurrenceDto> DtcHistory,
    int SessionCount,
    int RepairCount,
    int OpenRecallCount)
{
    public static VehicleHistoryDto From(VehicleHistory h) => new(
        h.VehicleId,
        h.VehicleName,
        h.Items,
        h.DtcHistory.Select(d => new DtcOccurrenceDto(d.Code, d.Occurrences, d.LastSeenUtc)).ToList(),
        h.SessionCount,
        h.RepairCount,
        h.OpenRecallCount);
}

public sealed record DtcOccurrenceDto(string Code, int Occurrences, DateTime LastSeenUtc);

public sealed record ConfirmedDiagnosisDto(Guid SessionId, string Vehicle, string Diagnosis, string Complaint, IReadOnlyList<string> Codes, DateTime WhenUtc);

// ---------------------------------------------------------------- repairs & notes

public sealed record RepairDto(
    Guid Id,
    Guid? VehicleId,
    string? VehicleName,
    Guid? DiagnosticSessionId,
    RepairKind Kind,
    RepairStatus Status,
    string Title,
    string? Description,
    DateTime PerformedUtc,
    int? Mileage,
    string? TechnicianName,
    decimal? LaborHours,
    IReadOnlyList<PartDto> Parts)
{
    public static RepairDto From(Repair r) => new(r.Id, r.VehicleId, r.Vehicle?.DisplayName, r.DiagnosticSessionId, r.Kind, r.Status, r.Title,
        r.Description, r.PerformedUtc, r.Mileage, r.TechnicianName, r.LaborHours,
        r.Parts.Select(p => new PartDto(p.Description, p.PartNumber, p.Brand, p.Quantity, p.UnitPrice)).ToList());
}

public sealed record PartDto(string Description, string? PartNumber, string? Brand, decimal Quantity, decimal? UnitPrice);

public sealed record AddRepairRequest(Guid? VehicleId, RepairKind Kind, string Title, string? Description, int? Mileage, decimal? LaborHours,
    IReadOnlyList<PartDto>? Parts);

public sealed record NoteDto(Guid Id, Guid? VehicleId, Guid? DiagnosticSessionId, NoteKind Kind, string Title, string Body, bool IsPinned,
    string? AuthorName, DateTime CreatedUtc, DateTime UpdatedUtc)
{
    public static NoteDto From(Note n) => new(n.Id, n.VehicleId, n.DiagnosticSessionId, n.Kind, n.Title, n.Body, n.IsPinned, n.AuthorName, n.CreatedUtc, n.UpdatedUtc);
}

public sealed record NoteRequest(Guid? VehicleId, Guid? SessionId, NoteKind Kind, string? Title, string? Body, bool Pinned);

// ---------------------------------------------------------------- estimates & inspections

public sealed record EstimateDto(
    Guid Id,
    string Number,
    Guid? CustomerId,
    string? CustomerName,
    Guid? VehicleId,
    string? VehicleName,
    Guid? DiagnosticSessionId,
    EstimateStatus Status,
    string? Notes,
    decimal TaxRate,
    DateTime? ValidUntilUtc,
    IReadOnlyList<EstimateLineDto> Lines,
    decimal Subtotal,
    decimal Tax,
    decimal Total,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    public static EstimateDto From(Estimate e) => new(e.Id, e.Number, e.CustomerId, e.Customer?.DisplayName, e.VehicleId, e.Vehicle?.DisplayName,
        e.DiagnosticSessionId, e.Status, e.Notes, e.TaxRate, e.ValidUntilUtc,
        e.Lines.OrderBy(l => l.SortOrder).Select(l => new EstimateLineDto(l.Kind, l.Description, l.PartNumber, l.Quantity, l.UnitPrice, l.Taxable, l.Total)).ToList(),
        e.Subtotal, e.Tax, e.Total, e.CreatedUtc, e.UpdatedUtc);
}

public sealed record EstimateLineDto(EstimateLineKind Kind, string Description, string? PartNumber, decimal Quantity, decimal UnitPrice, bool Taxable, decimal Total);

public sealed record EstimateRequest(Guid? CustomerId, Guid? VehicleId, Guid? SessionId, decimal TaxRate, string? Notes,
    IReadOnlyList<EstimateLineInput>? Lines, EstimateStatus Status = EstimateStatus.Draft);

public sealed record InspectionDto(
    Guid Id,
    Guid? VehicleId,
    string? VehicleName,
    string TemplateName,
    string? TechnicianName,
    int? Mileage,
    InspectionStatus Status,
    DateTime? CompletedUtc,
    string? Summary,
    IReadOnlyList<InspectionItemDto> Items,
    DateTime CreatedUtc)
{
    public static InspectionDto From(Inspection i) => new(i.Id, i.VehicleId, i.Vehicle?.DisplayName, i.TemplateName, i.TechnicianName, i.Mileage,
        i.Status, i.CompletedUtc, i.Summary,
        i.Items.OrderBy(x => x.SortOrder).Select(x => new InspectionItemDto(x.Id, x.Section, x.Name, x.Rating, x.Measurement, x.Notes)).ToList(),
        i.CreatedUtc);
}

public sealed record InspectionItemDto(Guid Id, string Section, string Name, InspectionRating Rating, string? Measurement, string? Notes);

public sealed record StartInspectionRequest(Guid VehicleId, int? Mileage);

public sealed record UpdateInspectionItemRequest(InspectionRating Rating, string? Measurement, string? Notes);
