using System.Text.Json;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Ai.Tools;

public sealed class SearchDtcTool(DtcService dtcs) : IAiTool
{
    public string Name => "search_dtc";

    public string Description => "Look up a diagnostic trouble code (definition, subsystem, common causes, built-in diagnostic playbook) or search codes by description. Generic SAE definitions only; manufacturer-specific codes are flagged as such.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new
        {
            code = new { type = "string", description = "A DTC such as P0302 or U0100" },
            query = new { type = "string", description = "Text search when no code is known, e.g. 'misfire cylinder 2'" },
            make = new { type = "string", description = "Vehicle make, to prefer manufacturer-specific definitions if present" },
        },
        required = Array.Empty<string>(),
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var code = ToolArgs.String(arguments, "code");
        var make = ToolArgs.String(arguments, "make");
        if (!string.IsNullOrWhiteSpace(code))
        {
            var detail = await dtcs.GetDetailAsync(code, make, cancellationToken);
            if (detail is null) return ToolExecutionResult.Fail($"'{code}' is not a valid trouble code format.");
            var primary = detail.Primary;
            var source = context.Sources.Register(new SourceCitation
            {
                Title = primary is null ? $"DTC {detail.Code} (code format only)" : $"DTC {detail.Code}: {primary.Description}",
                Type = SourceType.BuiltInReference,
                Publisher = primary?.Source ?? "SAE J2012 code structure",
            });
            return ToolExecutionResult.Ok(new
            {
                source = source.Label,
                code = detail.Code,
                known = detail.IsKnown,
                generic = detail.IsGeneric,
                system = detail.System.ToString(),
                subsystem = primary?.Subsystem ?? detail.SubsystemFromCode,
                description = primary?.Description ?? (detail.IsGeneric
                    ? "Generic code not in the built-in reference — verify its definition in service information."
                    : "Manufacturer-specific code: its meaning varies by manufacturer. Look it up in OEM service information for this vehicle."),
                commonSymptoms = primary?.Symptoms ?? [],
                commonCauses = primary?.Causes ?? [],
                causesNote = "Common causes are general possibilities, not confirmed for this vehicle. Test before replacing.",
                notes = primary?.Notes,
                relatedCodes = detail.Related.Select(r => new { r.Code, r.Description }),
                playbooks = detail.Playbooks.Select(p => new
                {
                    p.Title,
                    p.Summary,
                    causesByLikelihood = p.Causes.Take(8).Select(c => new { c.Title, category = c.Category.ToString(), relativeLikelihood = Math.Round(c.RelativeLikelihood, 2) }),
                    firstTests = p.Tests.Take(6).Select(t => new { t.Title, t.Minutes }),
                }),
                safety = detail.Safety.Select(s => s.Title),
                timesSeenInShop = detail.TimesSeenInSessions,
            }, primary?.Description ?? $"{detail.Code} (not in reference)");
        }

        var query = ToolArgs.String(arguments, "query");
        if (string.IsNullOrWhiteSpace(query)) return ToolExecutionResult.Fail("Provide either 'code' or 'query'.");
        var results = await dtcs.SearchAsync(query, 10, cancellationToken);
        return ToolExecutionResult.Ok(new
        {
            count = results.Count,
            results = results.Select(r => new { r.Code, r.Description, r.Subsystem, generic = r.IsGeneric, hasPlaybook = r.HasPlaybook }),
        }, $"{results.Count} matching code(s)");
    }
}

public sealed class GetDiagnosticSessionTool(DiagnosticSessionService sessions) : IAiTool
{
    public string Name => "get_diagnostic_session";

    public string Description => "Get the current state of a diagnostic session: complaint, DTCs, candidate causes with probabilities and statuses, completed test results, and the recommended next test.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { session_id = new { type = "string" } },
        required = Array.Empty<string>(),
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var id = ToolArgs.Guid(arguments, "session_id") ?? context.SessionId;
        if (id is null) return ToolExecutionResult.Fail("No diagnostic session is active.");
        var view = await sessions.GetViewAsync(id.Value, cancellationToken);
        if (view is null) return ToolExecutionResult.Fail("Session not found.");
        var s = view.Session;
        var source = context.Sources.Register(new SourceCitation { Title = $"Diagnostic session: {s.Title}", Type = SourceType.PrivateDocument, Publisher = "This workstation" });
        return ToolExecutionResult.Ok(new
        {
            source = source.Label,
            vehicle = s.Vehicle?.Description ?? s.VehicleDescription,
            complaint = s.Complaint,
            symptoms = s.Symptoms,
            conditions = s.Conditions,
            dtcs = s.Dtcs.Select(d => new { d.Code, d.Description, status = d.Status.ToString() }),
            stage = view.StageLabel,
            causes = s.Causes.OrderByDescending(c => c.Probability).Select(c => new
            {
                key = c.Key,
                title = c.Title,
                category = c.Category.ToString(),
                probability = Math.Round(c.Probability, 3),
                status = c.Status.ToString(),
                origin = c.Origin.ToString(),
            }),
            completedTests = s.Tests.Where(t => t.Status == TestStatus.Completed).OrderBy(t => t.ExecutionOrder).Select(t => new
            {
                t.Title,
                result = t.Result?.ToString(),
                outcome = t.SelectedOutcome?.Label,
                observed = t.ActualResult,
            }),
            nextTest = view.NextTest is { } n ? new { n.Title, n.Purpose, n.Procedure, n.Tools, expected = n.ExpectedResult, minutes = n.EstimatedMinutes } : null,
            evidenceOutsideTree = view.EvidenceOutsideTree,
            clarifyingQuestions = s.ClarifyingQuestions.Where(q => q.Answer is null).Select(q => q.Question),
            answered = s.ClarifyingQuestions.Where(q => q.Answer is not null).Select(q => new { q.Question, q.Answer }),
        }, $"{s.Title} — {view.StageLabel}");
    }
}

/// <summary>Lets the AI record into the active session. Everything it adds is labeled as AI inference.</summary>
public sealed class SaveDiagnosticStepTool(DiagnosticSessionService sessions) : IAiTool
{
    public string Name => "save_diagnostic_step";

    public string Description => "Record something in the active diagnostic session: a note/observation summary, a new possible cause, or a new test. Items you add are labeled as AI inference. Only add causes/tests you can justify; cite source labels in 'sources'.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new
        {
            session_id = new { type = "string" },
            kind = new { type = "string", @enum = new[] { "note", "cause", "test" } },
            title = new { type = "string" },
            detail = new { type = "string", description = "Note text, cause rationale, or test purpose" },
            category = new { type = "string", description = "For causes: Ignition, Fuel, AirIntake, Vacuum, Mechanical, Electrical, Sensor, Emissions, Exhaust, Cooling, Transmission, Network, Charging, Starting, Brakes, Other..." },
            procedure = new { type = "array", items = new { type = "string" } },
            tools = new { type = "array", items = new { type = "string" } },
            expected = new { type = "string" },
            implicates = new { type = "array", items = new { type = "string" }, description = "For tests: cause keys an abnormal result would implicate" },
            sources = new { type = "array", items = new { type = "string" }, description = "Source labels like S2" },
        },
        required = new[] { "kind", "title" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var id = ToolArgs.Guid(arguments, "session_id") ?? context.SessionId;
        if (id is null) return ToolExecutionResult.Fail("No diagnostic session is active; nothing was saved.");
        var kind = ToolArgs.String(arguments, "kind");
        var title = ToolArgs.String(arguments, "title") ?? string.Empty;
        var detail = ToolArgs.String(arguments, "detail");
        var sources = ReadStrings(arguments, "sources").Select(context.Sources.Find).OfType<SourceCitation>().ToList();
        var evidence = sources.Count > 0 ? EvidenceClass.SourceDerived : EvidenceClass.AiInference;

        switch (kind)
        {
            case "note":
            {
                var r = await sessions.AddObservationAsync(id.Value, $"[AI note] {title}{(detail is null ? string.Empty : ": " + detail)}", cancellationToken);
                return r.IsSuccess ? ToolExecutionResult.Ok(new { saved = true }, "Note saved") : ToolExecutionResult.Fail(r.Error!.Message);
            }

            case "cause":
            {
                var r = await sessions.AddCauseAsync(new AddCauseCommand
                {
                    SessionId = id.Value,
                    Title = title,
                    Description = detail,
                    Category = DiagnosticTreeBuilder.ParseCategory(ToolArgs.String(arguments, "category")),
                    Likelihood = 0.25,
                    Origin = ActorKind.Ai,
                    Evidence = evidence,
                    Sources = sources,
                }, cancellationToken);
                return r.IsSuccess ? ToolExecutionResult.Ok(new { saved = true, nodeId = r.Value }, $"Cause added: {title}") : ToolExecutionResult.Fail(r.Error!.Message);
            }

            case "test":
            {
                var r = await sessions.AddTestAsync(new AddTestCommand
                {
                    SessionId = id.Value,
                    Title = title,
                    Purpose = detail,
                    Procedure = ReadStrings(arguments, "procedure"),
                    Tools = ReadStrings(arguments, "tools"),
                    ExpectedResult = ToolArgs.String(arguments, "expected"),
                    ImplicatesCauseKeys = ReadStrings(arguments, "implicates"),
                    Origin = ActorKind.Ai,
                    Evidence = evidence,
                    Sources = sources,
                }, cancellationToken);
                return r.IsSuccess ? ToolExecutionResult.Ok(new { saved = true, testId = r.Value }, $"Test added: {title}") : ToolExecutionResult.Fail(r.Error!.Message);
            }

            default:
                return ToolExecutionResult.Fail("kind must be note, cause, or test.");
        }
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement args, string name) =>
        args.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
}
