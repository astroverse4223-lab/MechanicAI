using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Infrastructure.Tests.Content;

/// <summary>
/// Data-driven checks over every embedded content file (docs/CONTENT-SCHEMAS.md is the contract).
/// These read the raw JSON, so problems the loader would silently repair still fail here.
/// </summary>
public partial class ContentIntegrityTests
{
    private static readonly Lazy<IReadOnlySet<string>> DocumentedSafetyTags = new(LoadDocumentedSafetyTags);

    private static IReadOnlySet<string> LoadDocumentedSafetyTags()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "CONTENT-SCHEMAS.md"))) dir = dir.Parent;
        Assert.True(dir is not null, "docs/CONTENT-SCHEMAS.md not found above the test output directory");

        var markdown = File.ReadAllText(Path.Combine(dir.FullName, "docs", "CONTENT-SCHEMAS.md"));
        var section = markdown[markdown.IndexOf("### Safety tag vocabulary", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("###", 5, StringComparison.Ordinal)];
        return SafetyTableRowRegex().Matches(section).Select(m => m.Groups["tag"].Value).ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\|\s*`(?<tag>[a-z-]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex SafetyTableRowRegex();

    private static List<string> Problems() => [];

    [Fact]
    public void DocumentedSafetyVocabulary_MatchesSafetyTags()
    {
        Assert.Equal(12, DocumentedSafetyTags.Value.Count);
        Assert.Equal(DocumentedSafetyTags.Value.Order(StringComparer.Ordinal), SafetyTags.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RawContentIsDiscovered()
    {
        Assert.NotEmpty(RawContent.ResourceNames("Playbooks"));
        Assert.NotEmpty(RawContent.ResourceNames("Dtc"));
        Assert.NotEmpty(RawContent.ResourceNames("Training"));
        Assert.NotEmpty(RawContent.ResourceNames("Scenarios"));
        Assert.NotEmpty(RawContent.Playbooks());
    }

    [Fact]
    public void SafetyTags_AllContentUsesTheDocumentedVocabulary()
    {
        var problems = Problems();
        void Check(string where, JsonObject obj, string property = "safety")
        {
            foreach (var tag in RawContent.Strings(obj, property))
            {
                // Exact (case-sensitive) match: content must use the canonical lowercase tags.
                if (!DocumentedSafetyTags.Value.Contains(tag)) problems.Add($"{where}: '{tag}'");
            }
        }

        foreach (var (file, playbook) in RawContent.Playbooks())
        {
            var key = RawContent.Str(playbook, "key");
            foreach (var cause in RawContent.Array(playbook, "causes")) Check($"{file}/{key}/cause {RawContent.Str(cause.AsObject(), "key")}", cause.AsObject());
            foreach (var test in RawContent.Array(playbook, "tests")) Check($"{file}/{key}/test {RawContent.Str(test.AsObject(), "key")}", test.AsObject());
        }

        foreach (var (file, code) in RawContent.DtcCodes()) Check($"{file}/{RawContent.Str(code, "code")}", code);

        foreach (var (file, root) in RawContent.Files("Training"))
        {
            foreach (var course in RawContent.Array(root, "courses"))
            {
                foreach (var lesson in RawContent.Array(course.AsObject(), "lessons"))
                {
                    Check($"{file}/{RawContent.Str(course.AsObject(), "key")}/{RawContent.Str(lesson.AsObject(), "key")}", lesson.AsObject());
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Playbooks_AllRegexesCompile()
    {
        var problems = Problems();
        void Check(string where, string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (where.Contains("symptom", StringComparison.Ordinal))
                {
                    // Symptom keywords are matched on word boundaries; the wrapped form must compile too.
                    _ = new Regex(PlaybookMatcher.ToWordBoundedPattern(pattern), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                }
            }
            catch (ArgumentException ex)
            {
                problems.Add($"{where}: '{pattern}' ({ex.Message})");
            }
        }

        foreach (var (file, playbook) in RawContent.Playbooks())
        {
            var key = RawContent.Str(playbook, "key");
            var triggers = playbook["triggers"]?.AsObject() ?? new JsonObject();
            foreach (var p in RawContent.Strings(triggers, "dtcPatterns")) Check($"{file}/{key} dtcPattern", p);
            foreach (var p in RawContent.Strings(triggers, "symptomKeywords")) Check($"{file}/{key} symptomKeyword", p);
            foreach (var cause in RawContent.Array(playbook, "causes"))
            {
                foreach (var modifier in RawContent.Array(cause.AsObject(), "modifiers"))
                {
                    var when = RawContent.Str(modifier.AsObject(), "when");
                    var match = modifier["match"]?.GetValue<string>();
                    if (match is not null) Check($"{file}/{key}/{RawContent.Str(cause.AsObject(), "key")} {when} modifier", match);
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Playbooks_DtcPatternsMatchAtLeastOneValidCode()
    {
        // Guards against patterns that can never fire (e.g. a typo such as "^PO171$" with a letter O).
        var candidates = new List<string>();
        foreach (var system in "PBCU")
        {
            for (var n = 0; n < 0x4000; n++)
            {
                candidates.Add($"{system}{n >> 12}{n & 0xFFF:X3}");
            }
        }

        var problems = Problems();
        foreach (var (file, playbook) in RawContent.Playbooks())
        {
            var triggers = playbook["triggers"]?.AsObject() ?? new JsonObject();
            foreach (var pattern in RawContent.Strings(triggers, "dtcPatterns"))
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!candidates.Any(regex.IsMatch)) problems.Add($"{file}/{RawContent.Str(playbook, "key")}: '{pattern}'");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Playbooks_ModifierKindsAreKnown()
    {
        string[] kinds = ["dtc", "symptom", "mileage-over", "mileage-under"];
        var problems = Problems();
        foreach (var (file, playbook) in RawContent.Playbooks())
        {
            foreach (var cause in RawContent.Array(playbook, "causes"))
            {
                foreach (var modifier in RawContent.Array(cause.AsObject(), "modifiers"))
                {
                    var m = modifier.AsObject();
                    var when = RawContent.Str(m, "when");
                    var where = $"{file}/{RawContent.Str(playbook, "key")}/{RawContent.Str(cause.AsObject(), "key")}";
                    if (!kinds.Contains(when)) problems.Add($"{where}: unknown modifier '{when}'");
                    else if (when.StartsWith("mileage", StringComparison.Ordinal) && m["value"] is null) problems.Add($"{where}: {when} without value");
                    else if (!when.StartsWith("mileage", StringComparison.Ordinal) && m["match"] is null) problems.Add($"{where}: {when} without match");
                    if (m["factor"] is { } f && f.GetValue<double>() <= 0) problems.Add($"{where}: non-positive factor");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Playbooks_CausesAndTestsAreWellFormed()
    {
        var problems = Problems();
        foreach (var (file, playbook) in RawContent.Playbooks())
        {
            var key = RawContent.Str(playbook, "key");
            var where = $"{file}/{key}";
            if (!KebabCaseRegex().IsMatch(key)) problems.Add($"{where}: playbook key is not kebab-case");

            var causeKeys = RawContent.Array(playbook, "causes").Select(c => RawContent.Str(c.AsObject(), "key")).ToList();
            foreach (var dup in causeKeys.GroupBy(k => k).Where(g => g.Count() > 1)) problems.Add($"{where}: duplicate cause '{dup.Key}'");

            foreach (var causeNode in RawContent.Array(playbook, "causes"))
            {
                var cause = causeNode.AsObject();
                var causeKey = RawContent.Str(cause, "key");
                if (!KebabCaseRegex().IsMatch(causeKey)) problems.Add($"{where}: cause key '{causeKey}' is not kebab-case");
                if (!Enum.TryParse<DiagnosticCategory>(RawContent.Str(cause, "category"), true, out _)) problems.Add($"{where}/{causeKey}: unknown category '{RawContent.Str(cause, "category")}'");
                if (cause["prior"] is { } prior && prior.GetValue<double>() <= 0) problems.Add($"{where}/{causeKey}: prior must be positive");
            }

            var testKeys = RawContent.Array(playbook, "tests").Select(t => RawContent.Str(t.AsObject(), "key")).ToList();
            foreach (var dup in testKeys.GroupBy(k => k).Where(g => g.Count() > 1)) problems.Add($"{where}: duplicate test '{dup.Key}'");

            foreach (var testNode in RawContent.Array(playbook, "tests"))
            {
                var test = testNode.AsObject();
                var testKey = RawContent.Str(test, "key");
                if (!KebabCaseRegex().IsMatch(testKey)) problems.Add($"{where}: test key '{testKey}' is not kebab-case");
                var outcomes = RawContent.Array(test, "outcomes").Select(o => o.AsObject()).ToList();
                if (outcomes.Count(o => o["inconclusive"]?.GetValue<bool>() != true) < 2) problems.Add($"{where}/{testKey}: fewer than two decisive outcomes");
                foreach (var dup in outcomes.GroupBy(o => RawContent.Str(o, "key")).Where(g => g.Count() > 1)) problems.Add($"{where}/{testKey}: duplicate outcome '{dup.Key}'");
                foreach (var name in new[] { "difficulty", "invasiveness" })
                {
                    if (test[name] is { } v && v.GetValue<int>() is < 1 or > 5) problems.Add($"{where}/{testKey}: {name} out of 1-5");
                }

                foreach (var outcome in outcomes)
                {
                    foreach (var (cause, value) in outcome["likelihoods"]?.AsObject() ?? new JsonObject())
                    {
                        var v = value!.GetValue<double>();
                        if (v is <= 0 or > 1 || double.IsNaN(v)) problems.Add($"{where}/{testKey}/{RawContent.Str(outcome, "key")}: likelihood for '{cause}' is {v}");
                    }
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Playbooks_LikelihoodAndRequiresAnyCauseKeysReferenceCausesInTheSamePlaybook()
    {
        Assert.Empty(CrossReferenceProblems(RawContent.Playbooks()));
    }

    [Fact]
    public void Playbooks_SharedTestKeysAreStructurallyIdenticalAcrossAllFiles()
    {
        Assert.Empty(SharedTestKeyProblems(RawContent.Playbooks()));
    }

    [Fact]
    public void IntegrityChecks_DetectBrokenContent()
    {
        // Negative control: the checks above must actually catch the problems they look for.
        var a = JsonNode.Parse("""
            { "key": "a", "causes": [ { "key": "coil" } ],
              "tests": [ { "key": "shared", "requiresAnyCause": ["plug"], "outcomes": [
                { "key": "x", "normal": false, "likelihoods": { "coil": 0.9, "ghost": 0.5, "*": 0.1 } },
                { "key": "y", "normal": true } ] } ] }
            """)!.AsObject();
        var b = JsonNode.Parse("""
            { "key": "b", "causes": [ { "key": "plug" } ],
              "tests": [ { "key": "shared", "outcomes": [ { "key": "x", "normal": false }, { "key": "z", "normal": true } ] } ] }
            """)!.AsObject();
        (string, JsonObject)[] playbooks = [("one.json", a), ("two.json", b)];

        var crossReference = CrossReferenceProblems(playbooks);
        Assert.Equal(2, crossReference.Count);
        Assert.Contains(crossReference, p => p.Contains("'plug'", StringComparison.Ordinal));
        Assert.Contains(crossReference, p => p.Contains("'ghost'", StringComparison.Ordinal));

        var shared = Assert.Single(SharedTestKeyProblems(playbooks));
        Assert.Contains("one.json/a", shared, StringComparison.Ordinal);
        Assert.Contains("two.json/b", shared, StringComparison.Ordinal);
    }

    private static List<string> CrossReferenceProblems(IEnumerable<(string File, JsonObject Playbook)> playbooks)
    {
        var problems = Problems();
        foreach (var (file, playbook) in playbooks)
        {
            var key = RawContent.Str(playbook, "key");
            var causeKeys = RawContent.Array(playbook, "causes").Select(c => RawContent.Str(c.AsObject(), "key")).ToHashSet(StringComparer.Ordinal);
            foreach (var testNode in RawContent.Array(playbook, "tests"))
            {
                var test = testNode.AsObject();
                var testKey = RawContent.Str(test, "key");
                foreach (var required in RawContent.Strings(test, "requiresAnyCause"))
                {
                    if (!causeKeys.Contains(required)) problems.Add($"{file}/{key}/{testKey}: requiresAnyCause '{required}' is not a cause of this playbook");
                }

                foreach (var outcome in RawContent.Array(test, "outcomes"))
                {
                    foreach (var (cause, _) in outcome["likelihoods"]?.AsObject() ?? new JsonObject())
                    {
                        if (cause != DiagnosticMath.DefaultLikelihoodKey && !causeKeys.Contains(cause))
                        {
                            problems.Add($"{file}/{key}/{testKey}/{RawContent.Str(outcome.AsObject(), "key")}: likelihood key '{cause}' is not a cause of this playbook");
                        }
                    }
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Test keys are global: when two playbooks define the same test, the engine keeps the first
    /// definition and merges likelihoods by outcome key. Differing outcome structures would silently
    /// drop evidence, so every definition of a shared key must have the same outcomes (key, normal,
    /// inconclusive) in the same order — across all playbook files.
    /// </summary>
    private static List<string> SharedTestKeyProblems(IEnumerable<(string File, JsonObject Playbook)> playbooks)
    {
        var definitions = playbooks
            .SelectMany(p => RawContent.Array(p.Playbook, "tests").Select(t => (
                Where: $"{p.File}/{RawContent.Str(p.Playbook, "key")}",
                Key: RawContent.Str(t.AsObject(), "key"),
                Shape: Shape(t.AsObject()))))
            .GroupBy(d => d.Key)
            .Where(g => g.Count() > 1);

        var problems = Problems();
        foreach (var group in definitions)
        {
            var first = group.First();
            foreach (var other in group.Skip(1).Where(o => o.Shape != first.Shape))
            {
                problems.Add($"test '{group.Key}': {first.Where} has [{first.Shape}] but {other.Where} has [{other.Shape}]");
            }
        }

        return problems;
    }

    private static string Shape(JsonObject test) => string.Join("; ", RawContent.Array(test, "outcomes").Select(o =>
    {
        var outcome = o.AsObject();
        var normal = outcome["normal"] is { } n ? n.GetValue<bool>().ToString() : "null";
        var inconclusive = outcome["inconclusive"]?.GetValue<bool>() == true;
        return $"{RawContent.Str(outcome, "key")}:normal={normal}:inconclusive={inconclusive}";
    }));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabCaseRegex();
}
