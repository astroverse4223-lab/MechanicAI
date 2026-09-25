using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;

namespace MechanicAI.Domain.Entities;

/// <summary>A repair, maintenance item, inspection, or recall remedy performed on a vehicle.</summary>
public class Repair : Entity, IAggregateRoot, ISampleData, IVehicleScoped
{
    public Guid? VehicleId { get; set; }

    public Vehicle? Vehicle { get; set; }

    public Guid? DiagnosticSessionId { get; set; }

    public RepairKind Kind { get; set; } = RepairKind.Repair;

    public RepairStatus Status { get; set; } = RepairStatus.Completed;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime PerformedUtc { get; set; } = DateTime.UtcNow;

    public int? Mileage { get; set; }

    public string? TechnicianName { get; set; }

    public decimal? LaborHours { get; set; }

    public string? VerificationNotes { get; set; }

    public bool IsSample { get; set; }

    public List<Part> Parts { get; set; } = [];
}

public class Part : Entity
{
    public Guid? RepairId { get; set; }

    public string? PartNumber { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? Brand { get; set; }

    public decimal Quantity { get; set; } = 1;

    public decimal? UnitCost { get; set; }

    public decimal? UnitPrice { get; set; }

    public string? Supplier { get; set; }

    public string? Notes { get; set; }
}

public class Note : Entity, ISampleData, IVehicleScoped
{
    public Guid? VehicleId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? DiagnosticSessionId { get; set; }

    public NoteKind Kind { get; set; } = NoteKind.General;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    public bool IsPinned { get; set; }

    public string? AuthorName { get; set; }

    public bool IsSample { get; set; }
}

/// <summary>A photo or file attached to a vehicle, session, or repair. Stored on disk, indexed here.</summary>
public class MediaAttachment : Entity, IVehicleScoped
{
    public Guid? VehicleId { get; set; }

    public Guid? DiagnosticSessionId { get; set; }

    public Guid? RepairId { get; set; }

    public Guid? InspectionId { get; set; }

    public AttachmentKind Kind { get; set; } = AttachmentKind.Photo;

    public string FileName { get; set; } = string.Empty;

    /// <summary>Path relative to the application's media directory.</summary>
    public string StoredFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string? Caption { get; set; }

    public DateTime? TakenUtc { get; set; }

    /// <summary>Serialized image-analysis result (AI inference, always labeled as such).</summary>
    public string? AnalysisJson { get; set; }

    public DateTime? AnalyzedUtc { get; set; }
}
