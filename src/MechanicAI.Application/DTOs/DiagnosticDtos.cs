using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.DTOs;

public sealed record DiagnosticSessionSummary(
    Guid Id,
    string Title,
    string VehicleDescription,
    Guid? VehicleId,
    DiagnosticSessionStatus Status,
    IReadOnlyList<string> Dtcs,
    string Complaint,
    DateTime StartedUtc,
    DateTime UpdatedUtc,
    string? LeadingCause,
    double? LeadingProbability,
    int TestsCompleted,
    string? NextTest,
    bool IsSample);

/// <summary>Everything the diagnostic workspace needs to render a session.</summary>
public sealed record DiagnosticSessionView(
    DiagnosticSession Session,
    IReadOnlyList<TestRanking> Ranking,
    DiagnosticNode? LeadingCause,
    bool EvidenceOutsideTree,
    IReadOnlyList<SafetyWarning> SafetyWarnings,
    IReadOnlyList<string> InitialChecks)
{
    public DiagnosticTest? NextTest => Ranking.Count > 0 ? Ranking[0].Test : null;

    public int TestsCompleted => Session.Tests.Count(t => t.Status == TestStatus.Completed);

    public string StageLabel => DiagnosticStages.Label(Session.Status);
}

public static class DiagnosticStages
{
    /// <summary>Standard checks performed before targeted testing, regardless of the fault.</summary>
    public static readonly IReadOnlyList<string> InitialChecks =
    [
        "Verify the customer complaint (reproduce the condition when it is safe to do so).",
        "Scan all modules and record every DTC with its status and freeze-frame data before clearing anything.",
        "Check for open recalls and technical service bulletins for this vehicle.",
        "Visually inspect the affected system: connectors, wiring, hoses, leaks, and signs of previous repairs.",
        "Confirm battery state of charge and charging voltage — low system voltage causes erratic faults.",
    ];

    public static readonly IReadOnlyList<(DiagnosticSessionStatus Status, string Label)> Workflow =
    [
        (DiagnosticSessionStatus.Intake, "Complaint"),
        (DiagnosticSessionStatus.Analysis, "Analysis"),
        (DiagnosticSessionStatus.Testing, "Testing"),
        (DiagnosticSessionStatus.Isolated, "Isolated"),
        (DiagnosticSessionStatus.Repair, "Repair"),
        (DiagnosticSessionStatus.Verification, "Verification"),
        (DiagnosticSessionStatus.Completed, "Complete"),
    ];

    public static string Label(DiagnosticSessionStatus status) => status switch
    {
        DiagnosticSessionStatus.Intake => "Recording complaint",
        DiagnosticSessionStatus.Analysis => "Analyzing — research needed",
        DiagnosticSessionStatus.Testing => "Testing",
        DiagnosticSessionStatus.Isolated => "Fault isolated — confirm diagnosis",
        DiagnosticSessionStatus.Repair => "Repair",
        DiagnosticSessionStatus.Verification => "Verify repair",
        DiagnosticSessionStatus.Completed => "Completed",
        DiagnosticSessionStatus.Abandoned => "Closed without repair",
        _ => status.ToString(),
    };
}
