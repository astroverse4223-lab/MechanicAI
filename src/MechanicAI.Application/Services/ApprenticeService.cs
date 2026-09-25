using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Content;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

public sealed record ScenarioBriefing(
    Guid AttemptId,
    string ScenarioKey,
    string Title,
    string Vehicle,
    int? Mileage,
    string Complaint,
    string? CustomerStatement,
    IReadOnlyList<string> Codes,
    IReadOnlyDictionary<string, string>? FreezeFrame,
    TrainingLevel Difficulty,
    string Notice);

public sealed record ApprenticeTurn(
    string Input,
    string? TestKey,
    string? TestTitle,
    string? Result,
    int Points,
    string Feedback,
    DateTime AtUtc);

public sealed record ScenarioGrade(
    bool RootCauseCorrect,
    int Score,
    int MaxScore,
    bool Passed,
    string RootCause,
    IReadOnlyList<string> IdealPath,
    string Debrief,
    IReadOnlyList<string> CommonMistakes,
    IReadOnlyList<ApprenticeTurn> Turns);

/// <summary>
/// Apprentice mode: a fictional case where the apprentice chooses what to check. Each test
/// reveals only its own result. The root cause is revealed only after a diagnosis is submitted.
/// Scoring rewards efficient, discriminating tests and penalizes replacing parts without proof.
/// </summary>
public sealed class ApprenticeService(IAppDbContextFactory dbFactory, IAiRouter router)
{
    public const string FictionNotice = "Training scenario — fictional case. Readings are illustrative values chosen for teaching, not specifications for this vehicle.";

    private sealed class AttemptState
    {
        public List<ApprenticeTurn> Turns { get; set; } = [];

        public List<string> PerformedTests { get; set; } = [];

        public bool HintsUsed { get; set; }

        public string? SubmittedDiagnosis { get; set; }
    }

    public async Task<IReadOnlyList<TrainingScenario>> ListScenariosAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.TrainingScenarios.AsNoTracking().OrderBy(s => s.Difficulty).ThenBy(s => s.Title).ToListAsync(ct);
    }

    public async Task<Result<ScenarioBriefing>> StartAsync(string scenarioKey, string? trainee, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var entity = await db.TrainingScenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Key == scenarioKey, ct);
        var scenario = entity is null ? null : Json.Deserialize<ScenarioContent>(entity.DefinitionJson);
        if (entity is null || scenario is null) return Error.NotFound("Scenario");

        var attempt = new TrainingAttempt
        {
            Kind = TrainingAttemptKind.Scenario,
            ReferenceKey = scenarioKey,
            Title = scenario.Title,
            TraineeName = trainee,
            MaxScore = MaxScore(scenario),
            DetailsJson = Json.Serialize(new AttemptState()),
        };
        db.TrainingAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return new ScenarioBriefing(attempt.Id, scenario.Key, scenario.Title, scenario.Vehicle.ToString(), scenario.Mileage, scenario.Complaint,
            scenario.CustomerStatement, scenario.Codes, scenario.FreezeFrame, entity.Difficulty, FictionNotice);
    }

    /// <summary>The list of available checks (a hint: using it is recorded).</summary>
    public async Task<IReadOnlyList<(string Key, string Title)>> GetTestMenuAsync(Guid attemptId, CancellationToken ct = default)
    {
        var (attempt, scenario, state, db) = await LoadAsync(attemptId, ct);
        await using (db)
        {
            if (attempt is null || scenario is null) return [];
            state.HintsUsed = true;
            attempt.DetailsJson = Json.Serialize(state);
            await db.SaveChangesAsync(ct);
            return scenario.Tests.OrderBy(t => t.Title).Select(t => (t.Key, t.Title)).ToList();
        }
    }

    /// <summary>
    /// The apprentice says what they would check. The input is matched to a scenario test
    /// by keywords (or chosen directly by key); the observed result and feedback are returned.
    /// </summary>
    public async Task<Result<ApprenticeTurn>> CheckAsync(Guid attemptId, string input, string? testKey, CancellationToken ct = default)
    {
        var (attempt, scenario, state, db) = await LoadAsync(attemptId, ct);
        await using (db)
        {
            if (attempt is null || scenario is null) return Error.NotFound("Attempt");
            if (attempt.CompletedUtc is not null) return Error.Validation("This scenario is finished. Start it again to retry.");

            var test = testKey is not null ? scenario.Tests.FirstOrDefault(t => t.Key == testKey) : Match(scenario, input);
            ApprenticeTurn turn;
            if (test is null)
            {
                turn = new ApprenticeTurn(input, null, null, null, 0,
                    "I couldn't match that to a specific check. Describe the test you'd perform (for example \"review fuel trims\", \"smoke test the intake\", \"check the coil\"), or open the test menu.",
                    DateTime.UtcNow);
            }
            else if (state.PerformedTests.Contains(test.Key))
            {
                turn = new ApprenticeTurn(input, test.Key, test.Title, test.Result, 0, "You already performed this check — its result hasn't changed.", DateTime.UtcNow);
            }
            else
            {
                state.PerformedTests.Add(test.Key);
                var feedback = await TutorFeedbackAsync(scenario, state, test, input, ct) ?? DeterministicFeedback(test);
                turn = new ApprenticeTurn(input, test.Key, test.Title, test.Result, test.Points, feedback, DateTime.UtcNow);
            }

            state.Turns.Add(turn);
            attempt.Score = Math.Max(0, state.Turns.Sum(t => t.Points));
            attempt.DetailsJson = Json.Serialize(state);
            await db.SaveChangesAsync(ct);
            return turn;
        }
    }

    public async Task<Result<ScenarioGrade>> SubmitDiagnosisAsync(Guid attemptId, string diagnosis, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(diagnosis)) return Error.Validation("Describe your diagnosis.");
        var (attempt, scenario, state, db) = await LoadAsync(attemptId, ct);
        await using (db)
        {
            if (attempt is null || scenario is null) return Error.NotFound("Attempt");
            var lower = diagnosis.ToLowerInvariant();
            var keywordHits = scenario.RootCauseKeywords.Count(k => lower.Contains(k.ToLowerInvariant(), StringComparison.Ordinal));
            var correct = keywordHits >= Math.Min(2, Math.Max(1, scenario.RootCauseKeywords.Count / 2));
            if (!correct && keywordHits > 0) correct = await AiJudgeAsync(scenario, diagnosis, ct) ?? false;

            var max = MaxScore(scenario);
            var score = Math.Max(0, state.Turns.Sum(t => t.Points)) + (correct ? 25 : 0) - (state.HintsUsed ? 5 : 0);
            var passed = correct && score >= (max + 25) * 0.5;
            state.SubmittedDiagnosis = diagnosis;
            attempt.Score = Math.Max(0, score);
            attempt.MaxScore = max + 25;
            attempt.Passed = passed;
            attempt.CompletedUtc = DateTime.UtcNow;
            attempt.DetailsJson = Json.Serialize(state);
            await db.SaveChangesAsync(ct);

            var ideal = scenario.IdealPath.Select(k => scenario.Tests.FirstOrDefault(t => t.Key == k)?.Title ?? k).ToList();
            return new ScenarioGrade(correct, (int)attempt.Score, (int)attempt.MaxScore, passed, scenario.RootCause, ideal, scenario.Debrief,
                scenario.CommonMistakes, state.Turns);
        }
    }

    internal static ScenarioTest? Match(ScenarioContent scenario, string input)
    {
        var lower = input.ToLowerInvariant();
        var scored = scenario.Tests
            .Select(t => (Test: t, Score: t.Keywords.Sum(k => lower.Contains(k.ToLowerInvariant(), StringComparison.Ordinal) ? k.Length : 0)
                                          + (lower.Contains(t.Title.ToLowerInvariant(), StringComparison.Ordinal) ? 50 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();
        return scored.Count == 0 ? null : scored[0].Test;
    }

    private static string DeterministicFeedback(ScenarioTest test) => test.Value switch
    {
        "high" => "Good choice — this check narrows the possibilities efficiently. " + test.Teaches,
        "medium" => "Reasonable check. Think about what result would rule causes in or out. " + test.Teaches,
        "parts-cannon" => "Careful: that replaces a part without proving it's faulty. Test before replace. " + test.Teaches,
        _ => "This check doesn't narrow the problem much. What would a more targeted test tell you? " + test.Teaches,
    };

    private async Task<string?> TutorFeedbackAsync(ScenarioContent scenario, AttemptState state, ScenarioTest test, string input, CancellationToken ct)
    {
        try
        {
            var route = await router.ResolveChatAsync(AiTask.Training, DataSensitivity.General, cancellationToken: ct);
            if (!route.IsAvailable) return null;
            var hidden = new StringBuilder()
                .Append("HIDDEN (never reveal): root cause = ").Append(scenario.RootCause)
                .Append("; efficient path = ").Append(string.Join(" → ", scenario.IdealPath))
                .Append("; this check's value = ").Append(test.Value).Append("; teaching point = ").Append(test.Teaches);
            var visible = $"Scenario: {scenario.Vehicle}, complaint \"{scenario.Complaint}\", codes {string.Join(", ", scenario.Codes)}.\n" +
                          $"Checks performed so far: {string.Join(", ", state.PerformedTests)}.\n" +
                          $"The apprentice said: \"{input}\" → performed \"{test.Title}\" and observed: {test.Result}\n" +
                          "Give brief coaching feedback on this choice and ask one Socratic question about what to do next.";
            var completion = await route.Model!.CompleteAsync(new ChatRequest
            {
                SystemPrompt = Prompts.ApprenticeTutor + "\n\n" + hidden,
                Messages = [ChatMessage.User(visible)],
                Temperature = 0.3,
                MaxOutputTokens = 400,
            }, ct);
            var text = completion.Text.Trim();

            // Guardrail: never let the tutor leak the root cause before submission.
            var leaked = scenario.RootCauseKeywords.Count(k => k.Length > 3 && text.Contains(k, StringComparison.OrdinalIgnoreCase)) >= 2;
            return string.IsNullOrWhiteSpace(text) || leaked ? null : text;
        }
        catch (Exception ex) when (ex is ExternalServiceException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<bool?> AiJudgeAsync(ScenarioContent scenario, string diagnosis, CancellationToken ct)
    {
        try
        {
            var route = await router.ResolveChatAsync(AiTask.Training, DataSensitivity.General, cancellationToken: ct);
            if (!route.IsAvailable) return null;
            var completion = await route.Model!.CompleteAsync(new ChatRequest
            {
                SystemPrompt = "Decide whether an apprentice's diagnosis identifies the same root cause as the answer key. Minor wording differences are fine; a different component or a vague system-level answer is not. Reply with only YES or NO.",
                Messages = [ChatMessage.User($"Answer key: {scenario.RootCause}\nApprentice: {diagnosis}")],
                Temperature = 0,
                MaxOutputTokens = 1024,
            }, ct);
            return completion.Text.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
        }
        catch (ExternalServiceException)
        {
            return null;
        }
    }

    private static int MaxScore(ScenarioContent scenario) =>
        scenario.IdealPath.Select(k => scenario.Tests.FirstOrDefault(t => t.Key == k)?.Points ?? 0).Where(p => p > 0).Sum();

    private async Task<(TrainingAttempt? Attempt, ScenarioContent? Scenario, AttemptState State, IAppDbContext Db)> LoadAsync(Guid attemptId, CancellationToken ct)
    {
        var db = await dbFactory.CreateAsync(ct);
        var attempt = await db.TrainingAttempts.FirstOrDefaultAsync(a => a.Id == attemptId && a.Kind == TrainingAttemptKind.Scenario, ct);
        if (attempt is null) return (null, null, new AttemptState(), db);
        var entity = await db.TrainingScenarios.AsNoTracking().FirstOrDefaultAsync(s => s.Key == attempt.ReferenceKey, ct);
        var scenario = entity is null ? null : Json.Deserialize<ScenarioContent>(entity.DefinitionJson);
        var state = Json.Deserialize<AttemptState>(attempt.DetailsJson) ?? new AttemptState();
        return (attempt, scenario, state, db);
    }
}
