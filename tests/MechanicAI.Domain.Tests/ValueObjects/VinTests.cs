using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Domain.Tests.ValueObjects;

public class VinTests
{
    [Theory]
    [InlineData("1HGCM82633A004352", '3')]
    [InlineData("1M8GDM9AXKP042788", 'X')]
    [InlineData("1FTFW1E59JFA00001", '9')]
    public void ComputeCheckDigit_MatchesPosition9(string vin, char expected)
    {
        Assert.Equal(expected, Vin.ComputeCheckDigit(vin));
        Assert.True(Vin.Parse(vin).HasValidCheckDigit);
    }

    [Fact]
    public void HasValidCheckDigit_IsFalseWhenCheckDigitIsWrong()
    {
        var vin = Vin.Parse("1HGCM82643A004352");
        Assert.False(vin.HasValidCheckDigit);
    }

    [Fact]
    public void ComputeCheckDigit_RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => Vin.ComputeCheckDigit("1HGCM8263"));
    }

    [Fact]
    public void Parse_SplitsIntoSections()
    {
        var vin = Vin.Parse("1HGCM82633A004352");

        Assert.Equal("1HG", vin.Wmi);
        Assert.Equal("CM8263", vin.Vds);
        Assert.Equal("3A004352", vin.Vis);
        Assert.Equal('3', vin.CheckDigit);
        Assert.Equal('3', vin.ModelYearCode);
        Assert.Equal("1HGCM82633A004352", vin.ToString());
    }

    [Theory]
    [InlineData(" 1hgcm8-2633a004352 ")]
    [InlineData("1HG CM826 33A 004352")]
    [InlineData("1HG.CM826*33A_004352")]
    public void Parse_NormalizesCaseAndSeparators(string input)
    {
        Assert.True(Vin.TryParse(input, out var vin));
        Assert.Equal("1HGCM82633A004352", vin.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_RejectsEmpty(string? input)
    {
        Assert.False(Vin.TryParse(input, out _, out var error));
        Assert.Equal("VIN is empty.", error);
    }

    [Theory]
    [InlineData("1HGCM82633A00435")]
    [InlineData("1HGCM82633A0043521")]
    public void TryParse_RejectsWrongLength(string input)
    {
        Assert.False(Vin.TryParse(input, out _, out var error));
        Assert.Contains("17 characters", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1HGCM82633AO04352", 'O')]
    [InlineData("1HGCM82633AI04352", 'I')]
    [InlineData("1HGCM82633AQ04352", 'Q')]
    public void TryParse_RejectsLettersIOQ(string input, char letter)
    {
        Assert.False(Vin.TryParse(input, out _, out var error));
        Assert.Contains($"'{letter}'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RejectsNonAlphanumeric()
    {
        Assert.False(Vin.TryParse("1HGCM82633A00435#", out _, out var error));
        Assert.Contains("Invalid character", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ThrowsFormatExceptionForInvalidInput()
    {
        Assert.Throws<FormatException>(() => Vin.Parse("NOTAVIN"));
    }

    [Fact]
    public void ModelYear_DigitInPosition7MeansEarlierCycle()
    {
        var vin = Vin.Parse("1HGCM82633A004352");

        Assert.Equal([2003, 2033], vin.CandidateModelYears);
        Assert.Equal(2003, vin.EstimatedModelYear);
    }

    [Fact]
    public void ModelYear_LetterInPosition7MeansLaterCycle()
    {
        var vin = Vin.Parse("1FTFW1E59JFA00001");

        Assert.Equal([1988, 2018], vin.CandidateModelYears);
        Assert.Equal(2018, vin.EstimatedModelYear);
    }

    [Fact]
    public void ModelYear_NonNorthAmericanPrefersRecentYearNotInFuture()
    {
        var japanese = Vin.Parse("JM1BL1S54A1100001");
        var european = Vin.Parse("WVWZZZ1JZ3W386752");

        Assert.False(japanese.IsNorthAmerican);
        Assert.Equal(2010, japanese.EstimatedModelYear);
        Assert.Equal(2003, european.EstimatedModelYear);
    }

    [Fact]
    public void ModelYear_InvalidYearCodeYieldsNull()
    {
        // 'U' is not a valid position-10 model-year code.
        var vin = Vin.Parse("1HGCM8263UA004352");

        Assert.Empty(vin.CandidateModelYears);
        Assert.Null(vin.EstimatedModelYear);
    }

    [Theory]
    [InlineData("1HGCM82633A004352", "North America")]
    [InlineData("JM1BL1S54A1100001", "Asia")]
    [InlineData("WVWZZZ1JZ3W386752", "Europe")]
    [InlineData("9BWZZZ377VT004251", "South America")]
    [InlineData("6T1BF3EK5CU123456", "Oceania")]
    [InlineData("AAVZZZ6RZEU012345", "Africa")]
    public void RegionOfManufacture_FromFirstCharacter(string input, string region)
    {
        Assert.Equal(region, Vin.Parse(input).RegionOfManufacture);
    }

    [Fact]
    public void FindCandidates_FindsVinInOcrTextAndFixesConfusions()
    {
        // OCR read the zeros in "A004352" as the letter O.
        var found = Vin.FindCandidates("VIN: 1HGCM82633AOO4352 printed on door jamb");

        var vin = Assert.Single(found);
        Assert.Equal("1HGCM82633A004352", vin.Value);
    }

    [Fact]
    public void FindCandidates_SkipsNorthAmericanVinWithBadCheckDigit()
    {
        Assert.Empty(Vin.FindCandidates("1HGCM82643A004352"));
    }

    [Fact]
    public void FindCandidates_AcceptsNonNorthAmericanVinWithoutCheckDigit()
    {
        var vin = Assert.Single(Vin.FindCandidates("wvwzzz1jz3w386752"));
        Assert.Equal("WVWZZZ1JZ3W386752", vin.Value);
    }

    [Fact]
    public void FindCandidates_ReturnsEmptyForEmptyText()
    {
        Assert.Empty(Vin.FindCandidates(string.Empty));
    }

    [Fact]
    public void Vins_AreValueEqual()
    {
        Assert.Equal(Vin.Parse("1hgcm82633a004352"), Vin.Parse("1HGCM82633A004352"));
    }
}
