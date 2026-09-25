using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Commands;

public sealed record StartDiagnosticSessionCommand
{
    public Guid? VehicleId { get; init; }

    /// <summary>Used when the vehicle is not saved (e.g. "2017 Chevrolet Silverado 5.3L").</summary>
    public string? VehicleDescription { get; init; }

    public string Complaint { get; init; } = string.Empty;

    public IReadOnlyList<string> Symptoms { get; init; } = [];

    public IReadOnlyList<string> Dtcs { get; init; } = [];

    public IReadOnlyList<string> Conditions { get; init; } = [];

    public int? Mileage { get; init; }

    public string? TechnicianName { get; init; }
}

public sealed record RecordTestResultCommand(Guid SessionId, Guid TestId, string OutcomeKey, string? ActualResult, string? TechnicianName);

public sealed record PartInput(string Description, string? PartNumber, decimal Quantity, decimal? UnitPrice, string? Brand = null);

public sealed record RecordRepairCommand(Guid SessionId, string Description, IReadOnlyList<PartInput> Parts, decimal? LaborHours, string? TechnicianName);

public sealed record RecordVerificationCommand(Guid SessionId, bool Passed, string Notes, string? TechnicianName);

public sealed record AddCauseCommand
{
    public Guid SessionId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    public DiagnosticCategory Category { get; init; } = DiagnosticCategory.Other;

    /// <summary>Relative prior likelihood (0.01–1). The engine normalizes across causes.</summary>
    public double Likelihood { get; init; } = 0.1;

    public ActorKind Origin { get; init; } = ActorKind.Technician;

    public EvidenceClass Evidence { get; init; } = EvidenceClass.TechnicianObservation;

    public IReadOnlyList<SourceCitation> Sources { get; init; } = [];

    public string? Key { get; init; }
}

public sealed record AddTestCommand
{
    public Guid SessionId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Purpose { get; init; }

    public IReadOnlyList<string> Procedure { get; init; } = [];

    public IReadOnlyList<string> Tools { get; init; } = [];

    public string? ExpectedResult { get; init; }

    /// <summary>Causes an ABNORMAL result would implicate.</summary>
    public IReadOnlyList<string> ImplicatesCauseKeys { get; init; } = [];

    public int EstimatedMinutes { get; init; } = 15;

    public int Difficulty { get; init; } = 2;

    public int Invasiveness { get; init; } = 1;

    public IReadOnlyList<string> SafetyTags { get; init; } = [];

    public ActorKind Origin { get; init; } = ActorKind.Technician;

    public EvidenceClass Evidence { get; init; } = EvidenceClass.TechnicianObservation;

    public IReadOnlyList<SourceCitation> Sources { get; init; } = [];

    public string? Key { get; init; }
}
