using MechanicAI.Application.Research;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Tests.Research;

public class SourceClassifierTests
{
    [Theory]
    [InlineData("https://www.nhtsa.gov/recalls", SourceType.Government, "NHTSA")]
    [InlineData("https://techinfo.toyota.com/doc/123", SourceType.Manufacturer, "Toyota TIS")]
    [InlineData("https://www.toyota.com/camry", SourceType.Manufacturer, "Toyota")]
    [InlineData("https://www.alldata.com/", SourceType.ProfessionalDatabase, "ALLDATA")]
    [InlineData("https://www.underhoodservice.com/article", SourceType.TechnicalPublication, "Underhood Service")]
    [InlineData("https://mechanics.stackexchange.com/questions/1", SourceType.TechnicalCommunity, "Motor Vehicle Maintenance & Repair Stack Exchange")]
    [InlineData("https://old.reddit.com/r/MechanicAdvice", SourceType.Forum, "Reddit")]
    [InlineData("https://m.youtube.com/watch?v=abc", SourceType.SocialMedia, "YouTube")]
    public void Classify_KnownDomains(string url, SourceType type, string publisher)
    {
        var result = SourceClassifier.Classify(url);

        Assert.Equal(type, result.Type);
        Assert.Equal(publisher, result.Publisher);
        Assert.Equal(SourceClassifier.Label(type), result.Label);
        Assert.Equal(SourceClassifier.Weight(type), result.Weight, 9);
    }

    [Fact]
    public void Classify_DoesNotMatchLookalikeDomains()
    {
        var result = SourceClassifier.Classify("https://notford.com/page");

        Assert.Equal(SourceType.Unknown, result.Type);
        Assert.Equal("notford.com", result.Publisher);
    }

    [Theory]
    [InlineData("https://www.dmv.ca.gov/portal", "dmv.ca.gov")]
    [InlineData("https://example.gov.au/page", "example.gov.au")]
    [InlineData("https://army.mil/vehicles", "army.mil")]
    public void Classify_GovernmentSuffixes(string url, string publisher)
    {
        var result = SourceClassifier.Classify(url);

        Assert.Equal(SourceType.Government, result.Type);
        Assert.Equal(publisher, result.Publisher);
    }

    [Theory]
    [InlineData("https://www.f250club.net/forums/topic-1")]
    [InlineData("https://forums.example.net/post/1")]
    [InlineData("https://example.org/t/misfire-help/123")]
    [InlineData("https://example.org/showthread.php?t=1")]
    [InlineData("https://someforum.example/abc")]
    public void Classify_UnknownForumsByHostOrPath(string url)
    {
        Assert.Equal(SourceType.Forum, SourceClassifier.Classify(url).Type);
    }

    [Fact]
    public void Classify_UnknownSiteAndInvalidUrl()
    {
        var unknown = SourceClassifier.Classify("https://example.com/article");
        Assert.Equal(SourceType.Unknown, unknown.Type);
        Assert.Equal("General Web Source", unknown.Label);
        Assert.Equal(0.55, unknown.Weight, 9);

        var invalid = SourceClassifier.Classify("not a url");
        Assert.Equal(SourceType.Unknown, invalid.Type);
        Assert.Null(invalid.Publisher);
    }

    [Fact]
    public void Classify_PreferredDomainsGetABoostCappedAtOne()
    {
        Assert.Equal(0.70, SourceClassifier.Classify("https://www.example.com/a", [" Example.com "]).Weight, 9);
        Assert.Equal(1.0, SourceClassifier.Classify("https://ford.com/a", ["ford.com"]).Weight, 9);
        Assert.Equal(0.55, SourceClassifier.Classify("https://example.com/a", ["other.com"]).Weight, 9);
    }

    [Fact]
    public void Weight_RanksAuthorityOverForumsAndSocialMedia()
    {
        SourceType[] order =
        [
            SourceType.Manufacturer, SourceType.Government, SourceType.ProfessionalDatabase, SourceType.TechnicalPublication,
            SourceType.TechnicalCommunity, SourceType.Unknown, SourceType.Forum, SourceType.SocialMedia,
        ];

        for (var i = 1; i < order.Length; i++)
        {
            Assert.True(SourceClassifier.Weight(order[i - 1]) > SourceClassifier.Weight(order[i]), $"{order[i - 1]} > {order[i]}");
        }
    }

    [Fact]
    public void Label_CoversEveryType()
    {
        Assert.All(Enum.GetValues<SourceType>(), t => Assert.False(string.IsNullOrWhiteSpace(SourceClassifier.Label(t))));
        Assert.Equal("Your Document", SourceClassifier.Label(SourceType.PrivateDocument));
        Assert.Equal("Forum Discussion", SourceClassifier.Label(SourceType.Forum));
    }
}
