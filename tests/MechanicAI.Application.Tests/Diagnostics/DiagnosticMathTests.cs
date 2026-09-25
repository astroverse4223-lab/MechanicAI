using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Entities;

namespace MechanicAI.Application.Tests.Diagnostics;

public class DiagnosticMathTests
{
    internal static TestOutcomeDefinition Outcome(string key, params (string Cause, double Value)[] likelihoods) => new()
    {
        Key = key,
        Label = key,
        Likelihoods = likelihoods.ToDictionary(l => l.Cause, l => l.Value, StringComparer.Ordinal),
    };

    internal static DiagnosticTest Test(params TestOutcomeDefinition[] outcomes) => new() { Key = "t", Outcomes = [.. outcomes] };

    [Fact]
    public void Likelihood_UsesSpecifiedAndDefaultValues()
    {
        var moved = Outcome("moved", ("coil", 0.9), ("*", 0.1));
        var stayed = Outcome("stayed", ("coil", 0.1), ("*", 0.9));
        var test = Test(moved, stayed);

        Assert.Equal(0.9, DiagnosticMath.Likelihood(test, moved, "coil"), 9);
        Assert.Equal(0.1, DiagnosticMath.Likelihood(test, moved, "plug"), 9);
        Assert.Equal(0.9, DiagnosticMath.Likelihood(test, stayed, "plug"), 9);
    }

    [Fact]
    public void Likelihood_InconclusiveOutcomeHasNoEffect()
    {
        var inconclusive = new TestOutcomeDefinition { Key = "x", Inconclusive = true };
        var test = Test(Outcome("a", ("coil", 0.9)), Outcome("b", ("coil", 0.1)), inconclusive);

        Assert.Equal(1.0, DiagnosticMath.Likelihood(test, inconclusive, "coil"));
    }

    [Fact]
    public void Likelihood_IsUniformWhenTestSaysNothingAboutCause()
    {
        var a = Outcome("a", ("coil", 0.9));
        var b = Outcome("b", ("coil", 0.1));
        var c = Outcome("c", ("coil", 0.0));

        Assert.Equal(1.0 / 3, DiagnosticMath.Likelihood(Test(a, b, c), a, "plug"), 9);
    }

    [Fact]
    public void Likelihood_FillsMissingOutcomesWithRemainingMass()
    {
        var a = Outcome("a", ("coil", 0.7));
        var b = Outcome("b");

        Assert.Equal(0.7, DiagnosticMath.Likelihood(Test(a, b), a, "coil"), 9);
        Assert.Equal(0.3, DiagnosticMath.Likelihood(Test(a, b), b, "coil"), 9);
    }

    [Fact]
    public void Likelihood_NormalizesAcrossDecisiveOutcomes()
    {
        var a = Outcome("a", ("coil", 0.9));
        var b = Outcome("b", ("coil", 0.9));

        Assert.Equal(0.5, DiagnosticMath.Likelihood(Test(a, b), a, "coil"), 9);
    }

    [Fact]
    public void Likelihood_IsFlooredSoNoCauseBecomesImpossible()
    {
        var a = Outcome("a", ("coil", 0.0));
        var b = Outcome("b", ("coil", 1.0));

        var value = DiagnosticMath.Likelihood(Test(a, b), a, "coil");

        Assert.Equal(DiagnosticMath.LikelihoodFloor / (1.0 + DiagnosticMath.LikelihoodFloor), value, 9);
        Assert.True(value > 0);
    }

    [Fact]
    public void Likelihood_UniformWhenTestHasNoDecisiveOutcomes()
    {
        var only = Outcome("only", ("coil", 0.9));
        Assert.Equal(1.0, DiagnosticMath.Likelihood(Test(only), only, "coil"), 9);
    }

    [Fact]
    public void NormalizePriors_ScalesToModeledMassAndAddsUnlisted()
    {
        var priors = DiagnosticMath.NormalizePriors(
        [
            new("a", 3.0),
            new("b", 1.0),
            new("b", 0.5),
            new(DiagnosticMath.UnlistedCauseKey, 0.9),
        ]);

        Assert.Equal(3, priors.Count);
        Assert.Equal(0.75 * 0.94, priors["a"], 9);
        Assert.Equal(0.25 * 0.94, priors["b"], 9);
        Assert.Equal(DiagnosticMath.UnlistedCausePrior, priors[DiagnosticMath.UnlistedCauseKey], 9);
        Assert.Equal(1.0, priors.Values.Sum(), 9);
    }

    [Fact]
    public void NormalizePriors_EmptyGivesAllMassToUnlisted()
    {
        var priors = DiagnosticMath.NormalizePriors([]);

        Assert.Equal(1.0, Assert.Single(priors).Value);
    }

    [Fact]
    public void NormalizePriors_FloorsZeroPriors()
    {
        var priors = DiagnosticMath.NormalizePriors([new("a", 0.0), new("b", 1.0)]);

        Assert.True(priors["a"] > 0);
    }

    [Fact]
    public void Posterior_AppliesBayesRule()
    {
        var moved = Outcome("moved", ("coil", 0.9), ("*", 0.1));
        var test = Test(moved, Outcome("stayed", ("coil", 0.1), ("*", 0.9)));
        var priors = new Dictionary<string, double> { ["coil"] = 0.5, ["plug"] = 0.5 };

        var posterior = DiagnosticMath.Posterior(priors, [(test, moved)]);

        Assert.Equal(0.9, posterior["coil"], 9);
        Assert.Equal(0.1, posterior["plug"], 9);
    }

    [Fact]
    public void Posterior_SequentialEvidenceCompounds()
    {
        var moved = Outcome("moved", ("coil", 0.9), ("*", 0.1));
        var test = Test(moved, Outcome("stayed", ("coil", 0.1), ("*", 0.9)));
        var priors = new Dictionary<string, double> { ["coil"] = 0.5, ["plug"] = 0.5 };

        var posterior = DiagnosticMath.Posterior(priors, [(test, moved), (test, moved)]);

        Assert.Equal(0.81 / 0.82, posterior["coil"], 9);
    }

    [Fact]
    public void Posterior_ExcludedCausesGetZeroMass()
    {
        var priors = new Dictionary<string, double> { ["coil"] = 0.5, ["plug"] = 0.3, ["leak"] = 0.2 };

        var posterior = DiagnosticMath.Posterior(priors, [], new HashSet<string> { "coil" });

        Assert.Equal(0, posterior["coil"]);
        Assert.Equal(0.6, posterior["plug"], 9);
        Assert.Equal(0.4, posterior["leak"], 9);
    }

    [Fact]
    public void Posterior_IgnoresInconclusiveEvidence()
    {
        var inconclusive = new TestOutcomeDefinition { Key = "x", Inconclusive = true };
        var test = Test(Outcome("a", ("coil", 0.9)), Outcome("b", ("coil", 0.1)), inconclusive);
        var priors = new Dictionary<string, double> { ["coil"] = 0.25, ["plug"] = 0.75 };

        var posterior = DiagnosticMath.Posterior(priors, [(test, inconclusive)]);

        Assert.Equal(0.25, posterior["coil"], 9);
    }

    [Fact]
    public void Normalize_FallsBackToUniformWhenAllZero()
    {
        var p = new Dictionary<string, double> { ["a"] = 0, ["b"] = 0 };

        DiagnosticMath.Normalize(p);

        Assert.Equal(0.5, p["a"]);
        Assert.Equal(0.5, p["b"]);
    }

    [Fact]
    public void Entropy_InBits()
    {
        Assert.Equal(1.0, DiagnosticMath.Entropy([0.5, 0.5]), 9);
        Assert.Equal(2.0, DiagnosticMath.Entropy([0.25, 0.25, 0.25, 0.25]), 9);
        Assert.Equal(0.0, DiagnosticMath.Entropy([1.0, 0.0]), 9);
    }

    [Fact]
    public void ExpectedInformationGain_HighForDiscriminatingTestZeroForUseless()
    {
        var belief = new Dictionary<string, double> { ["coil"] = 0.5, ["plug"] = 0.5 };
        var discriminating = Test(Outcome("a", ("coil", 1.0), ("plug", 0.0)), Outcome("b", ("coil", 0.0), ("plug", 1.0)));
        var useless = Test(Outcome("a", ("*", 0.5)), Outcome("b", ("*", 0.5)));
        var single = Test(Outcome("a", ("coil", 1.0)));

        var gain = DiagnosticMath.ExpectedInformationGain(belief, discriminating);

        Assert.InRange(gain, 0.9, 1.0);
        Assert.Equal(0.0, DiagnosticMath.ExpectedInformationGain(belief, useless), 9);
        Assert.Equal(0.0, DiagnosticMath.ExpectedInformationGain(belief, single));
    }

    [Fact]
    public void ExpectedInformationGain_IsLowWhenBeliefIsAlreadyCertain()
    {
        var discriminating = Test(Outcome("a", ("coil", 1.0), ("plug", 0.0)), Outcome("b", ("coil", 0.0), ("plug", 1.0)));
        var certain = new Dictionary<string, double> { ["coil"] = 1.0, ["plug"] = 0.0 };

        Assert.Equal(0.0, DiagnosticMath.ExpectedInformationGain(certain, discriminating), 9);
    }

    [Fact]
    public void OutcomeProbabilities_SumToOne()
    {
        var belief = new Dictionary<string, double> { ["coil"] = 0.7, ["plug"] = 0.3 };
        var test = Test(
            Outcome("a", ("coil", 0.8), ("*", 0.2)),
            Outcome("b", ("coil", 0.2), ("*", 0.8)),
            new TestOutcomeDefinition { Key = "skip", Inconclusive = true });

        var probabilities = DiagnosticMath.OutcomeProbabilities(belief, test);

        Assert.Equal(2, probabilities.Count);
        Assert.Equal(0.7 * 0.8 + 0.3 * 0.2, probabilities[0].Probability, 9);
        Assert.Equal(1.0, probabilities.Sum(p => p.Probability), 9);
    }

    [Fact]
    public void Cost_PenalizesTimeDifficultyAndInvasiveness()
    {
        Assert.Equal(1.0 + 15 / 20.0, DiagnosticMath.Cost(new DiagnosticTest()), 9);
        Assert.Equal(1.0 + 60 / 20.0 + 4 * 0.5 + 4 * 0.75, DiagnosticMath.Cost(new DiagnosticTest { EstimatedMinutes = 60, Difficulty = 5, Invasiveness = 5 }), 9);
        // Out-of-range values are clamped.
        Assert.Equal(1.0 + 1 / 20.0, DiagnosticMath.Cost(new DiagnosticTest { EstimatedMinutes = 0, Difficulty = 0, Invasiveness = -3 }), 9);
        Assert.Equal(1.0 + 600 / 20.0 + 2 + 3, DiagnosticMath.Cost(new DiagnosticTest { EstimatedMinutes = 10_000, Difficulty = 99, Invasiveness = 99 }), 9);
    }
}
