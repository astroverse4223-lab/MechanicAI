using MechanicAI.Application.Content;

namespace MechanicAI.Application.Tests.Diagnostics;

/// <summary>Small in-memory playbooks shaped like Content/Playbooks/*.json.</summary>
internal static class TestPlaybooks
{
    public static PlaybookDefinition Misfire() => new()
    {
        Key = "misfire",
        Title = "Single-cylinder misfire",
        Triggers = new PlaybookTriggers
        {
            DtcPatterns = ["^P030[0-9]$"],
            SymptomKeywords = ["misfire", "rough idle"],
        },
        ClarifyingQuestions = ["Does it misfire cold or hot?", "Any recent ignition work?"],
        Causes =
        [
            new CauseDefinition
            {
                Key = "ignition-coil",
                Title = "Failed ignition coil",
                Category = "Ignition",
                Prior = 0.3,
                Description = "Coil breaks down under load.",
                Modifiers =
                [
                    new CauseModifier { When = "dtc", Match = "^P035[1-9]$", Factor = 3.0, Reason = "Coil circuit code present" },
                    new CauseModifier { When = "mileage-over", Value = 100000, Factor = 1.2 },
                    new CauseModifier { When = "symptom", Match = "rain|wet", Factor = 1.5, Reason = "Moisture" },
                ],
                Safety = ["ignition-voltage"],
            },
            new CauseDefinition { Key = "spark-plug", Title = "Worn spark plug", Category = "ignition", Prior = 0.2 },
            new CauseDefinition { Key = "vacuum-leak", Title = "Vacuum leak", Category = "Vacuum", Prior = 0.1, Safety = ["rotating", "not-a-tag"] },
        ],
        Tests =
        [
            new TestDefinition
            {
                Key = "coil-swap",
                Title = "Swap the coil",
                Minutes = 20,
                Safety = ["Rotating", "ignition-voltage", "bogus", "rotating"],
                RequiresAnyCause = ["ignition-coil"],
                Outcomes =
                [
                    Outcome("moved", false, ("ignition-coil", 0.95), ("*", 0.05)),
                    Outcome("stayed", true, ("ignition-coil", 0.05), ("*", 0.95)),
                    new OutcomeDefinition { Key = "inconclusive", Label = "Could not reproduce", Inconclusive = true },
                ],
            },
            new TestDefinition
            {
                Key = "smoke-test",
                Title = "Smoke test the intake",
                Minutes = 30,
                Difficulty = 9,
                Invasiveness = 0,
                RequiresAnyCause = ["vacuum-leak"],
                Outcomes =
                [
                    Outcome("found", false, ("vacuum-leak", 0.9), ("*", 0.05)),
                    Outcome("none", true, ("vacuum-leak", 0.1), ("*", 0.95)),
                ],
            },
            new TestDefinition
            {
                Key = "orphan-test",
                Title = "Only about causes that are not in the tree",
                Outcomes =
                [
                    Outcome("a", false, ("some-other-cause", 0.9)),
                    Outcome("b", true, ("some-other-cause", 0.1)),
                ],
            },
            new TestDefinition
            {
                Key = "generic-test",
                Title = "Default-only likelihoods",
                Minutes = 0,
                Outcomes =
                [
                    Outcome("a", false, ("*", 0.5)),
                    Outcome("b", true, ("*", 0.5)),
                ],
            },
        ],
        Verification = ["Clear codes.", "Road test."],
    };

    public static PlaybookDefinition Lean() => new()
    {
        Key = "lean",
        Title = "System too lean",
        Triggers = new PlaybookTriggers
        {
            DtcPatterns = ["^P017[14]$"],
            SymptomKeywords = ["hesitat", "lean"],
        },
        ClarifyingQuestions = ["ANY RECENT IGNITION WORK?", "Is it worse at idle?"],
        Causes =
        [
            new CauseDefinition { Key = "vacuum-leak", Title = "Vacuum leak", Category = "Vacuum", Prior = 0.4 },
            new CauseDefinition { Key = "maf-contaminated", Title = "Contaminated MAF", Category = "AirIntake", Prior = 0.2 },
        ],
        Tests =
        [
            new TestDefinition
            {
                Key = "smoke-test",
                Title = "Smoke test the intake (lean playbook copy)",
                Minutes = 30,
                RequiresAnyCause = ["maf-contaminated"],
                Outcomes =
                [
                    Outcome("found", false, ("vacuum-leak", 0.5), ("maf-contaminated", 0.05)),
                    Outcome("none", true, ("vacuum-leak", 0.5), ("maf-contaminated", 0.95)),
                    Outcome("partial", false, ("vacuum-leak", 0.5)),
                ],
            },
            new TestDefinition
            {
                Key = "maf-sensor-swap",
                Title = "Substitute a known-good MAF",
                RequiresAnyCause = ["maf-sensor-failed"],
                Outcomes =
                [
                    Outcome("better", false, ("maf-contaminated", 0.8)),
                    Outcome("same", true, ("maf-contaminated", 0.2)),
                ],
            },
        ],
        Verification = ["clear codes.", "Check fuel trims."],
    };

    public static PlaybookDefinition Stalling() => new()
    {
        Key = "stalling",
        Title = "Stalling (no codes)",
        Triggers = new PlaybookTriggers { SymptomKeywords = ["stall", "dies"] },
        Causes = [new CauseDefinition { Key = "idle-air", Title = "Idle air control", Category = "AirIntake", Prior = 0.3 }],
    };

    public static IReadOnlyList<PlaybookDefinition> All() => [Misfire(), Lean(), Stalling()];

    public static OutcomeDefinition Outcome(string key, bool normal, params (string Cause, double Value)[] likelihoods) => new()
    {
        Key = key,
        Label = key,
        Normal = normal,
        Likelihoods = likelihoods.ToDictionary(l => l.Cause, l => l.Value, StringComparer.Ordinal),
    };
}
