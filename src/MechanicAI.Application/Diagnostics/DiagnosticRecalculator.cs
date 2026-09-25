using MechanicAI.Application.Settings;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Diagnostics;

public sealed record CauseStatusChange(DiagnosticNode Node, CauseStatus From, CauseStatus To, string Reason);

public sealed record TestRanking(DiagnosticTest Test, double InformationGain, double Score);

public sealed record RecalculationResult(
    IReadOnlyList<CauseStatusChange> StatusChanges,
    IReadOnlyList<TestRanking> Ranking,
    DiagnosticNode? LeadingCause,
    bool EvidenceOutsideTree);

/// <summary>
/// Recomputes cause probabilities, cause statuses, and the test ranking for a session from
/// its priors and every recorded (non-reverted) result. Recomputing from scratch — rather than
/// updating incrementally — is what makes "go back a step" exact: reverting a result simply
/// removes it from the evidence.
/// </summary>
public static class DiagnosticRecalculator
{
    /// <summary>Tests below this expected information gain (bits) are not recommended.</summary>
    public const double MinimumUsefulGain = 0.02;

    public static RecalculationResult Recalculate(DiagnosticSession session, DiagnosticsSettings settings)
    {
        var causes = session.Nodes.Where(n => n.Kind == DiagnosticNodeKind.Cause).ToList();
        var byKey = causes.GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var priors = DiagnosticMath.NormalizePriors(
            causes.Where(c => c.Key != DiagnosticMath.UnlistedCauseKey)
                  .Select(c => new KeyValuePair<string, double>(c.Key, c.PriorProbability)));

        var excluded = causes
            .Where(c => c.IsManualStatus && c.Status == CauseStatus.RuledOut)
            .Select(c => c.Key)
            .ToHashSet(StringComparer.Ordinal);

        var evidence = session.Tests
            .Where(t => t.Status == TestStatus.Completed && t.SelectedOutcome is not null)
            .OrderBy(t => t.ExecutionOrder ?? int.MaxValue)
            .Select(t => (t, t.SelectedOutcome!))
            .ToList();

        var posterior = DiagnosticMath.Posterior(priors, evidence, excluded);
        var baseline = DiagnosticMath.Posterior(priors, [], excluded);

        var changes = new List<CauseStatusChange>();
        foreach (var node in causes)
        {
            var p = posterior.GetValueOrDefault(node.Key);
            var prior = baseline.GetValueOrDefault(node.Key);
            node.Probability = Math.Round(p, 6);

            if (node.IsManualStatus || node.Status == CauseStatus.Confirmed) continue;

            var touched = evidence.Any(e => Math.Abs(DiagnosticMath.Likelihood(e.Item1, e.Item2, node.Key) - AverageLikelihood(e.Item1, e.Item2)) > 1e-9);
            var ratio = prior > 0 ? p / prior : 1;
            CauseStatus next;
            string reason;
            if (node.Key == DiagnosticMath.UnlistedCauseKey)
            {
                next = p >= 0.35 ? CauseStatus.Suspected : CauseStatus.Open;
                reason = p >= 0.35
                    ? "Test results do not fit the causes in this tree well. Re-check results, research the vehicle, or run AI analysis."
                    : string.Empty;
            }
            else if (touched && p >= settings.IsolationThreshold)
            {
                next = CauseStatus.Likely;
                reason = $"Evidence strongly supports this cause ({p:P0}). Confirm before repairing.";
            }
            else if (touched && p < settings.RuleOutThreshold && ratio < 0.25)
            {
                next = CauseStatus.RuledOut;
                reason = "Test results argue against this cause.";
            }
            else if (touched && p >= 0.35 && ratio >= 1.5)
            {
                next = CauseStatus.Suspected;
                reason = "Test results point toward this cause.";
            }
            else
            {
                next = CauseStatus.Open;
                reason = string.Empty;
            }

            if (next != node.Status)
            {
                changes.Add(new CauseStatusChange(node, node.Status, next, reason));
                node.Status = next;
                node.StatusReason = string.IsNullOrEmpty(reason) ? null : reason;
            }
        }

        var ranking = RankTests(session, posterior);
        var top = ranking.FirstOrDefault();
        foreach (var test in session.Tests)
        {
            if (test.Status is TestStatus.Completed or TestStatus.Skipped) continue;
            var rank = ranking.FirstOrDefault(r => ReferenceEquals(r.Test, test));
            test.InformationGain = rank is null ? 0 : Math.Round(rank.InformationGain, 4);
            test.Score = rank is null ? 0 : Math.Round(rank.Score, 4);
            test.Status = top is not null && ReferenceEquals(top.Test, test) ? TestStatus.Recommended : TestStatus.Available;
        }

        var leading = causes
            .Where(c => c.Key != DiagnosticMath.UnlistedCauseKey && c.Status != CauseStatus.RuledOut)
            .OrderByDescending(c => c.Status == CauseStatus.Confirmed)
            .ThenByDescending(c => c.Probability)
            .FirstOrDefault();

        var outside = posterior.GetValueOrDefault(DiagnosticMath.UnlistedCauseKey) >= 0.35;
        return new RecalculationResult(changes, ranking, leading, outside);
    }

    /// <summary>Ranks unperformed tests by expected information gain per unit cost.</summary>
    public static IReadOnlyList<TestRanking> RankTests(DiagnosticSession session, IReadOnlyDictionary<string, double> belief)
    {
        var ranking = new List<TestRanking>();
        foreach (var test in session.Tests)
        {
            if (test.Status is TestStatus.Completed or TestStatus.Skipped) continue;
            if (test.Outcomes.Count(o => !o.Inconclusive) < 2) continue;

            var gain = DiagnosticMath.ExpectedInformationGain(belief, test);
            if (gain < MinimumUsefulGain) continue;
            ranking.Add(new TestRanking(test, gain, gain / DiagnosticMath.Cost(test)));
        }

        return ranking.OrderByDescending(r => r.Score).ThenBy(r => r.Test.EstimatedMinutes).ToList();
    }

    /// <summary>Current belief over causes (for ranking outside a full recalculation).</summary>
    public static Dictionary<string, double> CurrentBelief(DiagnosticSession session)
    {
        return session.Nodes
            .Where(n => n.Kind == DiagnosticNodeKind.Cause)
            .GroupBy(n => n.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Probability, StringComparer.Ordinal);
    }

    private static double AverageLikelihood(DiagnosticTest test, TestOutcomeDefinition outcome)
    {
        var decisive = test.Outcomes.Count(o => !o.Inconclusive);
        return decisive == 0 ? 1 : 1.0 / decisive;
    }
}
