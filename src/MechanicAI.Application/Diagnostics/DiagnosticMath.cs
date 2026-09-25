using MechanicAI.Domain.Entities;

namespace MechanicAI.Application.Diagnostics;

/// <summary>
/// Probabilistic core of the diagnostic engine.
///
/// Causes are treated as mutually exclusive hypotheses (single-fault assumption) plus an
/// explicit "cause not in the tree" hypothesis, so evidence that contradicts every modeled
/// cause shows up as rising probability of an unmodeled cause instead of being forced onto
/// the least-bad listed one.
///
/// Each test outcome carries P(outcome | cause). Recording an outcome multiplies every
/// cause's probability by that likelihood and renormalizes (Bayes' rule). Tests are ranked
/// by expected information gain (bits of entropy removed) divided by cost.
/// </summary>
public static class DiagnosticMath
{
    public const string UnlistedCauseKey = "unlisted-cause";

    public const string DefaultLikelihoodKey = "*";

    /// <summary>Prior weight of the "not in the tree" hypothesis, relative to normalized modeled priors.</summary>
    public const double UnlistedCausePrior = 0.06;

    /// <summary>Likelihoods are floored so a single (possibly mis-recorded) result can never make a cause impossible.</summary>
    public const double LikelihoodFloor = 0.01;

    /// <summary>
    /// P(outcome | cause), normalized across the test's decisive (non-inconclusive) outcomes.
    /// Returns 1 for inconclusive outcomes (no update).
    /// </summary>
    public static double Likelihood(DiagnosticTest test, TestOutcomeDefinition outcome, string causeKey)
    {
        if (outcome.Inconclusive) return 1.0;

        var decisive = test.Outcomes.Where(o => !o.Inconclusive).ToList();
        if (decisive.Count == 0) return 1.0;

        var raw = new double[decisive.Count];
        var specifiedSum = 0.0;
        var missing = 0;
        for (var i = 0; i < decisive.Count; i++)
        {
            var value = RawLikelihood(decisive[i], causeKey);
            if (double.IsNaN(value))
            {
                missing++;
            }
            else
            {
                specifiedSum += value;
            }

            raw[i] = value;
        }

        if (missing == decisive.Count)
        {
            // The test says nothing about this cause: uniform, i.e. no information.
            return 1.0 / decisive.Count;
        }

        var fill = missing == 0 ? 0 : Math.Max(LikelihoodFloor, (1.0 - specifiedSum) / missing);
        var total = 0.0;
        var index = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            if (double.IsNaN(raw[i])) raw[i] = fill;
            raw[i] = Math.Max(LikelihoodFloor, raw[i]);
            total += raw[i];
            if (ReferenceEquals(decisive[i], outcome) || decisive[i].Key == outcome.Key) index = i;
        }

        return index < 0 || total <= 0 ? 1.0 / decisive.Count : raw[index] / total;
    }

    private static double RawLikelihood(TestOutcomeDefinition outcome, string causeKey)
    {
        if (outcome.Likelihoods.TryGetValue(causeKey, out var v)) return v;
        if (outcome.Likelihoods.TryGetValue(DefaultLikelihoodKey, out var d)) return d;
        return double.NaN;
    }

    /// <summary>Normalizes priors and adds the unlisted-cause hypothesis if absent.</summary>
    public static Dictionary<string, double> NormalizePriors(IEnumerable<KeyValuePair<string, double>> priors)
    {
        var dict = priors
            .Where(p => p.Key != UnlistedCauseKey)
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Math.Max(1e-6, g.Max(x => x.Value)), StringComparer.Ordinal);
        var sum = dict.Values.Sum();
        if (sum <= 0) sum = 1;
        var modeledMass = 1.0 - UnlistedCausePrior;
        foreach (var key in dict.Keys.ToList())
        {
            dict[key] = dict[key] / sum * modeledMass;
        }

        dict[UnlistedCauseKey] = dict.Count == 0 ? 1.0 : UnlistedCausePrior;
        return dict;
    }

    /// <summary>
    /// Posterior over causes given priors and the recorded evidence, in order.
    /// Causes in <paramref name="excluded"/> (manually ruled out by the technician) get zero mass.
    /// </summary>
    public static Dictionary<string, double> Posterior(
        IReadOnlyDictionary<string, double> priors,
        IEnumerable<(DiagnosticTest Test, TestOutcomeDefinition Outcome)> evidence,
        IReadOnlySet<string>? excluded = null)
    {
        var p = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, prior) in priors)
        {
            p[key] = excluded is not null && excluded.Contains(key) ? 0 : prior;
        }

        foreach (var (test, outcome) in evidence)
        {
            if (outcome.Inconclusive) continue;
            foreach (var key in p.Keys.ToList())
            {
                if (p[key] == 0) continue;
                p[key] *= Likelihood(test, outcome, key);
            }

            Normalize(p);
        }

        Normalize(p);
        return p;
    }

    public static void Normalize(Dictionary<string, double> p)
    {
        var sum = p.Values.Sum();
        if (sum <= 0 || double.IsNaN(sum))
        {
            var live = p.Keys.ToList();
            foreach (var key in live) p[key] = 1.0 / live.Count;
            return;
        }

        foreach (var key in p.Keys.ToList()) p[key] /= sum;
    }

    /// <summary>Shannon entropy in bits.</summary>
    public static double Entropy(IEnumerable<double> probabilities)
    {
        var h = 0.0;
        foreach (var q in probabilities)
        {
            if (q > 1e-12) h -= q * Math.Log2(q);
        }

        return h;
    }

    /// <summary>Probability of observing each decisive outcome under the current belief.</summary>
    public static IReadOnlyList<(TestOutcomeDefinition Outcome, double Probability)> OutcomeProbabilities(
        IReadOnlyDictionary<string, double> belief, DiagnosticTest test)
    {
        var list = new List<(TestOutcomeDefinition, double)>();
        foreach (var outcome in test.Outcomes.Where(o => !o.Inconclusive))
        {
            var po = belief.Sum(kv => kv.Value * Likelihood(test, outcome, kv.Key));
            list.Add((outcome, po));
        }

        return list;
    }

    /// <summary>Expected reduction in entropy (bits) from performing <paramref name="test"/>.</summary>
    public static double ExpectedInformationGain(IReadOnlyDictionary<string, double> belief, DiagnosticTest test)
    {
        var decisive = test.Outcomes.Where(o => !o.Inconclusive).ToList();
        if (decisive.Count < 2) return 0;

        var h0 = Entropy(belief.Values);
        var expected = 0.0;
        foreach (var outcome in decisive)
        {
            var joint = new Dictionary<string, double>(belief.Count, StringComparer.Ordinal);
            var po = 0.0;
            foreach (var (key, prob) in belief)
            {
                var j = prob * Likelihood(test, outcome, key);
                joint[key] = j;
                po += j;
            }

            if (po < 1e-12) continue;
            expected += po * Entropy(joint.Values.Select(j => j / po));
        }

        return Math.Max(0, h0 - expected);
    }

    /// <summary>
    /// Relative cost of a test: time plus difficulty and invasiveness penalties. Quick,
    /// non-invasive tests are strongly preferred ("test before replace", least effort first).
    /// </summary>
    public static double Cost(DiagnosticTest test)
    {
        var minutes = Math.Clamp(test.EstimatedMinutes, 1, 600);
        var difficulty = Math.Clamp(test.Difficulty, 1, 5);
        var invasiveness = Math.Clamp(test.Invasiveness, 1, 5);
        return 1.0 + minutes / 20.0 + (difficulty - 1) * 0.5 + (invasiveness - 1) * 0.75;
    }
}
