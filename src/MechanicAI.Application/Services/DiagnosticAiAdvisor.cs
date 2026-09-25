using System.Globalization;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public sealed record DiagnosticAiResult(string Summary, int CausesAdded, int TestsAdded, int PriorsAdjusted, IReadOnlyList<string> Questions,
    IReadOnlyList<SourceCitation> Sources, IReadOnlyList<string> Warnings, string Model);

/// <summary>
/// "AI researches vehicle + DTC + symptoms, builds/refines the diagnostic tree": runs the agent
/// with research tools over the session, then applies its structured proposals to the tree —
/// every addition labeled as AI inference or source-derived, prior changes bounded and logged.
/// </summary>
public sealed class DiagnosticAiAdvisor(
    DiagnosticSessionService sessions,
    AgentRunner agent,
    IAiRouter router,
    ISettingsStore settings,
    ILogger<DiagnosticAiAdvisor> logger,
    IConnectivityMonitor? connectivity = null)
{
    private sealed class Proposal
    {
        public string Summary { get; set; } = string.Empty;

        public List<string> Questions { get; set; } = [];

        public List<PriorAdjustment> PriorAdjustments { get; set; } = [];

        public List<CauseProposal> AdditionalCauses { get; set; } = [];

        public List<TestProposal> AdditionalTests { get; set; } = [];
    }

    private sealed class PriorAdjustment
    {
        public string CauseKey { get; set; } = string.Empty;

        public double Factor { get; set; } = 1;

        public string Reason { get; set; } = string.Empty;
    }

    private sealed class CauseProposal
    {
        public string Title { get; set; } = string.Empty;

        public string Category { get; set; } = "Other";

        public double Likelihood { get; set; } = 0.1;

        public string? Rationale { get; set; }

        public List<string> Sources { get; set; } = [];
    }

    private sealed class TestProposal
    {
        public string Title { get; set; } = string.Empty;

        public string? Purpose { get; set; }

        public List<string> Procedure { get; set; } = [];

        public List<string> Tools { get; set; } = [];

        public string? Expected { get; set; }

        public List<string> Implicates { get; set; } = [];

        public int Minutes { get; set; } = 15;

        public List<string> Sources { get; set; } = [];
    }

    public async Task<Result<DiagnosticAiResult>> AnalyzeAsync(Guid sessionId, Func<AgentEvent, Task>? onEvent, CancellationToken ct = default)
    {
        var view = await sessions.GetViewAsync(sessionId, ct);
        if (view is null) return Error.NotFound("Diagnostic session");
        var route = await router.ResolveChatAsync(AiTask.Diagnostic, DataSensitivity.General, cancellationToken: ct);
        if (!route.IsAvailable) return Error.NotConfigured(route.UnavailableReason!);

        var model = route.Model!;
        var s = settings.Current;
        var privateAllowed = model.IsLocal || (s.Privacy.AllowCloudAi && s.Privacy.AllowDocumentsInCloud);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "search_dtc", "search_recalls", "get_vehicle_history", "decode_vin", "get_diagnostic_session" };
        if (connectivity is not { IsOnline: false }) allowed.Add("search_web");
        if (privateAllowed)
        {
            allowed.Add("search_documents");
            allowed.Add("search_knowledge_base");
        }

        var session = view.Session;
        var brief = new StringBuilder();
        brief.Append("Vehicle: ").Append(session.Vehicle?.Description ?? (string.IsNullOrWhiteSpace(session.VehicleDescription) ? "not specified" : session.VehicleDescription));
        if (session.Mileage is { } miles) brief.Append(CultureInfo.InvariantCulture, $", {miles:N0} miles");
        brief.Append("\nComplaint: ").Append(session.Complaint);
        if (session.Symptoms.Count > 0) brief.Append("\nSymptoms: ").Append(string.Join("; ", session.Symptoms));
        if (session.Conditions.Count > 0) brief.Append("\nConditions: ").Append(string.Join("; ", session.Conditions));
        brief.Append("\nDTCs: ").Append(session.Dtcs.Count == 0 ? "none" : string.Join(", ", session.Dtcs.Select(d => $"{d.Code} ({d.Status})")));
        foreach (var q in session.ClarifyingQuestions.Where(q => q.Answer is not null)) brief.Append($"\nQ: {q.Question} A: {q.Answer}");
        foreach (var obs in session.Steps.Where(x => x.Kind == DiagnosticStepKind.ObservationAdded && !x.IsReverted).TakeLast(10))
        {
            brief.Append("\nTechnician observation: ").Append(obs.Detail ?? obs.Title);
        }

        brief.Append("\n\nCandidate causes (key: title — probability, status):");
        foreach (var c in session.Causes.OrderByDescending(c => c.Probability))
        {
            brief.Append(CultureInfo.InvariantCulture, $"\n- {c.Key}: {c.Title} — {c.Probability:P0}, {c.Status}");
        }

        brief.Append("\n\nCompleted tests:");
        foreach (var t in session.CompletedTests)
        {
            brief.Append($"\n- {t.Title}: {t.Result} — {t.SelectedOutcome?.Label}{(t.ActualResult is null ? string.Empty : $" (observed: {t.ActualResult})")}");
        }

        if (!session.CompletedTests.Any()) brief.Append(" none yet");
        brief.Append("\n\nResearch this vehicle and complaint, then return the JSON object described in your instructions.");

        var registry = new SourceRegistry();
        AgentResult result;
        try
        {
            result = await agent.RunAsync(new AgentRequest
            {
                Model = model,
                SystemPrompt = Prompts.CoreRules + "\n\n" + Prompts.DiagnosticAnalysis,
                UserMessage = ChatMessage.User(brief.ToString()),
                AllowedTools = allowed,
                Context = new ToolContext { Sources = registry, VehicleId = session.VehicleId, SessionId = sessionId, ModelIsLocal = model.IsLocal },
                MaxRounds = Math.Min(6, s.Ai.MaxToolRounds),
                Temperature = 0.1,
            }, onEvent, ct);
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }

        var json = Json.ExtractJson(result.FinalText);
        if (json is null || !Json.TryDeserialize<Proposal>(json, out var proposal) || proposal is null)
        {
            // Fall back to recording the prose analysis only.
            logger.LogInformation("Diagnostic AI returned prose instead of JSON; storing it as a summary");
            await sessions.RecordAiAnalysisAsync(sessionId, result.FinalText, [], ct);
            return new DiagnosticAiResult(result.FinalText, 0, 0, 0, [], registry.Sources, result.Citations.Warnings, $"{result.Provider} · {result.Model}");
        }

        var summaryReport = CitationValidator.Validate(proposal.Summary, registry);
        var warnings = result.Citations.Warnings.Concat(summaryReport.Warnings).ToList();
        await sessions.RecordAiAnalysisAsync(sessionId, summaryReport.CleanedText, proposal.Questions.Take(3).ToList(), ct);

        var validKeys = session.Causes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var adjustments = proposal.PriorAdjustments
            .Where(a => validKeys.Contains(a.CauseKey) && a.Factor > 0 && Math.Abs(a.Factor - 1) > 0.05)
            .Select(a => (a.CauseKey, a.Factor, CitationValidator.Validate(a.Reason, registry).CleanedText))
            .ToList();
        if (adjustments.Count > 0) await sessions.AdjustPriorsAsync(sessionId, adjustments, ct);

        var causesAdded = 0;
        foreach (var cause in proposal.AdditionalCauses.Take(4))
        {
            if (string.IsNullOrWhiteSpace(cause.Title)) continue;
            var cited = cause.Sources.Select(registry.Find).OfType<SourceCitation>().ToList();
            var added = await sessions.AddCauseAsync(new AddCauseCommand
            {
                SessionId = sessionId,
                Title = cause.Title,
                Description = cause.Rationale is null ? null : CitationValidator.Validate(cause.Rationale, registry).CleanedText,
                Category = DiagnosticTreeBuilder.ParseCategory(cause.Category),
                Likelihood = Math.Clamp(cause.Likelihood, 0.02, 0.5),
                Origin = ActorKind.Ai,
                Evidence = cited.Count > 0 ? EvidenceClass.SourceDerived : EvidenceClass.AiInference,
                Sources = cited,
            }, ct);
            if (added.IsSuccess) causesAdded++;
        }

        var refreshed = await sessions.GetViewAsync(sessionId, ct);
        var causeLookup = refreshed?.Session.Causes.ToList() ?? [];
        var testsAdded = 0;
        foreach (var test in proposal.AdditionalTests.Take(4))
        {
            if (string.IsNullOrWhiteSpace(test.Title)) continue;
            var implicated = test.Implicates
                .Select(i => causeLookup.FirstOrDefault(c => c.Key.Equals(i, StringComparison.OrdinalIgnoreCase) ||
                                                             c.Title.Equals(i, StringComparison.OrdinalIgnoreCase))?.Key)
                .OfType<string>()
                .ToList();
            var cited = test.Sources.Select(registry.Find).OfType<SourceCitation>().ToList();
            var added = await sessions.AddTestAsync(new AddTestCommand
            {
                SessionId = sessionId,
                Title = test.Title,
                Purpose = test.Purpose,
                Procedure = test.Procedure,
                Tools = test.Tools,
                ExpectedResult = test.Expected,
                ImplicatesCauseKeys = implicated,
                EstimatedMinutes = test.Minutes <= 0 ? 15 : test.Minutes,
                Origin = ActorKind.Ai,
                Evidence = cited.Count > 0 ? EvidenceClass.SourceDerived : EvidenceClass.AiInference,
                Sources = cited,
            }, ct);
            if (added.IsSuccess) testsAdded++;
        }

        return new DiagnosticAiResult(summaryReport.CleanedText, causesAdded, testsAdded, adjustments.Count, proposal.Questions.Take(3).ToList(),
            registry.Sources, warnings, $"{result.Provider} · {result.Model}");
    }
}
