using MechanicAI.Application.Training;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using MechanicAI.Infrastructure.Content;
using MechanicAI.Infrastructure.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace MechanicAI.Infrastructure.Tests.Content;

public class EmbeddedContentProviderTests
{
    private static EmbeddedContentProvider Provider() => new(NullLogger<EmbeddedContentProvider>.Instance);

    [Fact]
    public async Task AllContent_LoadsWithoutValidationWarnings()
    {
        var provider = Provider();
        var ct = CancellationToken.None;

        await provider.LoadDtcFilesAsync(ct);
        await provider.LoadPlaybooksAsync(ct);
        await provider.LoadCoursesAsync(ct);
        await provider.LoadScenariosAsync(ct);
        await provider.LoadSampleDataAsync(ct);

        Assert.Empty(provider.ValidationWarnings);
    }

    [Fact]
    public async Task Playbooks_EveryShippedPlaybookPassesValidation()
    {
        var provider = Provider();
        var rawKeys = RawContent.Playbooks().Select(p => RawContent.Str(p.Playbook, "key")).ToList();

        var playbooks = await provider.LoadPlaybooksAsync(CancellationToken.None);

        Assert.NotEmpty(rawKeys);
        Assert.Equal(rawKeys.Count, playbooks.Count);
        Assert.Equal(rawKeys, playbooks.Select(p => p.Key));
        Assert.All(playbooks, p =>
        {
            Assert.NotNull(p.SourceFile);
            Assert.NotEmpty(p.Causes);
            Assert.NotEmpty(p.Tests);
            Assert.NotEmpty(p.Triggers.DtcPatterns.Concat(p.Triggers.SymptomKeywords));
        });
    }

    [Fact]
    public async Task Playbooks_AreCachedPerProvider()
    {
        var provider = Provider();
        var ct = CancellationToken.None;

        Assert.Same(await provider.LoadPlaybooksAsync(ct), await provider.LoadPlaybooksAsync(ct));
    }

    [Fact]
    public async Task Playbooks_KeysAreUnique()
    {
        var playbooks = await Provider().LoadPlaybooksAsync(CancellationToken.None);

        var duplicates = playbooks.GroupBy(p => p.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task DtcFiles_LoadWithNoInvalidEntries()
    {
        var provider = Provider();
        var raw = RawContent.Files("Dtc");

        var files = await provider.LoadDtcFilesAsync(CancellationToken.None);

        Assert.Equal(raw.Count, files.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var rawCount = RawContent.Array(raw[i].Root, "codes").Count();
            Assert.True(rawCount == files[i].Codes.Count, $"{raw[i].File}: {rawCount - files[i].Codes.Count} invalid DTC entries were dropped");
            Assert.False(string.IsNullOrWhiteSpace(files[i].Source), raw[i].File);
        }

        Assert.All(files.SelectMany(f => f.Codes), c =>
        {
            Assert.True(DtcCode.TryParse(c.Code, out var code), c.Code);
            Assert.Equal(code.Value, c.Code); // stored in canonical form
            Assert.False(string.IsNullOrWhiteSpace(c.Description), c.Code);
            Assert.False(string.IsNullOrWhiteSpace(c.Subsystem), c.Code);
        });
    }

    [Fact]
    public async Task DtcFiles_HaveNoDuplicateCodesAcrossFiles()
    {
        var provider = Provider();
        var files = await provider.LoadDtcFilesAsync(CancellationToken.None);
        var names = RawContent.ResourceNames("Dtc").Select(RawContent.FileName).ToList();

        var occurrences = files
            .SelectMany((f, i) => f.Codes.Select(c => (Code: DtcCode.Parse(c.Code).Value, File: names[i])))
            .GroupBy(x => x.Code)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} in {string.Join(", ", g.Select(x => x.File))}")
            .ToList();

        Assert.Empty(occurrences);
    }

    [Fact]
    public async Task DtcFiles_ContainOnlyGenericCodes()
    {
        // CONTENT-SCHEMAS rule 6: manufacturer-specific codes get no generic description.
        var files = await Provider().LoadDtcFilesAsync(CancellationToken.None);

        var manufacturerSpecific = files.SelectMany(f => f.Codes).Where(c => DtcCode.Parse(c.Code).IsManufacturerSpecific).Select(c => c.Code).ToList();
        Assert.Empty(manufacturerSpecific);
    }

    [Fact]
    public async Task Courses_LoadWithValidCategoriesQuizzesDiagramsAndExercises()
    {
        var provider = Provider();
        var ct = CancellationToken.None;
        var rawCount = RawContent.Files("Training").Sum(f => RawContent.Array(f.Root, "courses").Count());

        var courses = await provider.LoadCoursesAsync(ct);

        Assert.NotEmpty(courses);
        Assert.Equal(rawCount, courses.Count);
        Assert.Equal(courses.Count, courses.Select(c => c.Key).Distinct().Count());
        foreach (var course in courses)
        {
            Assert.True(Enum.TryParse<TrainingCategory>(course.Category, true, out _), course.Key);
            Assert.True(Enum.TryParse<TrainingLevel>(course.Level, true, out _), course.Key);
            Assert.NotEmpty(course.Lessons);
            Assert.All(course.Lessons, l => Assert.False(string.IsNullOrWhiteSpace(l.Body), $"{course.Key}/{l.Key}"));
            Assert.All(course.Exercises, e => Assert.Contains(e, ExerciseGenerator.Types));

            var rawQuestions = RawContent.Files("Training")
                .SelectMany(f => RawContent.Array(f.Root, "courses"))
                .Single(c => RawContent.Str(c.AsObject(), "key") == course.Key)["quiz"]?["questions"]?.AsArray().Count ?? 0;
            Assert.Equal(rawQuestions, course.Quiz?.Questions.Count ?? 0); // no question dropped as invalid

            foreach (var lesson in course.Lessons.Where(l => l.Diagram is not null))
            {
                Assert.NotNull(await provider.GetDiagramSvgAsync(lesson.Diagram!, ct));
            }
        }
    }

    [Fact]
    public async Task Scenarios_LoadAndIdealPathsReferenceTheirTests()
    {
        var rawCount = RawContent.Files("Scenarios").Sum(f => RawContent.Array(f.Root, "scenarios").Count());

        var scenarios = await Provider().LoadScenariosAsync(CancellationToken.None);

        Assert.NotEmpty(scenarios);
        Assert.Equal(rawCount, scenarios.Count);
        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Key).Distinct().Count());
        string[] values = ["high", "medium", "low", "parts-cannon"];
        foreach (var scenario in scenarios)
        {
            var testKeys = scenario.Tests.Select(t => t.Key).ToList();
            Assert.Equal(testKeys.Count, testKeys.Distinct().Count());
            Assert.InRange(testKeys.Count, 8, 14);
            Assert.All(scenario.IdealPath, k => Assert.Contains(k, testKeys));
            Assert.All(scenario.Tests, t => Assert.Contains(t.Value, values));
            Assert.All(scenario.Codes, c => Assert.True(DtcCode.TryParse(c, out _), $"{scenario.Key}: {c}"));
            Assert.True(Enum.TryParse<TrainingCategory>(scenario.Category, true, out _), scenario.Key);
            Assert.False(string.IsNullOrWhiteSpace(scenario.RootCause), scenario.Key);
        }
    }

    [Fact]
    public async Task SampleData_Loads()
    {
        var sample = await Provider().LoadSampleDataAsync(CancellationToken.None);

        Assert.NotNull(sample);
        Assert.NotEmpty(sample.Vehicles);
        var vehicleKeys = sample.Vehicles.Select(v => v.Key).ToHashSet();
        Assert.All(sample.Sessions, s => Assert.Contains(s.VehicleKey, vehicleKeys));
    }

    [Fact]
    public async Task Diagrams_AllEmbeddedSvgsAreServed()
    {
        var provider = Provider();
        var names = RawContent.ResourceNames("Diagrams", ".svg");

        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            var key = RawContent.FileName(name)[..^4];
            var svg = await provider.GetDiagramSvgAsync(key, CancellationToken.None);
            Assert.NotNull(svg);
            Assert.Contains("<svg", svg, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("../secrets")]
    [InlineData("does-not-exist")]
    public async Task GetDiagramSvg_ReturnsNullForUnknownOrUnsafeKeys(string key)
    {
        Assert.Null(await Provider().GetDiagramSvgAsync(key, CancellationToken.None));
    }

    [Fact]
    public void ContentVersion_IsStableShortHash()
    {
        var version = Provider().ContentVersion;

        Assert.Equal(16, version.Length);
        Assert.True(version.All(Uri.IsHexDigit));
        Assert.Equal(version, Provider().ContentVersion);
    }

    [Fact]
    public async Task ExternalContent_IsMergedAndValidated()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var dir = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "content", "Playbooks")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "a-shop.json"), """
            {
              "version": "1",
              "playbooks": [
                {
                  "key": "shop-custom",
                  "title": "Shop playbook",
                  "triggers": { "dtcPatterns": ["^P0999$", "([invalid"], "symptomKeywords": [] },
                  "causes": [ { "key": "shop-cause", "title": "Cause", "category": "NotACategory", "prior": 0.5, "safety": ["fuel", "bogus"] } ],
                  "tests": [
                    { "key": "one-outcome", "title": "Bad test", "outcomes": [ { "key": "a", "label": "A" } ] },
                    {
                      "key": "good-test", "title": "Good test", "safety": ["lifting", "nope"],
                      "outcomes": [
                        { "key": "a", "label": "A", "normal": false, "likelihoods": { "shop-cause": 1.5 } },
                        { "key": "b", "label": "B", "normal": true, "likelihoods": { "shop-cause": 0.1 } }
                      ]
                    }
                  ]
                },
                { "key": "empty", "title": "No causes", "causes": [] }
              ]
            }
            """, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(dir, "b-broken.json"), "{ not json", CancellationToken.None);
        var embeddedCount = (await Provider().LoadPlaybooksAsync(CancellationToken.None)).Count;
        var provider = new EmbeddedContentProvider(NullLogger<EmbeddedContentProvider>.Instance, paths);

        var playbooks = await provider.LoadPlaybooksAsync(CancellationToken.None);

        Assert.Equal(embeddedCount + 1, playbooks.Count);
        var custom = playbooks.Single(p => p.Key == "shop-custom");
        Assert.Equal(["^P0999$"], custom.Triggers.DtcPatterns);
        Assert.Equal(nameof(DiagnosticCategory.Other), custom.Causes[0].Category);
        Assert.Equal(["fuel"], custom.Causes[0].Safety);
        var test = Assert.Single(custom.Tests);
        Assert.Equal("good-test", test.Key);
        Assert.Equal(["lifting"], test.Safety);
        Assert.Equal(1.0, test.Outcomes[0].Likelihoods!["shop-cause"]);

        var warnings = provider.ValidationWarnings;
        Assert.Contains(warnings, w => w.Contains("invalid trigger pattern", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("unknown category", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("two decisive outcomes", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("out of range", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'empty' has no causes", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("b-broken.json", StringComparison.Ordinal) && w.Contains("invalid JSON", StringComparison.Ordinal));
    }
}
