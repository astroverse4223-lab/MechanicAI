using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.DTOs;

public sealed record VehicleSummary(
    Guid Id,
    string DisplayName,
    string? Engine,
    string? Vin,
    int? Mileage,
    DistanceUnit MileageUnit,
    string? CustomerName,
    DateTime? LastAccessedUtc,
    int OpenRecalls,
    int SessionCount,
    bool IsSample,
    bool IsFavorite,
    VehicleDataSource DataSource);

public sealed record VehicleInput
{
    public Guid? Id { get; init; }

    public string? Vin { get; init; }

    public int? Year { get; init; }

    public string Make { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string? Trim { get; init; }

    public string? Engine { get; init; }

    public decimal? DisplacementLiters { get; init; }

    public int? Cylinders { get; init; }

    public string? FuelType { get; init; }

    public string? Drivetrain { get; init; }

    public string? Transmission { get; init; }

    public string? BodyClass { get; init; }

    public int? Mileage { get; init; }

    public DistanceUnit MileageUnit { get; init; } = DistanceUnit.Miles;

    public string? Color { get; init; }

    public string? LicensePlate { get; init; }

    public Guid? CustomerId { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Result of looking up a VIN: local structural checks plus the authoritative decode.</summary>
public sealed record VinLookup(
    string Vin,
    bool CheckDigitValid,
    bool CheckDigitRequired,
    int? EstimatedModelYear,
    string Region,
    Sourced<VinDecodeResult>? Decode,
    string? DecodeError)
{
    public bool IsVerified => Decode is { Value.IsUsable: true } && DecodeError is null;
}

public sealed record VehicleHistoryItem(
    DateTime WhenUtc,
    string Kind,
    string Title,
    string? Detail,
    Guid? ReferenceId,
    string? Code,
    EvidenceClass? Evidence);
