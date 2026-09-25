using MechanicAI.Application.Commands;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Server.Contracts;

// ---------------------------------------------------------------- diagnostic sessions

/// <summary>A diagnostic session with its full tree (causes + tests), ranking, and audit trail.</summary>
public sealed record DiagnosticSessionDto(
    Guid Id,
    string Title,
    Guid? VehicleId,
    string VehicleDescription,
    DiagnosticSessionStatus Status,
    string StageLabel,
    string Complaint,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<string> Conditions,
    int? Mileage,
    string? TechnicianName,
    string? FinalDiagnosis,
    Guid? ConfirmedCauseNodeId,
    string? RepairPerformed,
    string? VerificationNotes,
    bool? VerificationPassed,
    string? AiSummary,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<ClarifyingQuestionDto> ClarifyingQuestions,
    IReadOnlyList<string> VerificationPlan,
    IReadOnlyList<string> CompletedInitialChecks,
    IReadOnlyList<string> InitialChecks,
    IReadOnlyList<SessionDtcDto> Dtcs,
    IReadOnlyList<DiagnosticNodeDto> Nodes,
    IReadOnlyList<DiagnosticTestDto> Tests,
    IReadOnlyList<TestRankingDto> Ranking,
    Guid? NextTestId,
    Guid? LeadingCauseId,
    bool EvidenceOutsideTree,
    int TestsCompleted,
    IReadOnlyList<SafetyWarning> SafetyWarnings,
    IReadOnlyList<DiagnosticStepDto> Steps,
    bool IsSample)
{
    public static DiagnosticSessionDto From(DiagnosticSessionView view)
    {
        var s = view.Session;
        return new DiagnosticSessionDto(
            s.Id, s.Title, s.VehicleId, s.Vehicle?.Description ?? s.VehicleDescription, s.Status, view.StageLabel, s.Complaint, s.Symptoms, s.Conditions,
            s.Mileage, s.TechnicianName, s.FinalDiagnosis, s.ConfirmedCauseNodeId, s.RepairPerformed, s.VerificationNotes, s.VerificationPassed,
            s.AiSummary, s.StartedUtc, s.CompletedUtc, s.UpdatedUtc,
            s.ClarifyingQuestions.Select(q => new ClarifyingQuestionDto(q.Question, q.Answer, q.AskedBy, q.AnsweredUtc)).ToList(),
            s.VerificationPlan, s.CompletedInitialChecks, view.InitialChecks,
            s.Dtcs.Select(d => new SessionDtcDto(d.Code, d.Description, d.Status, d.Source, d.FreezeFrame, d.RecordedUtc)).ToList(),
            s.Nodes.Select(DiagnosticNodeDto.From).ToList(),
            s.Tests.Select(DiagnosticTestDto.From).ToList(),
            view.Ranking.Select(r => new TestRankingDto(r.Test.Id, r.Test.Title, r.InformationGain, r.Score)).ToList(),
            view.NextTest?.Id,
            view.LeadingCause?.Id,
            view.EvidenceOutsideTree,
            view.TestsCompleted,
            view.SafetyWarnings,
            s.Steps.Select(DiagnosticStepDto.From).ToList(),
            s.IsSample);
    }
}

public sealed record ClarifyingQuestionDto(string Question, string? Answer, ActorKind AskedBy, DateTime? AnsweredUtc);

public sealed record SessionDtcDto(string Code, string? Description, DtcStatus Status, string Source, IReadOnlyDictionary<string, string> FreezeFrame, DateTime RecordedUtc);

public sealed record DiagnosticNodeDto(
    Guid Id,
    Guid? ParentId,
    DiagnosticNodeKind Kind,
    string Key,
    string Title,
    string? Description,
    DiagnosticCategory Category,
    double PriorProbability,
    double Probability,
    CauseStatus Status,
    string? StatusReason,
    bool IsManualStatus,
    EvidenceClass Evidence,
    ActorKind Origin,
    IReadOnlyList<string> SafetyTags,
    IReadOnlyList<SourceCitation> Sources,
    int SortOrder)
{
    public static DiagnosticNodeDto From(DiagnosticNode n) => new(n.Id, n.ParentId, n.Kind, n.Key, n.Title, n.Description, n.Category,
        n.PriorProbability, n.Probability, n.Status, n.StatusReason, n.IsManualStatus, n.Evidence, n.Origin, n.SafetyTags, n.Sources, n.SortOrder);
}

public sealed record DiagnosticTestDto(
    Guid Id,
    string Key,
    string Title,
    string? Purpose,
    IReadOnlyList<string> Procedure,
    IReadOnlyList<string> Tools,
    string? ExpectedResult,
    string? SpecificationNote,
    int EstimatedMinutes,
    int Difficulty,
    int Invasiveness,
    IReadOnlyList<string> SafetyTags,
    IReadOnlyList<string> RelatedCauseKeys,
    IReadOnlyList<TestOutcomeDto> Outcomes,
    TestStatus Status,
    string? SelectedOutcomeKey,
    TestResult? Result,
    string? ActualResult,
    DateTime? PerformedUtc,
    string? PerformedBy,
    int? ExecutionOrder,
    double? Score,
    double? InformationGain,
    Guid? PrimaryNodeId,
    ActorKind Origin,
    EvidenceClass Evidence,
    IReadOnlyList<SourceCitation> Sources)
{
    public static DiagnosticTestDto From(DiagnosticTest t) => new(t.Id, t.Key, t.Title, t.Purpose, t.Procedure, t.Tools, t.ExpectedResult,
        t.SpecificationNote, t.EstimatedMinutes, t.Difficulty, t.Invasiveness, t.SafetyTags, t.RelatedCauseKeys,
        t.Outcomes.Select(o => new TestOutcomeDto(o.Key, o.Label, o.Normal, o.Inconclusive, o.Interpretation, o.Likelihoods)).ToList(),
        t.Status, t.SelectedOutcomeKey, t.Result, t.ActualResult, t.PerformedUtc, t.PerformedBy, t.ExecutionOrder, t.Score, t.InformationGain,
        t.PrimaryNodeId, t.Origin, t.Evidence, t.Sources);
}

public sealed record TestOutcomeDto(string Key, string Label, bool? Normal, bool Inconclusive, string? Interpretation, IReadOnlyDictionary<string, double> Likelihoods);

public sealed record TestRankingDto(Guid TestId, string Title, double InformationGain, double Score);

public sealed record DiagnosticStepDto(
    Guid Id,
    int Sequence,
    DateTime TimestampUtc,
    DiagnosticStepKind Kind,
    ActorKind Actor,
    string? ActorName,
    string Title,
    string? Detail,
    EvidenceClass? Evidence,
    Guid? NodeId,
    Guid? TestId,
    bool IsReverted)
{
    public static DiagnosticStepDto From(DiagnosticStep s) => new(s.Id, s.Sequence, s.TimestampUtc, s.Kind, s.Actor, s.ActorName, s.Title, s.Detail,
        s.Evidence, s.NodeId, s.TestId, s.IsReverted);
}

public sealed record StartSessionRequest(
    Guid? VehicleId,
    string? VehicleDescription,
    string? Complaint,
    IReadOnlyList<string>? Symptoms,
    IReadOnlyList<string>? Dtcs,
    IReadOnlyList<string>? Conditions,
    int? Mileage)
{
    public StartDiagnosticSessionCommand ToCommand(string? technician) => new()
    {
        VehicleId = VehicleId,
        VehicleDescription = VehicleDescription,
        Complaint = Complaint ?? string.Empty,
        Symptoms = Symptoms ?? [],
        Dtcs = Dtcs ?? [],
        Conditions = Conditions ?? [],
        Mileage = Mileage,
        TechnicianName = technician,
    };
}

public sealed record StartSessionFromTextRequest(string Text, Guid? VehicleId);

public sealed record RecordTestResultRequest(string OutcomeKey, string? ActualResult);

public sealed record ReasonRequest(string? Reason);

public sealed record ObservationRequest(string Observation);

public sealed record AddSessionDtcRequest(string Code, DtcStatus Status = DtcStatus.Current);

public sealed record CauseStatusRequest(CauseStatus Status, string? Reason);

public sealed record ConfirmDiagnosisRequest(Guid NodeId, string? Notes);

public sealed record RecordRepairRequest(string Description, IReadOnlyList<PartInput>? Parts, decimal? LaborHours);

public sealed record RecordVerificationRequest(bool Passed, string? Notes);

// ---------------------------------------------------------------- DTC reference

public sealed record DtcDefinitionDto(
    string Code,
    string? Manufacturer,
    DtcSystem System,
    string Subsystem,
    string Description,
    bool IsGeneric,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<string> Causes,
    IReadOnlyList<string> RelatedCodes,
    IReadOnlyList<string> SafetyTags,
    string? Notes,
    string Source,
    bool IsUserDefined)
{
    public static DtcDefinitionDto From(DtcDefinition d) => new(d.Code, d.Manufacturer, d.System, d.Subsystem, d.Description, d.IsGeneric,
        d.Symptoms, d.Causes, d.RelatedCodes, d.SafetyTags, d.Notes, d.Source, d.IsUserDefined);
}

public sealed record DtcDetailDto(
    string Code,
    DtcSystem System,
    bool IsGeneric,
    string SubsystemFromCode,
    bool IsKnown,
    IReadOnlyList<DtcDefinitionDto> Definitions,
    IReadOnlyList<PlaybookSummary> Playbooks,
    IReadOnlyList<DtcDefinitionDto> Related,
    IReadOnlyList<SafetyWarning> Safety,
    int TimesSeenInSessions)
{
    public static DtcDetailDto From(DtcDetail d) => new(d.Code, d.System, d.IsGeneric, d.SubsystemFromCode, d.IsKnown,
        d.Definitions.Select(DtcDefinitionDto.From).ToList(), d.Playbooks, d.Related.Select(DtcDefinitionDto.From).ToList(), d.Safety, d.TimesSeenInSessions);
}

public sealed record DtcDefinitionRequest(string? Manufacturer, string Description, string Source, string? Notes);

// ---------------------------------------------------------------- knowledge base

public sealed record DocumentDto(
    Guid Id,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DocumentKind Kind,
    string? Description,
    Guid? VehicleId,
    string? Make,
    string? Model,
    int? YearFrom,
    int? YearTo,
    int PageCount,
    int ChunkCount,
    DocumentStatus Status,
    string? StatusMessage,
    DateTime? IndexedUtc,
    string? EmbeddingModel,
    bool UsedOcr,
    IReadOnlyList<string> Tags,
    DateTime CreatedUtc,
    DateTime UpdatedUtc)
{
    public static DocumentDto From(Document d) => new(d.Id, d.Title, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.Kind, d.Description,
        d.VehicleId, d.Make, d.Model, d.YearFrom, d.YearTo, d.PageCount, d.ChunkCount, d.Status, d.StatusMessage, d.IndexedUtc, d.EmbeddingModel,
        d.UsedOcr, d.Tags, d.CreatedUtc, d.UpdatedUtc);
}

public sealed record DocumentMetadataRequest(string? Title, DocumentKind Kind, Guid? VehicleId, string? Make, string? Model, int? YearFrom, int? YearTo,
    IReadOnlyList<string>? Tags, string? Description)
{
    public DocumentUploadOptions ToOptions() => new()
    {
        Title = Title,
        Kind = Kind,
        VehicleId = VehicleId,
        Make = Make,
        Model = Model,
        YearFrom = YearFrom,
        YearTo = YearTo,
        Tags = Tags ?? [],
        Description = Description,
    };
}

public sealed record AskDocumentsRequest(
    string Question,
    DocumentQuestionTemplate Template = DocumentQuestionTemplate.Free,
    DocumentKind? Kind = null,
    Guid? DocumentId = null,
    int? VehicleYear = null,
    string? VehicleMake = null,
    string? VehicleModel = null);

public sealed record KnowledgeStatsDto(int Documents, int Ready, int Chunks, int Embedded);

// ---------------------------------------------------------------- training

public sealed record CourseDto(
    Guid Id,
    string Key,
    TrainingCategory Category,
    string Title,
    string Summary,
    TrainingLevel Level,
    IReadOnlyList<LessonSummaryDto> Lessons,
    IReadOnlyList<QuizDto> Quizzes,
    int Flashcards)
{
    public static CourseDto From(TrainingCourse c) => new(c.Id, c.Key, c.Category, c.Title, c.Summary, c.Level,
        c.Lessons.OrderBy(l => l.SortOrder).Select(l => new LessonSummaryDto(l.Id, l.Key, l.Title, l.EstimatedMinutes, l.CompletedUtc)).ToList(),
        c.Quizzes.Select(QuizDto.From).ToList(), c.Flashcards.Count);
}

public sealed record LessonSummaryDto(Guid Id, string Key, string Title, int EstimatedMinutes, DateTime? CompletedUtc);

public sealed record LessonDto(
    Guid Id,
    Guid CourseId,
    string CourseTitle,
    string Key,
    string Title,
    string BodyMarkdown,
    string? DiagramSvg,
    int EstimatedMinutes,
    DateTime? CompletedUtc,
    IReadOnlyList<SafetyWarning> Safety,
    Guid? PreviousLessonId,
    Guid? NextLessonId)
{
    public static LessonDto From(LessonView v) => new(v.Lesson.Id, v.Course.Id, v.Course.Title, v.Lesson.Key, v.Lesson.Title, v.Lesson.BodyMarkdown,
        v.DiagramSvg, v.Lesson.EstimatedMinutes, v.Lesson.CompletedUtc, v.Safety, v.PreviousLessonId, v.NextLessonId);
}

/// <summary>A quiz without its answer key (answers are graded server-side).</summary>
public sealed record QuizDto(Guid Id, string Key, string Title, bool IsAiGenerated, IReadOnlyList<QuizQuestionDto> Questions)
{
    public static QuizDto From(TrainingQuiz q) => new(q.Id, q.Key, q.Title, q.IsAiGenerated,
        q.Questions.Select((x, i) => new QuizQuestionDto(i, x.Question, x.Choices)).ToList());
}

public sealed record QuizQuestionDto(int Index, string Question, IReadOnlyList<string> Choices);

public sealed record SubmitQuizRequest(IReadOnlyList<int> Answers);

public sealed record FlashcardDto(Guid Id, Guid CourseId, string Front, string Back, int Box, DateTime DueUtc)
{
    public static FlashcardDto From(Flashcard f) => new(f.Id, f.CourseId, f.Front, f.Back, f.Box, f.DueUtc);
}

public sealed record FlashcardReviewRequest(bool Correct);

public sealed record LessonCompletionRequest(bool Completed = true);

public sealed record TrainingAttemptDto(Guid Id, TrainingAttemptKind Kind, string ReferenceKey, string Title, string? TraineeName,
    DateTime StartedUtc, DateTime? CompletedUtc, double Score, double MaxScore, bool? Passed)
{
    public static TrainingAttemptDto From(TrainingAttempt a) => new(a.Id, a.Kind, a.ReferenceKey, a.Title, a.TraineeName, a.StartedUtc, a.CompletedUtc,
        a.Score, a.MaxScore, a.Passed);
}
