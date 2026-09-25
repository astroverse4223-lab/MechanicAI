using System.Globalization;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Search;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

/// <summary>
/// Orchestrates a structured diagnostic session:
/// complaint → symptoms → DTCs → causes → tests → results → isolation → repair → verification.
/// Every state change is persisted with a timestamped <see cref="DiagnosticStep"/>.
/// </summary>
public sealed class DiagnosticSessionService(
    IAppDbContextFactory dbFactory,
    IReferenceContentProvider content,
    ISettingsStore settings,
    ILogger<DiagnosticSessionService> logger)
{
    public event EventHandler<Guid>? SessionChanged;

    public async Task<Result<Guid>> StartAsync(StartDiagnosticSessionCommand command, CancellationToken ct = default)
    {
        var dtcs = command.Dtcs
            .Select(d => DtcCode.TryParse(d, out var code, assumePowertrain: true) ? code.Value : null)
            .OfType<string>()
            .Distinct()
            .ToList();

        var invalid = command.Dtcs.Where(d => !DtcCode.TryParse(d, out _, assumePowertrain: true)).ToList();
        if (invalid.Count > 0)
        {
            return Error.Validation($"Not a valid trouble code: {string.Join(", ", invalid)}. Codes look like P0302, U0100, B0001, or C0035.");
        }

        if (string.IsNullOrWhiteSpace(command.Complaint) && dtcs.Count == 0 && command.Symptoms.Count == 0)
        {
            return Error.Validation("Enter the customer complaint, at least one symptom, or a trouble code.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        Vehicle? vehicle = null;
        if (command.VehicleId is { } vehicleId)
        {
            vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId, ct);
            if (vehicle is null) return Error.NotFound("Vehicle");
            vehicle.LastAccessedUtc = DateTime.UtcNow;
            if (command.Mileage is { } miles && (vehicle.Mileage is null || miles > vehicle.Mileage)) vehicle.Mileage = miles;
        }

        var technician = string.IsNullOrWhiteSpace(command.TechnicianName) ? settings.Current.Diagnostics.TechnicianName : command.TechnicianName;
        var vehicleDescription = vehicle?.Description ?? command.VehicleDescription?.Trim() ?? string.Empty;
        var session = new DiagnosticSession
        {
            VehicleId = vehicle?.Id,
            VehicleDescription = vehicleDescription,
            Complaint = command.Complaint.Trim(),
            Symptoms = command.Symptoms.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList(),
            Conditions = command.Conditions.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct().ToList(),
            Mileage = command.Mileage ?? vehicle?.Mileage,
            TechnicianName = technician,
            Status = DiagnosticSessionStatus.Intake,
        };
        session.Title = BuildTitle(session, dtcs);

        var definitions = await db.Dtcs.AsNoTracking()
            .Where(d => dtcs.Contains(d.Code))
            .ToListAsync(ct);

        var actor = string.IsNullOrWhiteSpace(technician) ? null : technician;
        session.AddStep(DiagnosticStepKind.SessionCreated, ActorKind.Technician, "Diagnostic session started",
            string.IsNullOrEmpty(vehicleDescription) ? "Vehicle not specified" : vehicleDescription, EvidenceClass.TechnicianObservation, actorName: actor);
        if (!string.IsNullOrWhiteSpace(session.Complaint))
        {
            session.AddStep(DiagnosticStepKind.ComplaintRecorded, ActorKind.Technician, "Customer complaint recorded", session.Complaint,
                EvidenceClass.TechnicianObservation, actorName: actor);
        }

        foreach (var symptom in session.Symptoms)
        {
            session.AddStep(DiagnosticStepKind.SymptomAdded, ActorKind.Technician, $"Symptom: {symptom}", null, EvidenceClass.TechnicianObservation, actorName: actor);
        }

        foreach (var code in dtcs)
        {
            var definition = PickDefinition(definitions, code, vehicle?.Make);
            session.Dtcs.Add(new SessionDtc
            {
                SessionId = session.Id,
                Code = code,
                Description = definition?.Description,
                Status = DtcStatus.Current,
            });
            session.AddStep(DiagnosticStepKind.DtcAdded, ActorKind.Technician, $"DTC {code} recorded",
                definition is null ? DescribeUnknownCode(code) : $"{definition.Description} ({definition.Source})",
                EvidenceClass.TechnicianObservation, actorName: actor);
        }

        await BuildTreeAsync(session, vehicle, ct);

        db.DiagnosticSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await settings.UpdateAsync(s => s.ActiveSessionId = session.Id, ct);
        logger.LogInformation("Diagnostic session {SessionId} started with {DtcCount} DTC(s) and {CauseCount} candidate cause(s)",
            session.Id, dtcs.Count, session.Causes.Count());
        SessionChanged?.Invoke(this, session.Id);
        return session.Id;
    }

    /// <summary>
    /// Starts a session from free text such as "2017 Silverado 5.3 P0171 and P0174. What should I check?".
    /// Matches an existing saved vehicle when the description is unambiguous.
    /// </summary>
    public async Task<Result<Guid>> StartFromTextAsync(string text, Guid? vehicleId, CancellationToken ct = default)
    {
        var parsed = QueryParser.Parse(text);
        if (!parsed.LooksLikeDiagnosis && string.IsNullOrWhiteSpace(parsed.Remainder))
        {
            return Error.Validation("Describe the problem or enter a trouble code so a diagnostic session can be built.");
        }

        var resolvedVehicleId = vehicleId ?? await FindMatchingVehicleAsync(parsed, ct);
        var complaint = string.IsNullOrWhiteSpace(parsed.Remainder) ? text.Trim() : parsed.Remainder;
        return await StartAsync(new StartDiagnosticSessionCommand
        {
            VehicleId = resolvedVehicleId,
            VehicleDescription = resolvedVehicleId is null ? parsed.VehicleDescription : null,
            Complaint = complaint,
            Symptoms = parsed.Symptoms,
            Conditions = parsed.Conditions,
            Dtcs = parsed.Dtcs,
            Mileage = parsed.Mileage,
        }, ct);
    }

    private async Task<Guid?> FindMatchingVehicleAsync(ParsedQuery parsed, CancellationToken ct)
    {
        if (parsed.Vin is not null)
        {
            await using var vdb = await dbFactory.CreateAsync(ct);
            var byVin = await vdb.Vehicles.AsNoTracking().Where(v => v.Vin == parsed.Vin).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(ct);
            if (byVin is not null) return byVin;
        }

        if (parsed.Year is null || parsed.Model is null) return null;
        await using var db = await dbFactory.CreateAsync(ct);
        var candidates = await db.Vehicles.AsNoTracking()
            .Where(v => v.Year == parsed.Year && v.Model == parsed.Model && !v.IsSample)
            .Select(v => v.Id)
            .Take(2)
            .ToListAsync(ct);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public async Task<DiagnosticSessionView?> GetViewAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await WithGraph(db.DiagnosticSessions).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;
        SortChildren(session);
        return BuildView(session);
    }

    public async Task<IReadOnlyList<DiagnosticSessionSummary>> ListAsync(int take = 50, Guid? vehicleId = null, bool includeClosed = true,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = WithGraph(db.DiagnosticSessions).AsNoTracking();
        if (vehicleId is { } id) query = query.Where(s => s.VehicleId == id);
        if (!includeClosed) query = query.Where(s => s.Status != DiagnosticSessionStatus.Completed && s.Status != DiagnosticSessionStatus.Abandoned);
        var sessions = await query.OrderByDescending(s => s.UpdatedUtc).Take(take).ToListAsync(ct);
        return sessions.Select(ToSummary).ToList();
    }

    public async Task<Result> RecordTestResultAsync(RecordTestResultCommand command, CancellationToken ct = default)
    {
        return await MutateAsync(command.SessionId, (session, _) =>
        {
            if (session.IsClosed) return Error.Validation("This session is closed. Reopen it to record more results.");
            var test = session.Tests.FirstOrDefault(t => t.Id == command.TestId);
            if (test is null) return Error.NotFound("Test");
            var outcome = test.Outcomes.FirstOrDefault(o => o.Key == command.OutcomeKey);
            if (outcome is null) return Error.Validation("Choose one of the listed results for this test.");

            var wasCompleted = test.Status == TestStatus.Completed;
            test.SelectedOutcomeKey = outcome.Key;
            test.Result = outcome.ToResult();
            test.ActualResult = string.IsNullOrWhiteSpace(command.ActualResult) ? null : command.ActualResult.Trim();
            test.PerformedUtc = DateTime.UtcNow;
            test.PerformedBy = command.TechnicianName ?? session.TechnicianName;
            test.Status = TestStatus.Completed;
            if (!wasCompleted || test.ExecutionOrder is null)
            {
                test.ExecutionOrder = session.Tests.Where(t => t.ExecutionOrder is not null).Select(t => t.ExecutionOrder!.Value).DefaultIfEmpty(0).Max() + 1;
            }

            var resultLabel = test.Result switch
            {
                TestResult.Pass => "PASS",
                TestResult.Fail => "FAIL",
                _ => "INCONCLUSIVE",
            };
            var detail = $"{resultLabel}: {outcome.Label}";
            if (test.ActualResult is not null) detail += $"\nObserved: {test.ActualResult}";
            if (!string.IsNullOrWhiteSpace(outcome.Interpretation)) detail += $"\nInterpretation: {outcome.Interpretation}";
            session.AddStep(DiagnosticStepKind.TestResultRecorded, ActorKind.Technician,
                $"{(wasCompleted ? "Result changed" : "Result recorded")} — {test.Title}", detail,
                EvidenceClass.TechnicianObservation, testId: test.Id, actorName: test.PerformedBy);
            return Result.Success();
        }, ct);
    }

    /// <summary>Removes a recorded result ("go back"): the tree is recomputed without it.</summary>
    public async Task<Result> RevertTestResultAsync(Guid sessionId, Guid testId, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var test = session.Tests.FirstOrDefault(t => t.Id == testId);
            if (test is null) return Error.NotFound("Test");
            if (test.Status is not (TestStatus.Completed or TestStatus.Skipped)) return Error.Validation("This test has no recorded result.");
            RevertTest(session, test);
            return Result.Success();
        }, ct);
    }

    /// <summary>Steps back by reverting the most recently recorded test result.</summary>
    public async Task<Result> GoBackAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var last = session.Tests
                .Where(t => t.Status == TestStatus.Completed && t.ExecutionOrder is not null)
                .OrderByDescending(t => t.ExecutionOrder)
                .FirstOrDefault();
            if (last is null) return Error.Validation("There are no recorded results to step back from.");
            RevertTest(session, last);
            return Result.Success();
        }, ct);
    }

    private static void RevertTest(DiagnosticSession session, DiagnosticTest test)
    {
        var previous = test.SelectedOutcome?.Label;
        test.Status = TestStatus.Available;
        test.SelectedOutcomeKey = null;
        test.Result = null;
        test.ActualResult = null;
        test.PerformedUtc = null;
        test.ExecutionOrder = null;
        foreach (var step in session.Steps.Where(s => s.TestId == test.Id && s.Kind == DiagnosticStepKind.TestResultRecorded && !s.IsReverted))
        {
            step.IsReverted = true;
            step.RevertedUtc = DateTime.UtcNow;
        }

        session.AddStep(DiagnosticStepKind.TestResultReverted, ActorKind.Technician, $"Result removed — {test.Title}",
            previous is null ? null : $"Previously: {previous}", testId: test.Id);
        if (session.Status is DiagnosticSessionStatus.Isolated or DiagnosticSessionStatus.Repair or DiagnosticSessionStatus.Verification &&
            session.ConfirmedCauseNodeId is null)
        {
            session.Status = DiagnosticSessionStatus.Testing;
        }
    }

    public async Task<Result> SkipTestAsync(Guid sessionId, Guid testId, string? reason, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var test = session.Tests.FirstOrDefault(t => t.Id == testId);
            if (test is null) return Error.NotFound("Test");
            test.Status = TestStatus.Skipped;
            session.AddStep(DiagnosticStepKind.ObservationAdded, ActorKind.Technician, $"Test skipped — {test.Title}", reason, testId: test.Id);
            return Result.Success();
        }, ct);
    }

    public async Task<Result> SetCauseStatusAsync(Guid sessionId, Guid nodeId, CauseStatus status, string? reason, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var node = session.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Kind == DiagnosticNodeKind.Cause);
            if (node is null) return Error.NotFound("Cause");
            if (status == CauseStatus.Confirmed) return Error.Validation("Use 'Confirm diagnosis' to confirm a cause.");

            var from = node.Status;
            node.Status = status;
            node.IsManualStatus = status != CauseStatus.Open;
            node.StatusReason = string.IsNullOrWhiteSpace(reason) ? $"Set by technician ({status})" : reason.Trim();
            session.AddStep(DiagnosticStepKind.CauseStatusChanged, ActorKind.Technician, $"{node.Title}: {from} → {status}", node.StatusReason,
                EvidenceClass.TechnicianObservation, nodeId: node.Id);
            return Result.Success();
        }, ct);
    }

    public async Task<Result> AddObservationAsync(Guid sessionId, string observation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(observation)) return Error.Validation("Observation is empty.");
        return await MutateAsync(sessionId, (session, _) =>
        {
            session.AddStep(DiagnosticStepKind.ObservationAdded, ActorKind.Technician, "Technician observation", observation.Trim(),
                EvidenceClass.TechnicianObservation, actorName: session.TechnicianName);
            return Result.Success();
        }, ct, recalculate: false);
    }

    public async Task<Result> SetInitialCheckAsync(Guid sessionId, string check, bool done, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var list = session.CompletedInitialChecks.ToList();
            if (done && !list.Contains(check))
            {
                list.Add(check);
                session.AddStep(DiagnosticStepKind.ObservationAdded, ActorKind.Technician, "Initial check completed", check, EvidenceClass.TechnicianObservation);
            }
            else if (!done)
            {
                list.Remove(check);
            }

            session.CompletedInitialChecks = list;
            return Result.Success();
        }, ct, recalculate: false);
    }

    public async Task<Result> AnswerQuestionAsync(Guid sessionId, int index, string answer, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            if (index < 0 || index >= session.ClarifyingQuestions.Count) return Error.NotFound("Question");
            var questions = session.ClarifyingQuestions.Select(q => new ClarifyingQuestion
            {
                Question = q.Question,
                Answer = q.Answer,
                AskedBy = q.AskedBy,
                AnsweredUtc = q.AnsweredUtc,
            }).ToList();
            questions[index].Answer = answer.Trim();
            questions[index].AnsweredUtc = DateTime.UtcNow;
            session.ClarifyingQuestions = questions;
            session.AddStep(DiagnosticStepKind.ClarifyingQuestionAnswered, ActorKind.Technician, questions[index].Question, answer.Trim(),
                EvidenceClass.TechnicianObservation);

            // Answers are symptom evidence: rebuild so symptom modifiers (cold/hot/load...) apply.
            return Result.Success();
        }, ct, rebuild: true);
    }

    public async Task<Result> AddDtcAsync(Guid sessionId, string code, DtcStatus status = DtcStatus.Current, string source = "Technician entry",
        CancellationToken ct = default)
    {
        if (!DtcCode.TryParse(code, out var parsed, assumePowertrain: true)) return Error.Validation($"'{code}' is not a valid trouble code.");
        await using var lookup = await dbFactory.CreateAsync(ct);
        var definitions = await lookup.Dtcs.AsNoTracking().Where(d => d.Code == parsed.Value).ToListAsync(ct);

        return await MutateAsync(sessionId, (session, _) =>
        {
            if (session.Dtcs.Any(d => d.Code == parsed.Value)) return Error.Validation($"{parsed.Value} is already recorded.");
            var definition = PickDefinition(definitions, parsed.Value, session.Vehicle?.Make);
            session.Dtcs.Add(new SessionDtc { SessionId = session.Id, Code = parsed.Value, Description = definition?.Description, Status = status, Source = source });
            session.AddStep(DiagnosticStepKind.DtcAdded, ActorKind.Technician, $"DTC {parsed.Value} recorded",
                definition?.Description ?? DescribeUnknownCode(parsed.Value), EvidenceClass.TechnicianObservation);
            return Result.Success();
        }, ct, rebuild: true);
    }

    public async Task<Result> RemoveDtcAsync(Guid sessionId, string code, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var dtc = session.Dtcs.FirstOrDefault(d => d.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (dtc is null) return Error.NotFound("DTC");
            session.Dtcs.Remove(dtc);
            session.AddStep(DiagnosticStepKind.DtcRemoved, ActorKind.Technician, $"DTC {dtc.Code} removed", null);
            return Result.Success();
        }, ct, rebuild: true);
    }

    public async Task<Result> UpdateComplaintAsync(Guid sessionId, string complaint, IReadOnlyList<string> symptoms, IReadOnlyList<string> conditions,
        CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            session.Complaint = complaint.Trim();
            session.Symptoms = symptoms.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            session.Conditions = conditions.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            session.AddStep(DiagnosticStepKind.ComplaintRecorded, ActorKind.Technician, "Complaint updated", session.Complaint, EvidenceClass.TechnicianObservation);
            return Result.Success();
        }, ct, rebuild: true);
    }

    public async Task<Result<Guid>> AddCauseAsync(AddCauseCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Title)) return Error.Validation("Give the cause a name.");
        var id = Guid.Empty;
        var result = await MutateAsync(command.SessionId, (session, _) =>
        {
            var key = string.IsNullOrWhiteSpace(command.Key) ? "custom-" + Slug(command.Title) : command.Key!;
            if (session.Nodes.Any(n => n.Kind == DiagnosticNodeKind.Cause && n.Key == key))
            {
                return Error.Validation("That cause is already in the tree.");
            }

            var existingPriors = session.Causes.Where(c => c.Key != DiagnosticMath.UnlistedCauseKey).Select(c => c.PriorProbability).DefaultIfEmpty(0.1);
            var node = new DiagnosticNode
            {
                SessionId = session.Id,
                Kind = DiagnosticNodeKind.Cause,
                Key = key,
                Title = command.Title.Trim(),
                Description = command.Description,
                Category = command.Category,
                PriorProbability = Math.Clamp(command.Likelihood, 0.01, 1.0) * existingPriors.Max(),
                Origin = command.Origin,
                OriginDetail = command.Origin == ActorKind.Ai ? "AI analysis" : "Added by technician",
                Evidence = command.Evidence,
                Sources = command.Sources.Select(s => s.Clone()).ToList(),
                SafetyTags = SafetyAdvisor.DetectTags($"{command.Title} {command.Description}").ToList(),
            };
            session.Nodes.Add(node);
            id = node.Id;
            session.AddStep(DiagnosticStepKind.CauseAdded, command.Origin, $"Possible cause added: {node.Title}", command.Description,
                command.Evidence, nodeId: node.Id);
            return Result.Success();
        }, ct, rebuild: true);

        return result.IsSuccess ? id : Result<Guid>.Failure(result.Error!);
    }

    public async Task<Result<Guid>> AddTestAsync(AddTestCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Title)) return Error.Validation("Give the test a name.");
        var id = Guid.Empty;
        var result = await MutateAsync(command.SessionId, (session, _) =>
        {
            var implicated = command.ImplicatesCauseKeys
                .Where(k => session.Nodes.Any(n => n.Kind == DiagnosticNodeKind.Cause && n.Key == k))
                .Distinct()
                .ToList();
            var likelihoodAbnormal = implicated.ToDictionary(k => k, _ => 0.85, StringComparer.Ordinal);
            likelihoodAbnormal[DiagnosticMath.DefaultLikelihoodKey] = 0.15;
            var likelihoodNormal = implicated.ToDictionary(k => k, _ => 0.15, StringComparer.Ordinal);
            likelihoodNormal[DiagnosticMath.DefaultLikelihoodKey] = 0.85;

            var test = new DiagnosticTest
            {
                SessionId = session.Id,
                Key = string.IsNullOrWhiteSpace(command.Key) ? "custom-" + Slug(command.Title) + "-" + session.Tests.Count.ToString(CultureInfo.InvariantCulture) : command.Key!,
                Title = command.Title.Trim(),
                Purpose = command.Purpose,
                Procedure = command.Procedure.ToList(),
                Tools = command.Tools.ToList(),
                ExpectedResult = command.ExpectedResult,
                EstimatedMinutes = Math.Clamp(command.EstimatedMinutes, 1, 600),
                Difficulty = Math.Clamp(command.Difficulty, 1, 5),
                Invasiveness = Math.Clamp(command.Invasiveness, 1, 5),
                SafetyTags = command.SafetyTags.Where(SafetyTags.IsValid).Concat(SafetyAdvisor.DetectTags(string.Join(' ', command.Procedure))).Distinct().ToList(),
                RelatedCauseKeys = implicated,
                Outcomes =
                [
                    new TestOutcomeDefinition { Key = "normal", Label = "Result within expected range", Normal = true, Likelihoods = likelihoodNormal },
                    new TestOutcomeDefinition { Key = "abnormal", Label = "Abnormal result", Normal = false, Likelihoods = likelihoodAbnormal },
                    new TestOutcomeDefinition { Key = "inconclusive", Label = "Inconclusive", Inconclusive = true },
                ],
                Origin = command.Origin,
                OriginDetail = command.Origin == ActorKind.Ai ? "AI analysis" : "Added by technician",
                Evidence = command.Evidence,
                Sources = command.Sources.Select(s => s.Clone()).ToList(),
            };
            test.PrimaryNodeId = session.Nodes.FirstOrDefault(n => implicated.Count > 0 && n.Key == implicated[0])?.Id
                                 ?? session.Nodes.FirstOrDefault(n => n.Kind == DiagnosticNodeKind.Root)?.Id;
            session.Tests.Add(test);
            id = test.Id;
            session.AddStep(DiagnosticStepKind.TestAdded, command.Origin, $"Test added: {test.Title}", command.Purpose, command.Evidence, testId: test.Id);
            return Result.Success();
        }, ct);

        return result.IsSuccess ? id : Result<Guid>.Failure(result.Error!);
    }

    public async Task<Result> ConfirmDiagnosisAsync(Guid sessionId, Guid nodeId, string? notes, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            var node = session.Nodes.FirstOrDefault(n => n.Id == nodeId && n.Kind == DiagnosticNodeKind.Cause);
            if (node is null) return Error.NotFound("Cause");
            if (node.Key == DiagnosticMath.UnlistedCauseKey) return Error.Validation("Add the actual cause to the tree first, then confirm it.");

            var hasSupportingTest = session.Tests.Any(t => t.Status == TestStatus.Completed && t.RelatedCauseKeys.Contains(node.Key));
            foreach (var other in session.Causes.Where(c => c.Status == CauseStatus.Confirmed && c.Id != node.Id))
            {
                other.Status = CauseStatus.Open;
                other.IsManualStatus = false;
            }

            node.Status = CauseStatus.Confirmed;
            node.IsManualStatus = true;
            node.Evidence = EvidenceClass.Verified;
            node.StatusReason = string.IsNullOrWhiteSpace(notes) ? "Confirmed by technician" : notes.Trim();
            session.ConfirmedCauseNodeId = node.Id;
            session.FinalDiagnosis = string.IsNullOrWhiteSpace(notes) ? node.Title : $"{node.Title} — {notes.Trim()}";
            session.Status = DiagnosticSessionStatus.Repair;
            session.AddStep(DiagnosticStepKind.DiagnosisConfirmed, ActorKind.Technician, $"Diagnosis confirmed: {node.Title}",
                (hasSupportingTest ? string.Empty : "Note: no completed test directly supports this cause. ") + (notes ?? string.Empty),
                EvidenceClass.Verified, nodeId: node.Id, actorName: session.TechnicianName);
            return Result.Success();
        }, ct);
    }

    public async Task<Result> RecordRepairAsync(RecordRepairCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Description)) return Error.Validation("Describe the repair performed.");
        return await MutateAsync(command.SessionId, (session, db) =>
        {
            session.RepairPerformed = command.Description.Trim();
            session.Status = DiagnosticSessionStatus.Verification;
            var repair = new Repair
            {
                VehicleId = session.VehicleId,
                DiagnosticSessionId = session.Id,
                Kind = RepairKind.Repair,
                Status = RepairStatus.Completed,
                Title = session.FinalDiagnosis ?? Text.Truncate(command.Description, 80),
                Description = command.Description.Trim(),
                Mileage = session.Mileage,
                TechnicianName = command.TechnicianName ?? session.TechnicianName,
                LaborHours = command.LaborHours,
                Parts = command.Parts.Where(p => !string.IsNullOrWhiteSpace(p.Description)).Select(p => new Part
                {
                    Description = p.Description.Trim(),
                    PartNumber = p.PartNumber,
                    Quantity = p.Quantity <= 0 ? 1 : p.Quantity,
                    UnitPrice = p.UnitPrice,
                    Brand = p.Brand,
                }).ToList(),
            };
            db.Repairs.Add(repair);
            var partsText = repair.Parts.Count == 0 ? "No parts" : string.Join(", ", repair.Parts.Select(p => $"{p.Quantity:0.##} × {p.Description}{(p.PartNumber is null ? string.Empty : $" ({p.PartNumber})")}"));
            session.AddStep(DiagnosticStepKind.RepairRecorded, ActorKind.Technician, "Repair recorded", $"{repair.Description}\nParts: {partsText}",
                EvidenceClass.TechnicianObservation, actorName: repair.TechnicianName);
            return Result.Success();
        }, ct, recalculate: false);
    }

    public async Task<Result> RecordVerificationAsync(RecordVerificationCommand command, CancellationToken ct = default)
    {
        return await MutateAsync(command.SessionId, (session, db) =>
        {
            session.VerificationPassed = command.Passed;
            session.VerificationNotes = command.Notes?.Trim();
            if (command.Passed)
            {
                session.Status = DiagnosticSessionStatus.Completed;
                session.CompletedUtc = DateTime.UtcNow;
                session.AddStep(DiagnosticStepKind.VerificationRecorded, ActorKind.Technician, "Repair verified — complaint resolved", command.Notes,
                    EvidenceClass.Verified, actorName: command.TechnicianName ?? session.TechnicianName);
                session.AddStep(DiagnosticStepKind.SessionCompleted, ActorKind.System, "Session completed", null);
            }
            else
            {
                // The confirmed cause did not resolve the complaint: it may have been one of several
                // faults or a misdiagnosis. Return to testing with that cause de-emphasized.
                if (session.ConfirmedCauseNodeId is { } confirmedId && session.Nodes.FirstOrDefault(n => n.Id == confirmedId) is { } confirmed)
                {
                    confirmed.Status = CauseStatus.Open;
                    confirmed.IsManualStatus = false;
                    confirmed.Evidence = EvidenceClass.TechnicianObservation;
                    confirmed.StatusReason = "Repaired, but verification failed — the complaint persists.";
                    confirmed.PriorProbability *= 0.3;
                }

                session.ConfirmedCauseNodeId = null;
                session.Status = DiagnosticSessionStatus.Testing;
                session.AddStep(DiagnosticStepKind.VerificationRecorded, ActorKind.Technician, "Verification FAILED — complaint persists",
                    command.Notes, EvidenceClass.TechnicianObservation, actorName: command.TechnicianName ?? session.TechnicianName);
            }

            return Result.Success();
        }, ct);
    }

    public async Task<Result> ReopenAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            if (!session.IsClosed) return Result.Success();
            session.Status = session.ConfirmedCauseNodeId is null ? DiagnosticSessionStatus.Testing : DiagnosticSessionStatus.Verification;
            session.CompletedUtc = null;
            session.AddStep(DiagnosticStepKind.SessionReopened, ActorKind.Technician, "Session reopened", null);
            return Result.Success();
        }, ct);
    }

    public async Task<Result> AbandonAsync(Guid sessionId, string? reason, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            session.Status = DiagnosticSessionStatus.Abandoned;
            session.CompletedUtc = DateTime.UtcNow;
            session.AddStep(DiagnosticStepKind.SessionCompleted, ActorKind.Technician, "Session closed without repair", reason);
            return Result.Success();
        }, ct, recalculate: false);
    }

    public async Task<Result> DeleteAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await WithGraph(db.DiagnosticSessions).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return Error.NotFound("Session");
        db.DiagnosticSessions.Remove(session);
        await db.SaveChangesAsync(ct);
        if (settings.Current.ActiveSessionId == sessionId) await settings.UpdateAsync(s => s.ActiveSessionId = null, ct);
        SessionChanged?.Invoke(this, sessionId);
        return Result.Success();
    }

    /// <summary>Stores an AI analysis summary on the session (labeled AI inference) and logs it.</summary>
    public async Task<Result> RecordAiAnalysisAsync(Guid sessionId, string summary, IReadOnlyList<string> questions, CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            session.AiSummary = summary;
            if (questions.Count > 0)
            {
                var existing = session.ClarifyingQuestions.ToList();
                foreach (var q in questions.Where(q => !existing.Any(e => e.Question.Equals(q, StringComparison.OrdinalIgnoreCase))).Take(6))
                {
                    existing.Add(new ClarifyingQuestion { Question = q, AskedBy = ActorKind.Ai });
                }

                session.ClarifyingQuestions = existing;
            }

            session.AddStep(DiagnosticStepKind.AiAnalysis, ActorKind.Ai, "AI analysis", summary, EvidenceClass.AiInference);
            return Result.Success();
        }, ct, recalculate: false);
    }

    /// <summary>Adjusts cause priors from AI research (bounded ×0.5–×2, always logged with its reason).</summary>
    public async Task<Result> AdjustPriorsAsync(Guid sessionId, IReadOnlyList<(string CauseKey, double Factor, string Reason)> adjustments,
        CancellationToken ct = default)
    {
        return await MutateAsync(sessionId, (session, _) =>
        {
            foreach (var (key, factor, reason) in adjustments)
            {
                var node = session.Nodes.FirstOrDefault(n => n.Kind == DiagnosticNodeKind.Cause && n.Key == key);
                if (node is null || node.Key == DiagnosticMath.UnlistedCauseKey) continue;
                var bounded = Math.Clamp(factor, 0.5, 2.0);
                node.PriorProbability *= bounded;
                session.AddStep(DiagnosticStepKind.AiAnalysis, ActorKind.Ai, $"Priority of '{node.Title}' adjusted ×{bounded:0.##}", reason,
                    EvidenceClass.AiInference, nodeId: node.Id);
            }

            return Result.Success();
        }, ct);
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result> MutateAsync(
        Guid sessionId,
        Func<DiagnosticSession, IAppDbContext, Result> mutate,
        CancellationToken ct,
        bool recalculate = true,
        bool rebuild = false)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var session = await WithGraph(db.DiagnosticSessions).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return Error.NotFound("Diagnostic session");

        var result = mutate(session, db);
        if (result.IsFailure) return result;

        if (rebuild)
        {
            session.Title = BuildTitle(session, session.Dtcs.Select(d => d.Code).ToList());
            await BuildTreeAsync(session, session.Vehicle, ct);
        }
        else if (recalculate)
        {
            ApplyRecalculation(session);
        }

        session.Touch();
        await db.SaveChangesAsync(ct);
        SessionChanged?.Invoke(this, session.Id);
        return Result.Success();
    }

    private async Task BuildTreeAsync(DiagnosticSession session, Vehicle? vehicle, CancellationToken ct)
    {
        var playbooks = await content.LoadPlaybooksAsync(ct);
        var dtcs = session.Dtcs.Select(d => d.Code).ToList();
        var symptomText = string.Join(". ", new[] { session.Complaint }
            .Concat(session.Symptoms)
            .Concat(session.Conditions)
            .Concat(session.ClarifyingQuestions.Where(q => q.Answer is not null).Select(q => $"{q.Question} {q.Answer}")));

        var matches = PlaybookMatcher.Match(playbooks, dtcs, symptomText);
        var tree = DiagnosticTreeBuilder.Build(matches, new TreeContext(dtcs, symptomText, session.Mileage ?? vehicle?.Mileage));
        var rootTitle = dtcs.Count > 0 ? string.Join(" + ", dtcs) : Text.Truncate(session.Complaint, 60);
        DiagnosticTreeMaterializer.Materialize(session, tree, string.IsNullOrWhiteSpace(rootTitle) ? "Complaint" : rootTitle);

        session.PlaybookKeys = matches.Select(m => m.Playbook.Key).ToList();
        if (tree.VerificationSteps.Count > 0 || session.VerificationPlan.Count == 0)
        {
            session.VerificationPlan = tree.VerificationSteps.Count > 0
                ? tree.VerificationSteps.ToList()
                : ["Clear DTCs only after recording freeze-frame data.", "Operate the vehicle under the conditions that produced the complaint.", "Confirm the complaint is resolved and no DTCs return."];
        }

        var questions = session.ClarifyingQuestions.ToList();
        foreach (var q in tree.ClarifyingQuestions)
        {
            if (!questions.Any(e => e.Question.Equals(q, StringComparison.OrdinalIgnoreCase)))
            {
                questions.Add(new ClarifyingQuestion { Question = q, AskedBy = ActorKind.System });
            }
        }

        session.ClarifyingQuestions = questions;

        var description = tree.IsEmpty
            ? "No built-in playbook matches these codes/symptoms. Research the code (DTC lookup, service documents, web) or run AI analysis to build the tree."
            : $"{tree.Causes.Count} candidate causes and {tree.Tests.Count} tests from: {string.Join(", ", matches.Select(m => $"{m.Playbook.Title} ({string.Join("; ", m.Reasons)})"))}.";
        session.AddStep(DiagnosticStepKind.TreeGenerated, ActorKind.System, "Diagnostic tree built", description, EvidenceClass.SourceDerived);

        ApplyRecalculation(session);
        if (session.Status is DiagnosticSessionStatus.Intake or DiagnosticSessionStatus.Analysis)
        {
            session.Status = session.Tests.Count > 0 ? DiagnosticSessionStatus.Testing : DiagnosticSessionStatus.Analysis;
        }
    }

    private void ApplyRecalculation(DiagnosticSession session)
    {
        var previousTop = session.Tests.FirstOrDefault(t => t.Status == TestStatus.Recommended)?.Id;
        var result = DiagnosticRecalculator.Recalculate(session, settings.Current.Diagnostics);

        foreach (var change in result.StatusChanges)
        {
            session.AddStep(DiagnosticStepKind.CauseStatusChanged, ActorKind.System, $"{change.Node.Title}: {change.From} → {change.To}",
                change.Reason, change.To == CauseStatus.Likely ? EvidenceClass.AiInference : null, nodeId: change.Node.Id);
        }

        var top = result.Ranking.FirstOrDefault()?.Test;
        if (top is not null && top.Id != previousTop && !session.IsClosed)
        {
            session.AddStep(DiagnosticStepKind.TestRecommended, ActorKind.System, $"Next recommended test: {top.Title}",
                $"Expected information gain {top.InformationGain:0.00} bits; est. {top.EstimatedMinutes} min.", EvidenceClass.AiInference, testId: top.Id);
        }

        var anyLikely = session.Causes.Any(c => c.Status == CauseStatus.Likely);
        if (session.Status == DiagnosticSessionStatus.Testing && anyLikely) session.Status = DiagnosticSessionStatus.Isolated;
        else if (session.Status == DiagnosticSessionStatus.Isolated && !anyLikely) session.Status = DiagnosticSessionStatus.Testing;
    }

    private DiagnosticSessionView BuildView(DiagnosticSession session)
    {
        var belief = DiagnosticRecalculator.CurrentBelief(session);
        var ranking = DiagnosticRecalculator.RankTests(session, belief);
        var leading = session.Causes
            .Where(c => c.Key != DiagnosticMath.UnlistedCauseKey && c.Status != CauseStatus.RuledOut)
            .OrderByDescending(c => c.Status == CauseStatus.Confirmed)
            .ThenByDescending(c => c.Probability)
            .FirstOrDefault();
        var outside = belief.GetValueOrDefault(DiagnosticMath.UnlistedCauseKey) >= 0.35;

        var tags = session.Nodes.SelectMany(n => n.SafetyTags)
            .Concat(ranking.Take(3).SelectMany(r => r.Test.SafetyTags))
            .Concat(SafetyAdvisor.DetectTags($"{session.Complaint} {string.Join(' ', session.Dtcs.Select(d => d.Code))} {session.VehicleDescription}"));
        return new DiagnosticSessionView(session, ranking, leading, outside, SafetyAdvisor.ForTags(tags), DiagnosticStages.InitialChecks);
    }

    private static DiagnosticSessionSummary ToSummary(DiagnosticSession s)
    {
        var leading = s.Causes
            .Where(c => c.Key != DiagnosticMath.UnlistedCauseKey && c.Status != CauseStatus.RuledOut)
            .OrderByDescending(c => c.Status == CauseStatus.Confirmed)
            .ThenByDescending(c => c.Probability)
            .FirstOrDefault();
        var next = s.Tests.FirstOrDefault(t => t.Status == TestStatus.Recommended);
        return new DiagnosticSessionSummary(
            s.Id,
            s.Title,
            s.Vehicle?.Description ?? s.VehicleDescription,
            s.VehicleId,
            s.Status,
            s.Dtcs.Select(d => d.Code).ToList(),
            s.Complaint,
            s.StartedUtc,
            s.UpdatedUtc,
            leading?.Title,
            leading?.Probability,
            s.Tests.Count(t => t.Status == TestStatus.Completed),
            next?.Title,
            s.IsSample);
    }

    private static IQueryable<DiagnosticSession> WithGraph(IQueryable<DiagnosticSession> query) =>
        query
            .Include(s => s.Vehicle)
            .Include(s => s.Dtcs)
            .Include(s => s.Nodes)
            .Include(s => s.Tests)
            .Include(s => s.Steps)
            .AsSplitQuery();

    private static void SortChildren(DiagnosticSession session)
    {
        session.Nodes = session.Nodes.OrderBy(n => n.SortOrder).ToList();
        session.Steps = session.Steps.OrderBy(s => s.Sequence).ToList();
        session.Dtcs = session.Dtcs.OrderBy(d => d.RecordedUtc).ToList();
    }

    private static DtcDefinition? PickDefinition(IReadOnlyList<DtcDefinition> definitions, string code, string? make)
    {
        var matching = definitions.Where(d => d.Code == code).ToList();
        return matching.FirstOrDefault(d => make is not null && string.Equals(d.Manufacturer, make, StringComparison.OrdinalIgnoreCase))
               ?? matching.FirstOrDefault(d => d.IsGeneric && d.Manufacturer is null);
    }

    private static string DescribeUnknownCode(string code)
    {
        if (!DtcCode.TryParse(code, out var parsed)) return "Unrecognized code";
        return parsed.IsManufacturerSpecific
            ? $"Manufacturer-specific {parsed.System.ToString().ToLowerInvariant()} code — its meaning varies by manufacturer. Look it up in OEM service information for this vehicle."
            : $"Generic {parsed.SubsystemDescription} code not in the built-in reference. Verify its definition in service information.";
    }

    private static string BuildTitle(DiagnosticSession session, IReadOnlyList<string> dtcs)
    {
        var head = dtcs.Count > 0 ? string.Join(", ", dtcs.Take(3)) + (dtcs.Count > 3 ? "…" : string.Empty) : null;
        var complaint = Text.Truncate(Text.CollapseWhitespace(session.Complaint), 60);
        return (head, complaint) switch
        {
            (not null, { Length: > 0 }) => $"{head} — {complaint}",
            (not null, _) => head,
            (_, { Length: > 0 }) => complaint,
            _ => session.Symptoms.FirstOrDefault() ?? "Diagnostic session",
        };
    }

    private static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return Text.Truncate(slug.Trim('-'), 40, string.Empty);
    }
}
