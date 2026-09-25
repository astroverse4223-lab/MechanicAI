using System.Globalization;
using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

public class Vehicle : Entity, IAggregateRoot, ISampleData
{
    public string? Vin { get; set; }

    public int? Year { get; set; }

    public string Make { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Trim { get; set; }

    /// <summary>Human-readable engine description, e.g. "3.5L V6 GTDI (EcoBoost)".</summary>
    public string? Engine { get; set; }

    public decimal? DisplacementLiters { get; set; }

    public int? Cylinders { get; set; }

    public string? FuelType { get; set; }

    public string? Drivetrain { get; set; }

    public string? Transmission { get; set; }

    public string? BodyClass { get; set; }

    public int? Mileage { get; set; }

    public DistanceUnit MileageUnit { get; set; } = DistanceUnit.Miles;

    public string? Color { get; set; }

    public string? LicensePlate { get; set; }

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public string? Notes { get; set; }

    public VehicleDataSource DataSource { get; set; } = VehicleDataSource.Manual;

    /// <summary>When the identity fields were last confirmed by a VIN decode.</summary>
    public DateTime? DecodedUtc { get; set; }

    public DateTime? LastAccessedUtc { get; set; }

    public DateTime? RecallsCheckedUtc { get; set; }

    public bool IsFavorite { get; set; }

    public bool IsSample { get; set; }

    public List<VehicleSpecification> Specifications { get; set; } = [];

    public List<Recall> Recalls { get; set; } = [];

    public string DisplayName
    {
        get
        {
            var parts = new List<string>(4);
            if (Year is { } y) parts.Add(y.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(Make)) parts.Add(Make);
            if (!string.IsNullOrWhiteSpace(Model)) parts.Add(Model);
            if (!string.IsNullOrWhiteSpace(Trim)) parts.Add(Trim);
            return parts.Count == 0 ? "Unidentified vehicle" : string.Join(' ', parts);
        }
    }

    /// <summary>Short description including engine, used in AI prompts and reports.</summary>
    public string Description
    {
        get
        {
            var engine = Engine;
            if (string.IsNullOrWhiteSpace(engine) && DisplacementLiters is { } d)
            {
                engine = string.Create(CultureInfo.InvariantCulture, $"{d:0.0}L");
            }

            return string.IsNullOrWhiteSpace(engine) ? DisplayName : $"{DisplayName} {engine}";
        }
    }
}

/// <summary>
/// A single specification value with provenance. Values decoded from NHTSA are
/// <see cref="VerificationLevel.Verified"/>; manual entries are technician-entered.
/// </summary>
public class VehicleSpecification : Entity
{
    public Guid VehicleId { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    /// <summary>Upstream variable name (e.g. vPIC "DisplacementL") for traceability.</summary>
    public string? SourceKey { get; set; }

    public DateTime RetrievedUtc { get; set; } = DateTime.UtcNow;

    public VerificationLevel Verification { get; set; } = VerificationLevel.Unverified;
}

/// <summary>A safety recall campaign as published by NHTSA for the vehicle's year/make/model.</summary>
public class Recall : Entity
{
    public Guid VehicleId { get; set; }

    public string CampaignNumber { get; set; } = string.Empty;

    public string? Manufacturer { get; set; }

    public string? Component { get; set; }

    public string? Summary { get; set; }

    public string? Consequence { get; set; }

    public string? Remedy { get; set; }

    public string? Notes { get; set; }

    public DateTime? ReportReceivedDate { get; set; }

    public bool ParkIt { get; set; }

    public bool ParkOutside { get; set; }

    public bool OverTheAirUpdate { get; set; }

    public DateTime RetrievedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Completion status tracked by the technician. NHTSA data does not include VIN-level completion.</summary>
    public RecallStatus Status { get; set; } = RecallStatus.Unknown;

    public string? SourceUrl { get; set; }
}
