namespace MechanicAI.Application.Abstractions;

/// <summary>A value with provenance: where it came from, when, and whether it was served from cache.</summary>
public sealed record Sourced<T>(T Value, string SourceName, string? SourceUrl, DateTime RetrievedUtc, bool FromCache);

/// <summary>Decodes a VIN using an authoritative service (NHTSA vPIC).</summary>
public interface IVinDecoder
{
    Task<Sourced<VinDecodeResult>> DecodeAsync(string vin, int? modelYear, CancellationToken cancellationToken);
}

public sealed record DecodedField(string Category, string Name, string Value, string SourceKey);

public sealed record VinDecodeResult
{
    public required string Vin { get; init; }

    public int? ModelYear { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Trim { get; init; }

    public string? Series { get; init; }

    public string? EngineDescription { get; init; }

    public decimal? DisplacementLiters { get; init; }

    public int? Cylinders { get; init; }

    public string? FuelType { get; init; }

    public string? Drivetrain { get; init; }

    public string? Transmission { get; init; }

    public string? BodyClass { get; init; }

    public string? VehicleType { get; init; }

    public string? Manufacturer { get; init; }

    public string? PlantCountry { get; init; }

    /// <summary>vPIC error code ("0" = clean decode). Non-zero codes may still include partial data.</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorText { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<DecodedField> Fields { get; init; } = [];

    /// <summary>True when the decode identified at least make, model, and year.</summary>
    public bool IsUsable => ModelYear is not null && !string.IsNullOrWhiteSpace(Make) && !string.IsNullOrWhiteSpace(Model);
}

public sealed record RecallRecord(
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
    string SourceUrl);

public interface IRecallProvider
{
    Task<Sourced<IReadOnlyList<RecallRecord>>> GetRecallsAsync(string make, string model, int modelYear,
        CancellationToken cancellationToken);
}

public sealed record ComplaintRecord(
    long OdiNumber,
    string Components,
    string Summary,
    DateTime? IncidentDate,
    DateTime? FiledDate,
    bool Crash,
    bool Fire,
    int Injuries,
    int Deaths,
    string? PartialVin);

/// <summary>Owner complaints filed with NHTSA — government-hosted, but unverified owner reports.</summary>
public interface IComplaintProvider
{
    Task<Sourced<IReadOnlyList<ComplaintRecord>>> GetComplaintsAsync(string make, string model, int modelYear,
        CancellationToken cancellationToken);
}

/// <summary>Reference lists of makes and models (NHTSA vPIC) for validation and pickers.</summary>
public interface IVehicleCatalog
{
    Task<Sourced<IReadOnlyList<string>>> GetModelsAsync(string make, int modelYear, CancellationToken cancellationToken);
}
