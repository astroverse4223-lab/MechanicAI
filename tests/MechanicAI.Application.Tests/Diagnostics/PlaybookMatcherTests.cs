using MechanicAI.Application.Diagnostics;

namespace MechanicAI.Application.Tests.Diagnostics;

public class PlaybookMatcherTests
{
    [Fact]
    public void Match_ByDtcAndSymptom_ScoresAndExplains()
    {
        var matches = PlaybookMatcher.Match(TestPlaybooks.All(), ["P0302"], "misfire at idle, stalls sometimes");

        var match = Assert.Single(matches);
        Assert.Equal("misfire", match.Playbook.Key);
        Assert.True(match.MatchedByDtc);
        Assert.Equal(1.35, match.Score, 6);
        Assert.Equal(["DTC P0302", "complaint mentions \"misfire\""], match.Reasons);
    }

    [Fact]
    public void Match_SymptomOnlyPlaybooksAreDroppedWhenCodesMatched()
    {
        var matches = PlaybookMatcher.Match(TestPlaybooks.All(), ["P0302"], "stalls at lights");

        Assert.DoesNotContain(matches, m => m.Playbook.Key == "stalling");
    }

    [Fact]
    public void Match_SymptomOnlyPlaybooksAreUsedWithoutCodes()
    {
        var matches = PlaybookMatcher.Match(TestPlaybooks.All(), [], "car stalls at lights");

        var match = Assert.Single(matches);
        Assert.Equal("stalling", match.Playbook.Key);
        Assert.False(match.MatchedByDtc);
        Assert.Equal(0.35, match.Score, 6);
    }

    [Fact]
    public void Match_StrongKeywordMatchOfCodePlaybookIsKeptAlongsideDtcMatch()
    {
        var matches = PlaybookMatcher.Match(TestPlaybooks.All(), ["P0302"], "hesitation and running lean");

        Assert.Equal(["misfire", "lean"], matches.Select(m => m.Playbook.Key));
        Assert.False(matches[1].MatchedByDtc);
        Assert.Equal(0.7, matches[1].Score, 6);
    }

    [Fact]
    public void Match_WeakKeywordMatchOfCodePlaybookIsDroppedWhenOthersMatchByDtc()
    {
        var matches = PlaybookMatcher.Match(TestPlaybooks.All(), ["P0302"], "hesitation");

        Assert.Equal(["misfire"], matches.Select(m => m.Playbook.Key));
    }

    [Fact]
    public void Match_OrdersByScore()
    {
        var matches = PlaybookMatcher.Match(TestPlaybooks.All(), ["P0171", "P0174", "P0302"], string.Empty);

        Assert.Equal(["lean", "misfire"], matches.Select(m => m.Playbook.Key));
        Assert.Equal(2.0, matches[0].Score, 6);
    }

    [Fact]
    public void Match_NothingMatches()
    {
        Assert.Empty(PlaybookMatcher.Match(TestPlaybooks.All(), ["U0100"], "radio is quiet"));
    }

    [Theory]
    [InlineData("stall", "engine stalls at idle", true)]
    [InlineData("stall", "Stalling when cold", true)]
    [InlineData("stall", "stalled", true)]
    [InlineData("stall", "after the install", false)]
    [InlineData("dies", "it dies at stops", true)]
    [InlineData("dies", "diesel engine", false)]
    [InlineData("hesitat", "hesitation on takeoff", true)]
    [InlineData("hesitat", "unhesitating", false)]
    [InlineData("rough idle", "Rough Idle when warm", true)]
    [InlineData("\\bmis\\b", "mis fire", true)]
    [InlineData("", "anything", false)]
    [InlineData("stall", "", false)]
    public void IsSymptomMatch_UsesWordBoundaries(string pattern, string input, bool expected)
    {
        Assert.Equal(expected, PlaybookMatcher.IsSymptomMatch(pattern, input));
    }

    [Theory]
    [InlineData("stall", "(?<![A-Za-z0-9])(?:stall)(?:s|es|ed|ing)?(?![A-Za-z])")]
    [InlineData("hesitat", "(?<![A-Za-z0-9])(?:hesitat)")]
    [InlineData("rain|wet", "(?<![A-Za-z0-9])(?:rain|wet)")]
    [InlineData("^P03", "^P03")]
    [InlineData("\\bcel\\b", "\\bcel\\b")]
    public void ToWordBoundedPattern_WrapsOnlyUnanchoredPatterns(string pattern, string expected)
    {
        Assert.Equal(expected, PlaybookMatcher.ToWordBoundedPattern(pattern));
    }

    [Fact]
    public void IsMatch_IsCaseInsensitiveRegex()
    {
        Assert.True(PlaybookMatcher.IsMatch("^P030[0-9]$", "p0302"));
        Assert.False(PlaybookMatcher.IsMatch("^P030[0-9]$", "P0312"));
    }

    [Fact]
    public void IsMatch_InvalidRegexFallsBackToSubstring()
    {
        Assert.True(PlaybookMatcher.IsMatch("([", "text with ([ inside"));
        Assert.False(PlaybookMatcher.IsMatch("([", "plain text"));
    }
}
