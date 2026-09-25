using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Domain.Tests.ValueObjects;

public class SafetyTagsTests
{
    [Fact]
    public void All_ContainsEveryConstant()
    {
        var constants = typeof(SafetyTags)
            .GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(12, constants.Count);
        Assert.Equal(constants.Count, SafetyTags.All.Count);
        Assert.All(constants, c => Assert.Contains(c, SafetyTags.All));
    }

    [Theory]
    [InlineData("airbag")]
    [InlineData("high-voltage")]
    [InlineData("HIGH-VOLTAGE")]
    [InlineData("Hot-Surfaces")]
    [InlineData("exhaust-gas")]
    public void IsValid_IsCaseInsensitive(string tag)
    {
        Assert.True(SafetyTags.IsValid(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("high voltage")]
    [InlineData("electrical")]
    [InlineData("fire")]
    public void IsValid_RejectsUnknownTags(string tag)
    {
        Assert.False(SafetyTags.IsValid(tag));
    }
}

public class SourceCitationTests
{
    [Fact]
    public void Defaults_AreUnknownTypeAndNotCached()
    {
        var before = DateTime.UtcNow;
        var citation = new SourceCitation();

        Assert.Equal(SourceType.Unknown, citation.Type);
        Assert.False(citation.FromCache);
        Assert.Equal(string.Empty, citation.Label);
        Assert.InRange(citation.RetrievedUtc, before, DateTime.UtcNow);
    }

    [Fact]
    public void Clone_CopiesAllValuesIntoIndependentInstance()
    {
        var original = new SourceCitation
        {
            Label = "S1",
            Title = "Service bulletin",
            Url = "https://example.com/tsb",
            Type = SourceType.Manufacturer,
            Publisher = "OEM",
            PublishedUtc = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Excerpt = "Excerpt",
            DocumentId = Guid.NewGuid(),
            PageNumber = 4,
            ChunkId = Guid.NewGuid(),
            FromCache = true,
        };

        var clone = original.Clone();
        clone.Label = "S2";

        Assert.NotSame(original, clone);
        Assert.Equal("S1", original.Label);
        Assert.Equal(original.Title, clone.Title);
        Assert.Equal(original.Url, clone.Url);
        Assert.Equal(original.Type, clone.Type);
        Assert.Equal(original.Publisher, clone.Publisher);
        Assert.Equal(original.PublishedUtc, clone.PublishedUtc);
        Assert.Equal(original.RetrievedUtc, clone.RetrievedUtc);
        Assert.Equal(original.Excerpt, clone.Excerpt);
        Assert.Equal(original.DocumentId, clone.DocumentId);
        Assert.Equal(original.PageNumber, clone.PageNumber);
        Assert.Equal(original.ChunkId, clone.ChunkId);
        Assert.True(clone.FromCache);
    }
}
