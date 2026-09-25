using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Domain.Entities;

/// <summary>
/// A structured diagnostic workflow for one complaint on one vehicle:
/// complaint → symptoms → DTCs → possible causes → tests → results → isolation →
/// repair → verification. Every change is recorded as a timestamped <see cref="DiagnosticStep"/>.
/// </summary>
public class DiagnosticSession : Entity, IAggregateRoot, ISampleData, IVehicleScoped
{
    public Guid? VehicleId { get; set; }

    public Vehicle? Vehicle { get; set; }

    /// <summary>Snapshot of the vehicle description at session start (also used when no vehicle record exists).</summary>
    public string VehicleDescription { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Complaint { get; set; } = string.Empty;

    public List<string> Symptoms { get; set; } = [];

    /// <summary>Operating conditions when the fault occurs (cold, hot, idle, under load, rain...).</summary>
    public List<string> Conditions { get; set; } = [];

    public int? Mileage { get; set; }

    public string? TechnicianName { get; set; }

    public Guid? TechnicianId { get; set; }

    public DiagnosticSessionStatus Status { get; set; } = DiagnosticSessionStatus.Intake;

    public string? FinalDiagnosis { get; set; }

    public Guid? ConfirmedCauseNodeId { get; set; }

    public string? RepairPerformed { get; set; }

    public string? VerificationNotes { get; set; }

    public bool? VerificationPassed { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    /// <summary>Latest AI analysis summary (always labeled as AI inference in the UI).</summary>
    public string? AiSummary { get; set; }

    /// <summary>Keys of the playbooks used to build the tree.</summary>
    public List<string> PlaybookKeys { get; set; } = [];

    public List<ClarifyingQuestion> ClarifyingQuestions { get; set; } = [];

    /// <summary>Steps to verify the repair (from the matched playbooks, editable by the technician).</summary>
    public List<string> VerificationPlan { get; set; } = [];

    /// <summary>Standard initial checks the technician has ticked off (verify complaint, scan all modules...).</summary>
    public List<string> CompletedInitialChecks { get; set; } = [];

    public bool IsSample { get; set; }

    public List<SessionDtc> Dtcs { get; set; } = [];

    public List<DiagnosticNode> Nodes { get; set; } = [];

    public List<DiagnosticTest> Tests { get; set; } = [];

    public List<DiagnosticStep> Steps { get; set; } = [];

    public bool IsClosed => Status is DiagnosticSessionStatus.Completed or DiagnosticSessionStatus.Abandoned;

    /// <summary>Appends a timestamped entry to the session's audit trail.</summary>
    public DiagnosticStep AddStep(
        DiagnosticStepKind kind,
        ActorKind actor,
        string title,
        string? detail = null,
        EvidenceClass? evidence = null,
        Guid? nodeId = null,
        Guid? testId = null,
        string? actorName = null,
        string? dataJson = null)
    {
        var step = new DiagnosticStep
        {
            SessionId = Id,
            Sequence = Steps.Count == 0 ? 1 : Steps.Max(s => s.Sequence) + 1,
            TimestampUtc = DateTime.UtcNow,
            Kind = kind,
            Actor = actor,
            ActorName = actorName,
            Title = title,
            Detail = detail,
            Evidence = evidence,
            NodeId = nodeId,
            TestId = testId,
            DataJson = dataJson,
        };
        Steps.Add(step);
        Touch();
        return step;
    }

    public IEnumerable<DiagnosticNode> Causes => Nodes.Where(n => n.Kind == DiagnosticNodeKind.Cause);

    /// <summary>Tests with a recorded, non-reverted result, in the order they were performed.</summary>
    public IEnumerable<DiagnosticTest> CompletedTests =>
        Tests.Where(t => t.Status == TestStatus.Completed).OrderBy(t => t.ExecutionOrder ?? int.MaxValue);
}

public class ClarifyingQuestion
{
    public string Question { get; set; } = string.Empty;

    public string? Answer { get; set; }

    public ActorKind AskedBy { get; set; } = ActorKind.System;

    public DateTime? AnsweredUtc { get; set; }
}

/// <summary>A trouble code recorded for a session (entered manually or read from the vehicle).</summary>
public class SessionDtc : Entity
{
    public Guid SessionId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DtcStatus Status { get; set; } = DtcStatus.Current;

    /// <summary>"Technician entry", "OBD-II scan", etc.</summary>
    public string Source { get; set; } = "Technician entry";

    public Dictionary<string, string> FreezeFrame { get; set; } = [];

    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A node in the diagnostic tree: the root (the complaint), a system category
/// (Ignition, Fuel...), or a candidate cause with a probability that is updated
/// by Bayes' rule as test results come in.
/// </summary>
public class DiagnosticNode : Entity
{
    public Guid SessionId { get; set; }

    public Guid? ParentId { get; set; }

    public DiagnosticNodeKind Kind { get; set; }

    /// <summary>Stable key (cause key such as "ignition-coil", or category key).</summary>
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DiagnosticCategory Category { get; set; } = DiagnosticCategory.Other;

    /// <summary>Prior probability after symptom/DTC/mileage modifiers, before any test results.</summary>
    public double PriorProbability { get; set; }

    /// <summary>Current posterior probability given all non-reverted test results.</summary>
    public double Probability { get; set; }

    public CauseStatus Status { get; set; } = CauseStatus.Open;

    public string? StatusReason { get; set; }

    /// <summary>
    /// True when the technician set the status by hand (e.g. ruled a cause out from direct
    /// inspection). Manual statuses are never overwritten by recalculation.
    /// </summary>
    public bool IsManualStatus { get; set; }

    public EvidenceClass Evidence { get; set; } = EvidenceClass.UnconfirmedPossibility;

    /// <summary>Who proposed this cause: the playbook engine (System), the AI, or the technician.</summary>
    public ActorKind Origin { get; set; } = ActorKind.System;

    public string? OriginDetail { get; set; }

    public List<string> SafetyTags { get; set; } = [];

    public List<SourceCitation> Sources { get; set; } = [];

    public int SortOrder { get; set; }
}

/// <summary>
/// A diagnostic test with its procedure, expected result, possible outcomes (with
/// likelihoods per cause), and — once performed — the technician's actual result.
/// </summary>
public class DiagnosticTest : Entity
{
    public Guid SessionId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Purpose { get; set; }

    public List<string> Procedure { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public string? ExpectedResult { get; set; }

    /// <summary>Which OEM specification the technician must look up, if any.</summary>
    public string? SpecificationNote { get; set; }

    public int EstimatedMinutes { get; set; } = 15;

    /// <summary>1 (easy) to 5 (expert).</summary>
    public int Difficulty { get; set; } = 1;

    /// <summary>1 (non-invasive) to 5 (major disassembly).</summary>
    public int Invasiveness { get; set; } = 1;

    public List<string> SafetyTags { get; set; } = [];

    /// <summary>Cause keys this test discriminates.</summary>
    public List<string> RelatedCauseKeys { get; set; } = [];

    public List<TestOutcomeDefinition> Outcomes { get; set; } = [];

    public TestStatus Status { get; set; } = TestStatus.Available;

    public string? SelectedOutcomeKey { get; set; }

    public TestResult? Result { get; set; }

    /// <summary>What the technician actually observed or measured, in their words.</summary>
    public string? ActualResult { get; set; }

    public DateTime? PerformedUtc { get; set; }

    public string? PerformedBy { get; set; }

    /// <summary>Order in which results were recorded (1 = first test performed).</summary>
    public int? ExecutionOrder { get; set; }

    /// <summary>Last computed recommendation score (expected information gain per unit cost).</summary>
    public double? Score { get; set; }

    /// <summary>Expected information gain in bits at the last recommendation pass.</summary>
    public double? InformationGain { get; set; }

    /// <summary>The cause node this test is displayed under in the tree.</summary>
    public Guid? PrimaryNodeId { get; set; }

    public ActorKind Origin { get; set; } = ActorKind.System;

    public string? OriginDetail { get; set; }

    public EvidenceClass Evidence { get; set; } = EvidenceClass.SourceDerived;

    public List<SourceCitation> Sources { get; set; } = [];

    public TestOutcomeDefinition? SelectedOutcome =>
        SelectedOutcomeKey is null ? null : Outcomes.FirstOrDefault(o => o.Key == SelectedOutcomeKey);
}

/// <summary>
/// One possible outcome of a test. <see cref="Likelihoods"/> holds P(outcome | cause)
/// keyed by cause key, with "*" as the default for unlisted causes.
/// </summary>
public class TestOutcomeDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>True = within expected (PASS), false = abnormal (FAIL), null = not applicable.</summary>
    public bool? Normal { get; set; }

    public bool Inconclusive { get; set; }

    public Dictionary<string, double> Likelihoods { get; set; } = [];

    public string? Interpretation { get; set; }

    public TestResult ToResult() => Inconclusive ? TestResult.Inconclusive : Normal == false ? TestResult.Fail : TestResult.Pass;
}

/// <summary>Timestamped, append-only record of everything that happened in a session.</summary>
public class DiagnosticStep : Entity
{
    public Guid SessionId { get; set; }

    public int Sequence { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    public DiagnosticStepKind Kind { get; set; }

    public ActorKind Actor { get; set; }

    public string? ActorName { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public EvidenceClass? Evidence { get; set; }

    public Guid? NodeId { get; set; }

    public Guid? TestId { get; set; }

    public bool IsReverted { get; set; }

    public DateTime? RevertedUtc { get; set; }

    public string? DataJson { get; set; }
}
