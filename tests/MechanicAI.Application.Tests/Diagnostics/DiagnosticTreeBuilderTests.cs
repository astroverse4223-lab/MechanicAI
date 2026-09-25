using MechanicAI.Application.Content;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Tests.Diagnostics;

public class DiagnosticTreeBuilderTests
{
    private static BuiltTree Build(IReadOnlyList<PlaybookDefinition> playbooks, string[] dtcs, string symptoms, int? mileage = null)
    {
        var matches = PlaybookMatcher.Match(playbooks, dtcs, symptoms);
        return DiagnosticTreeBuilder.Build(matches, new TreeContext(dtcs, symptoms, mileage));
    }

    [Fact]
    public void Build_AppliesModifiersAndMatchStrengthToPriors()
    {
        var tree = Build(TestPlaybooks.All(), ["P0302", "P0351"], "misfire in the rain", 120_000);

        // Match score 1.35 (one DTC + one keyword) scales priors by 0.6 + 1.35 * 0.4 = 1.14.
        var coil = tree.Causes.Single(c => c.Definition.Key == "ignition-coil");
        Assert.Equal(0.3 * 3.0 * 1.2 * 1.5 * 1.14, coil.Prior, 9);
        Assert.Equal(3, coil.AppliedModifiers.Count);
        Assert.Contains("Coil circuit code present (×3)", coil.AppliedModifiers);
        Assert.Contains(coil.AppliedModifiers, m => m.Contains("mileage-over", StringComparison.Ordinal));
        Assert.Equal(DiagnosticCategory.Ignition, coil.Category);
        Assert.Equal("misfire", coil.PlaybookKey);

        var plug = tree.Causes.Single(c => c.Definition.Key == "spark-plug");
        Assert.Equal(0.2 * 1.14, plug.Prior, 9);
        Assert.Empty(plug.AppliedModifiers);
        Assert.Equal(DiagnosticCategory.Ignition, plug.Category); // "ignition" parsed case-insensitively
    }

    [Fact]
    public void Build_OrdersCausesByPrior()
    {
        var tree = Build(TestPlaybooks.All(), ["P0302"], string.Empty);

        Assert.Equal(["ignition-coil", "spark-plug", "vacuum-leak"], tree.Causes.Select(c => c.Definition.Key));
        Assert.False(tree.IsEmpty);
    }

    [Fact]
    public void Build_IncludesOnlyTestsRelatedToCausesInTheTree()
    {
        var tree = Build(TestPlaybooks.All(), ["P0302"], string.Empty);

        var keys = tree.Tests.Select(t => t.Definition.Key).ToList();
        Assert.Equal(["coil-swap", "smoke-test", "generic-test"], keys);
        Assert.Equal(["ignition-coil"], tree.Tests[0].RelatedCauseKeys);
        Assert.Empty(tree.Tests[2].RelatedCauseKeys);
    }

    [Fact]
    public void Build_SkipsTestsWhoseRequiredCausesAreMissing()
    {
        var tree = Build([TestPlaybooks.Lean()], ["P0171"], string.Empty);

        Assert.DoesNotContain(tree.Tests, t => t.Definition.Key == "maf-sensor-swap");
        Assert.Contains(tree.Tests, t => t.Definition.Key == "smoke-test");
    }

    [Fact]
    public void Build_MergesSharedCausesAndTestsAcrossPlaybooks()
    {
        var misfire = TestPlaybooks.Misfire();
        var lean = TestPlaybooks.Lean();
        var tree = Build([misfire, lean], ["P0302", "P0171"], string.Empty);

        // Shared cause: the stronger prior wins (both playbooks matched with score 1.0).
        var vacuum = Assert.Single(tree.Causes, c => c.Definition.Key == "vacuum-leak");
        Assert.Equal(0.4, vacuum.Prior, 9);
        Assert.Equal(4, tree.Causes.Count);

        // Shared test: first definition wins, likelihoods for new causes are merged in.
        var smoke = Assert.Single(tree.Tests, t => t.Definition.Key == "smoke-test");
        Assert.Equal("Smoke test the intake", smoke.Definition.Title);
        Assert.Equal(["found", "none"], smoke.Definition.Outcomes.Select(o => o.Key));
        var found = smoke.Definition.Outcomes[0].Likelihoods!;
        Assert.Equal(0.9, found["vacuum-leak"]);
        Assert.Equal(0.05, found["maf-contaminated"]);
        Assert.Equal(["vacuum-leak", "maf-contaminated"], smoke.Definition.RequiresAnyCause);
        Assert.Equal(["maf-contaminated", "vacuum-leak"], smoke.RelatedCauseKeys.Order(StringComparer.Ordinal));

        // The source playbook definitions are not mutated by the merge.
        var original = misfire.Tests.Single(t => t.Key == "smoke-test");
        Assert.False(original.Outcomes[0].Likelihoods!.ContainsKey("maf-contaminated"));
        Assert.Equal(["vacuum-leak"], original.RequiresAnyCause);
    }

    [Fact]
    public void Build_DeduplicatesQuestionsAndVerificationCaseInsensitively()
    {
        var tree = Build([TestPlaybooks.Misfire(), TestPlaybooks.Lean()], ["P0302", "P0171"], string.Empty);

        Assert.Equal(["Does it misfire cold or hot?", "Any recent ignition work?", "Is it worse at idle?"], tree.ClarifyingQuestions);
        Assert.Equal(["Clear codes.", "Road test.", "Check fuel trims."], tree.VerificationSteps);
    }

    [Fact]
    public void Build_EmptyWhenNothingMatched()
    {
        var tree = DiagnosticTreeBuilder.Build([], new TreeContext([], string.Empty, null));

        Assert.True(tree.IsEmpty);
        Assert.Empty(tree.Tests);
    }

    [Fact]
    public void ApplyModifiers_HandlesEachModifierKind()
    {
        var cause = new CauseDefinition
        {
            Prior = 0.5,
            Modifiers =
            [
                new CauseModifier { When = "DTC", Match = "^P0171$", Factor = 2 },
                new CauseModifier { When = "mileage-under", Value = 50_000, Factor = 0.5 },
                new CauseModifier { When = "mileage-over", Value = 10_000, Factor = 3 },
                new CauseModifier { When = "symptom", Match = "cold", Factor = 0 },
                new CauseModifier { When = "unknown-kind", Factor = 10 },
                new CauseModifier { When = "dtc", Match = null, Factor = 10 },
            ],
        };

        var (prior, applied) = DiagnosticTreeBuilder.ApplyModifiers(cause, new TreeContext(["P0171"], "only when cold", 30_000));

        Assert.Equal(0.5 * 2 * 0.5 * 3, prior, 9);
        Assert.Equal(3, applied.Count);
    }

    [Fact]
    public void ApplyModifiers_FloorsPrior()
    {
        var (prior, _) = DiagnosticTreeBuilder.ApplyModifiers(new CauseDefinition { Prior = 0 }, new TreeContext([], string.Empty, null));
        Assert.Equal(0.001, prior);
    }

    [Theory]
    [InlineData("Ignition", DiagnosticCategory.Ignition)]
    [InlineData("airintake", DiagnosticCategory.AirIntake)]
    [InlineData("HighVoltage", DiagnosticCategory.HighVoltage)]
    [InlineData("nonsense", DiagnosticCategory.Other)]
    [InlineData(null, DiagnosticCategory.Other)]
    public void ParseCategory_FallsBackToOther(string? input, DiagnosticCategory expected)
    {
        Assert.Equal(expected, DiagnosticTreeBuilder.ParseCategory(input));
    }

    [Fact]
    public void ToEntity_SanitizesAndClampsDefinition()
    {
        var tree = Build(TestPlaybooks.All(), ["P0302"], string.Empty);
        var sessionId = Guid.NewGuid();

        var coil = DiagnosticTreeBuilder.ToEntity(tree.Tests.Single(t => t.Definition.Key == "coil-swap"), sessionId);
        var smoke = DiagnosticTreeBuilder.ToEntity(tree.Tests.Single(t => t.Definition.Key == "smoke-test"), sessionId);
        var generic = DiagnosticTreeBuilder.ToEntity(tree.Tests.Single(t => t.Definition.Key == "generic-test"), sessionId);

        Assert.Equal(sessionId, coil.SessionId);
        Assert.Equal(["rotating", "ignition-voltage"], coil.SafetyTags);
        Assert.Equal(20, coil.EstimatedMinutes);
        Assert.Equal(3, coil.Outcomes.Count);
        Assert.True(coil.Outcomes[2].Inconclusive);
        Assert.Empty(coil.Outcomes[2].Likelihoods);
        Assert.Equal(ActorKind.System, coil.Origin);
        Assert.Equal("Playbook: misfire", coil.OriginDetail);
        Assert.Equal(EvidenceClass.SourceDerived, coil.Evidence);
        var source = Assert.Single(coil.Sources);
        Assert.Equal(SourceType.BuiltInReference, source.Type);

        Assert.Equal(5, smoke.Difficulty);
        Assert.Equal(1, smoke.Invasiveness);
        Assert.Equal(15, generic.EstimatedMinutes);
    }
}
