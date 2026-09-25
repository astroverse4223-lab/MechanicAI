using MechanicAI.Application.Ai;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Tests.Ai;

public class CitationValidatorTests
{
    private const string Removed = "(link removed — not from a retrieved source)";

    private static SourceRegistry Registry()
    {
        var registry = new SourceRegistry();
        registry.Register(new SourceCitation { Title = "NHTSA recalls", Url = "https://www.nhtsa.gov/recalls", Type = SourceType.Government });
        registry.Register(new SourceCitation { Title = "Shop manual", DocumentId = Guid.NewGuid(), PageNumber = 12, Type = SourceType.PrivateDocument });
        return registry;
    }

    [Fact]
    public void Validate_KeepsRealCitationsAndRemovesFabricatedOnes()
    {
        var report = CitationValidator.Validate("Coil failures are common [S1]. See [S1, S3] and [s2][S9].", Registry());

        Assert.Equal("Coil failures are common [S1]. See [S1] and [S2].", report.CleanedText);
        Assert.Equal(["S1", "S2"], report.CitedSources.Select(s => s.Label));
        Assert.Equal(2, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("[S3]", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("[S9]", StringComparison.Ordinal));
        Assert.True(report.HadProblems);
    }

    [Fact]
    public void Validate_CollapsesSpaceLeftByRemovedCitation()
    {
        var report = CitationValidator.Validate("A claim [S7] follows.", Registry());

        Assert.Equal("A claim follows.", report.CleanedText);
        Assert.Empty(report.CitedSources);
    }

    [Fact]
    public void Validate_KeepsUrlsFromRetrievedSourcesAndRemovesOthers()
    {
        var text = "Official: https://nhtsa.gov/recalls/ and fake: https://evil.example.com/fake.";

        var report = CitationValidator.Validate(text, Registry());

        Assert.Equal($"Official: https://nhtsa.gov/recalls/ and fake: {Removed}.", report.CleanedText);
        var warning = Assert.Single(report.Warnings);
        Assert.Contains("evil.example.com", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RepairsMarkdownLinkWithRemovedTarget()
    {
        var report = CitationValidator.Validate("See [the forum](https://forum.example.com/t/123) for more.", Registry());

        Assert.Equal($"See [the forum]{Removed} for more.", report.CleanedText);
    }

    [Fact]
    public void Validate_FlagsUncitedSourceDerivedStatements()
    {
        var text = "## Source-derived information\n- Fact one [S1]\n- Fact two\n1. Fact three\n\n## AI inference\n- Not counted\n";

        var report = CitationValidator.Validate(text, Registry());

        var warning = Assert.Single(report.Warnings);
        Assert.StartsWith("2 statement(s)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_CleanTextHasNoProblems()
    {
        var report = CitationValidator.Validate("Check the coil [S1].", Registry());

        Assert.False(report.HadProblems);
        Assert.Equal("Check the coil [S1].", report.CleanedText);
    }

    [Fact]
    public void Validate_EmptyText()
    {
        var report = CitationValidator.Validate(string.Empty, Registry());

        Assert.Equal(string.Empty, report.CleanedText);
        Assert.Empty(report.CitedSources);
        Assert.False(report.HadProblems);
    }
}

public class SourceRegistryTests
{
    [Fact]
    public void Register_AssignsSequentialLabelsWithoutMutatingInput()
    {
        var registry = new SourceRegistry();
        var input = new SourceCitation { Title = "A", Url = "https://a.example.com/x", Label = "original" };

        var first = registry.Register(input);
        var second = registry.Register(new SourceCitation { Title = "B", Url = "https://b.example.com/" });

        Assert.Equal("S1", first.Label);
        Assert.Equal("S2", second.Label);
        Assert.Equal("original", input.Label);
        Assert.NotSame(input, first);
        Assert.Equal(2, registry.Sources.Count);
    }

    [Fact]
    public void Register_DeduplicatesByNormalizedUrl()
    {
        var registry = new SourceRegistry();
        var first = registry.Register(new SourceCitation { Title = "A", Url = "https://www.example.com/page/" });

        var again = registry.Register(new SourceCitation { Title = "A again", Url = "http://EXAMPLE.com/page" });

        Assert.Same(first, again);
        Assert.Single(registry.Sources);
    }

    [Fact]
    public void Register_DeduplicatesDocumentsByPageAndChunks()
    {
        var registry = new SourceRegistry();
        var doc = Guid.NewGuid();
        var chunk = Guid.NewGuid();

        var page1 = registry.Register(new SourceCitation { Title = "Manual", DocumentId = doc, PageNumber = 1 });
        var page1Again = registry.Register(new SourceCitation { Title = "Manual", DocumentId = doc, PageNumber = 1 });
        var page2 = registry.Register(new SourceCitation { Title = "Manual", DocumentId = doc, PageNumber = 2 });
        var c1 = registry.Register(new SourceCitation { Title = "Chunk", ChunkId = chunk, DocumentId = doc, PageNumber = 5 });
        var c1Again = registry.Register(new SourceCitation { Title = "Chunk", ChunkId = chunk, DocumentId = doc, PageNumber = 6 });

        Assert.Same(page1, page1Again);
        Assert.NotSame(page1, page2);
        Assert.Same(c1, c1Again);
        Assert.Equal(3, registry.Sources.Count);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var registry = new SourceRegistry();
        registry.Register(new SourceCitation { Title = "A", Url = "https://a.example.com" });

        Assert.NotNull(registry.Find("s1"));
        Assert.Null(registry.Find("S2"));
    }

    [Theory]
    [InlineData("https://www.Example.com/Path/?q=1", "example.com/path/?q=1")]
    [InlineData("https://example.com/path/).", "example.com/path")]
    [InlineData("not a url/", "not a url")]
    public void NormalizeUrl_IgnoresSchemeWwwCaseAndTrailingPunctuation(string url, string expected)
    {
        Assert.Equal(expected, SourceRegistry.NormalizeUrl(url));
    }

    [Fact]
    public void ContainsUrl_MatchesNormalizedForms()
    {
        var registry = new SourceRegistry();
        registry.Register(new SourceCitation { Title = "A", Url = "https://www.example.com/a" });

        Assert.True(registry.ContainsUrl("http://example.com/a/"));
        Assert.False(registry.ContainsUrl("https://example.com/b"));
    }
}
