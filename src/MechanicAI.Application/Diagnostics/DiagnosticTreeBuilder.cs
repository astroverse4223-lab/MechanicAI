using MechanicAI.Application.Content;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Diagnostics;

public sealed record TreeContext(IReadOnlyList<string> Dtcs, string SymptomText, int? Mileage);

public sealed record BuiltCause(CauseDefinition Definition, DiagnosticCategory Category, double Prior, IReadOnlyList<string> AppliedModifiers, string PlaybookKey);

public sealed record BuiltTest(TestDefinition Definition, IReadOnlyList<string> RelatedCauseKeys, string PlaybookKey);

public sealed record BuiltTree(
    IReadOnlyList<PlaybookMatch> Matches,
    IReadOnlyList<BuiltCause> Causes,
    IReadOnlyList<BuiltTest> Tests,
    IReadOnlyList<string> ClarifyingQuestions,
    IReadOnlyList<string> VerificationSteps)
{
    public bool IsEmpty => Causes.Count == 0;
}

/// <summary>Merges matching playbooks into one set of causes and tests with context-adjusted priors.</summary>
public static class DiagnosticTreeBuilder
{
    public static BuiltTree Build(IReadOnlyList<PlaybookMatch> matches, TreeContext context)
    {
        var causes = new Dictionary<string, BuiltCause>(StringComparer.Ordinal);
        var tests = new Dictionary<string, (TestDefinition Def, HashSet<string> Related, string Playbook)>(StringComparer.Ordinal);
        var questions = new List<string>();
        var verification = new List<string>();

        foreach (var match in matches)
        {
            var playbook = match.Playbook;
            foreach (var cause in playbook.Causes)
            {
                if (string.IsNullOrWhiteSpace(cause.Key)) continue;
                var (prior, applied) = ApplyModifiers(cause, context);

                // Scale by how strongly the playbook matched so a strongly matched (DTC) playbook's
                // causes outweigh a weakly matched (single keyword) one.
                prior *= Math.Min(1.5, 0.6 + match.Score * 0.4);

                if (causes.TryGetValue(cause.Key, out var existing))
                {
                    if (prior > existing.Prior)
                    {
                        causes[cause.Key] = existing with { Prior = prior, AppliedModifiers = existing.AppliedModifiers.Union(applied).ToList() };
                    }
                }
                else
                {
                    causes[cause.Key] = new BuiltCause(cause, ParseCategory(cause.Category), prior, applied, playbook.Key);
                }
            }

            foreach (var test in playbook.Tests)
            {
                if (string.IsNullOrWhiteSpace(test.Key)) continue;
                var related = RelatedCauses(test);
                if (tests.TryGetValue(test.Key, out var existing))
                {
                    existing.Related.UnionWith(related);
                    MergeLikelihoods(existing.Def, test);
                }
                else
                {
                    tests[test.Key] = (CloneTest(test), new HashSet<string>(related, StringComparer.Ordinal), playbook.Key);
                }
            }

            foreach (var q in playbook.ClarifyingQuestions)
            {
                if (!questions.Contains(q, StringComparer.OrdinalIgnoreCase)) questions.Add(q);
            }

            foreach (var v in playbook.Verification)
            {
                if (!verification.Contains(v, StringComparer.OrdinalIgnoreCase)) verification.Add(v);
            }
        }

        var builtTests = new List<BuiltTest>();
        foreach (var (key, value) in tests)
        {
            var related = value.Related.Where(causes.ContainsKey).ToList();
            var requires = value.Def.RequiresAnyCause;
            if (requires.Count > 0 && !requires.Any(causes.ContainsKey)) continue;
            if (related.Count == 0 && requires.Count == 0 && !HasDefaultOnly(value.Def)) continue;
            builtTests.Add(new BuiltTest(value.Def, related, value.Playbook));
        }

        return new BuiltTree(
            matches,
            causes.Values.OrderByDescending(c => c.Prior).ToList(),
            builtTests,
            questions.Take(10).ToList(),
            verification.Take(10).ToList());
    }

    public static (double Prior, IReadOnlyList<string> Applied) ApplyModifiers(CauseDefinition cause, TreeContext context)
    {
        var prior = Math.Max(0.001, cause.Prior);
        var applied = new List<string>();
        foreach (var modifier in cause.Modifiers)
        {
            var hit = modifier.When.ToLowerInvariant() switch
            {
                "dtc" => modifier.Match is not null && context.Dtcs.Any(d => PlaybookMatcher.IsMatch(modifier.Match, d)),
                "symptom" => modifier.Match is not null && PlaybookMatcher.IsSymptomMatch(modifier.Match, context.SymptomText),
                "mileage-over" => modifier.Value is { } v && context.Mileage is { } m && m > v,
                "mileage-under" => modifier.Value is { } v2 && context.Mileage is { } m2 && m2 < v2,
                _ => false,
            };

            if (!hit || modifier.Factor <= 0) continue;
            prior *= modifier.Factor;
            applied.Add(string.IsNullOrWhiteSpace(modifier.Reason)
                ? $"×{modifier.Factor:0.##} ({modifier.When})"
                : $"{modifier.Reason} (×{modifier.Factor:0.##})");
        }

        return (prior, applied);
    }

    public static DiagnosticCategory ParseCategory(string? category) =>
        Enum.TryParse<DiagnosticCategory>(category, ignoreCase: true, out var parsed) ? parsed : DiagnosticCategory.Other;

    private static IEnumerable<string> RelatedCauses(TestDefinition test) =>
        test.Outcomes
            .Where(o => o.Likelihoods is not null)
            .SelectMany(o => o.Likelihoods!.Keys)
            .Concat(test.RequiresAnyCause)
            .Where(k => k != DiagnosticMath.DefaultLikelihoodKey)
            .Distinct(StringComparer.Ordinal);

    private static bool HasDefaultOnly(TestDefinition test) =>
        test.Outcomes.Any(o => o.Likelihoods?.ContainsKey(DiagnosticMath.DefaultLikelihoodKey) == true);

    private static void MergeLikelihoods(TestDefinition target, TestDefinition source)
    {
        foreach (var outcome in source.Outcomes)
        {
            var existing = target.Outcomes.FirstOrDefault(o => o.Key == outcome.Key);
            if (existing is null || outcome.Likelihoods is null) continue;
            existing.Likelihoods ??= new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (cause, value) in outcome.Likelihoods)
            {
                existing.Likelihoods.TryAdd(cause, value);
            }
        }

        foreach (var cause in source.RequiresAnyCause)
        {
            if (!target.RequiresAnyCause.Contains(cause)) target.RequiresAnyCause.Add(cause);
        }
    }

    private static TestDefinition CloneTest(TestDefinition t) => new()
    {
        Key = t.Key,
        Title = t.Title,
        Purpose = t.Purpose,
        Procedure = [.. t.Procedure],
        Tools = [.. t.Tools],
        Expected = t.Expected,
        SpecNote = t.SpecNote,
        Minutes = t.Minutes,
        Difficulty = t.Difficulty,
        Invasiveness = t.Invasiveness,
        Safety = [.. t.Safety],
        RequiresAnyCause = [.. t.RequiresAnyCause],
        Outcomes = t.Outcomes.Select(o => new OutcomeDefinition
        {
            Key = o.Key,
            Label = o.Label,
            Normal = o.Normal,
            Inconclusive = o.Inconclusive,
            Interpretation = o.Interpretation,
            Likelihoods = o.Likelihoods is null ? null : new Dictionary<string, double>(o.Likelihoods, StringComparer.Ordinal),
        }).ToList(),
    };

    /// <summary>Converts a built test into a session entity.</summary>
    public static DiagnosticTest ToEntity(BuiltTest built, Guid sessionId)
    {
        var d = built.Definition;
        return new DiagnosticTest
        {
            SessionId = sessionId,
            Key = d.Key,
            Title = d.Title,
            Purpose = d.Purpose,
            Procedure = [.. d.Procedure],
            Tools = [.. d.Tools],
            ExpectedResult = d.Expected,
            SpecificationNote = d.SpecNote,
            EstimatedMinutes = d.Minutes <= 0 ? 15 : d.Minutes,
            Difficulty = Math.Clamp(d.Difficulty, 1, 5),
            Invasiveness = Math.Clamp(d.Invasiveness, 1, 5),
            SafetyTags = d.Safety.Where(SafetyTags.IsValid).Select(s => s.ToLowerInvariant()).Distinct().ToList(),
            RelatedCauseKeys = [.. built.RelatedCauseKeys],
            Outcomes = d.Outcomes.Select(o => new TestOutcomeDefinition
            {
                Key = o.Key,
                Label = o.Label,
                Normal = o.Normal,
                Inconclusive = o.Inconclusive,
                Interpretation = o.Interpretation,
                Likelihoods = o.Likelihoods is null
                    ? []
                    : new Dictionary<string, double>(o.Likelihoods, StringComparer.Ordinal),
            }).ToList(),
            Origin = ActorKind.System,
            OriginDetail = $"Playbook: {built.PlaybookKey}",
            Evidence = EvidenceClass.SourceDerived,
            Sources =
            [
                new SourceCitation
                {
                    Label = "REF",
                    Title = $"Mechanic AI diagnostic playbook ({built.PlaybookKey})",
                    Type = SourceType.BuiltInReference,
                    Excerpt = "General diagnostic procedure. Verify specifications against OEM service information.",
                },
            ],
        };
    }
}
