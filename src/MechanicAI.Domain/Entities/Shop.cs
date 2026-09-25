using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

public class Estimate : Entity, IAggregateRoot, IVehicleScoped
{
    public string Number { get; set; } = string.Empty;

    public Guid? CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? VehicleId { get; set; }

    public Vehicle? Vehicle { get; set; }

    public Guid? DiagnosticSessionId { get; set; }

    public EstimateStatus Status { get; set; } = EstimateStatus.Draft;

    public string? Notes { get; set; }

    /// <summary>Tax rate as a fraction (0.0825 = 8.25%). Applied to taxable lines.</summary>
    public decimal TaxRate { get; set; }

    public DateTime? ValidUntilUtc { get; set; }

    public List<EstimateLine> Lines { get; set; } = [];

    public decimal Subtotal => Lines.Sum(l => l.Total);

    public decimal TaxableTotal => Lines.Where(l => l.Taxable).Sum(l => l.Total);

    public decimal Tax => decimal.Round(TaxableTotal * TaxRate, 2, MidpointRounding.AwayFromZero);

    public decimal Total => Subtotal + Tax;
}

public class EstimateLine : Entity
{
    public Guid EstimateId { get; set; }

    public EstimateLineKind Kind { get; set; } = EstimateLineKind.Part;

    public string Description { get; set; } = string.Empty;

    public string? PartNumber { get; set; }

    /// <summary>Quantity for parts/fees, hours for labor.</summary>
    public decimal Quantity { get; set; } = 1;

    /// <summary>Unit price for parts/fees, hourly rate for labor.</summary>
    public decimal UnitPrice { get; set; }

    public bool Taxable { get; set; } = true;

    public int SortOrder { get; set; }

    public decimal Total => decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Multi-point vehicle inspection.</summary>
public class Inspection : Entity, IAggregateRoot, IVehicleScoped
{
    public Guid? VehicleId { get; set; }

    public Vehicle? Vehicle { get; set; }

    public string TemplateName { get; set; } = string.Empty;

    public string? TechnicianName { get; set; }

    public int? Mileage { get; set; }

    public InspectionStatus Status { get; set; } = InspectionStatus.InProgress;

    public DateTime? CompletedUtc { get; set; }

    public string? Summary { get; set; }

    public List<InspectionItem> Items { get; set; } = [];
}

public class InspectionItem : Entity
{
    public Guid InspectionId { get; set; }

    public string Section { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public InspectionRating Rating { get; set; } = InspectionRating.NotInspected;

    /// <summary>Optional measurement with units as observed (e.g. "6 mm", "4/32 in").</summary>
    public string? Measurement { get; set; }

    public string? Notes { get; set; }

    public int SortOrder { get; set; }
}
