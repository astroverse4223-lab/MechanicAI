using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Domain.Tests.ValueObjects;

public class DtcCodeTests
{
    [Theory]
    [InlineData("P0302", "P0302")]
    [InlineData("p0302", "P0302")]
    [InlineData("  P 0302 ", "P0302")]
    [InlineData("U0100-87", "U0100")]
    [InlineData("P0302:00", "P0302")]
    [InlineData("B0001_1", "B0001")]
    [InlineData("C0035", "C0035")]
    public void TryParse_AcceptsValidCodesAndStripsSuffixes(string input, string expected)
    {
        Assert.True(DtcCode.TryParse(input, out var code));
        Assert.Equal(expected, code.Value);
        Assert.Equal(expected, code.ToString());
    }

    [Theory]
    [InlineData("P0A80", "P0A80")]
    [InlineData("p0aff", "P0AFF")]
    [InlineData("U3FFF", "U3FFF")]
    [InlineData("P2BAD", "P2BAD")]
    public void TryParse_AcceptsHexDigitsInLastThreePositions(string input, string expected)
    {
        Assert.True(DtcCode.TryParse(input, out var code));
        Assert.Equal(expected, code.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("P4302")]
    [InlineData("X0302")]
    [InlineData("P03G2")]
    [InlineData("P030")]
    [InlineData("P03021")]
    [InlineData("0302")]
    [InlineData("-P0302")]
    public void TryParse_RejectsInvalidCodes(string? input)
    {
        Assert.False(DtcCode.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_BareDigitsNeedAssumePowertrain()
    {
        Assert.False(DtcCode.TryParse("0302", out _));
        Assert.True(DtcCode.TryParse("0302", out var code, assumePowertrain: true));
        Assert.Equal("P0302", code.Value);
    }

    [Fact]
    public void TryParse_AssumePowertrainStillRequiresValidSecondDigit()
    {
        Assert.False(DtcCode.TryParse("4302", out _, assumePowertrain: true));
    }

    [Fact]
    public void Parse_ThrowsForInvalid()
    {
        Assert.Throws<FormatException>(() => DtcCode.Parse("not a code"));
    }

    [Fact]
    public void Codes_AreValueEqual()
    {
        Assert.Equal(DtcCode.Parse("p0171"), DtcCode.Parse("P0171"));
        Assert.NotEqual(DtcCode.Parse("P0171"), DtcCode.Parse("P0174"));
    }

    [Theory]
    [InlineData("P0302", DtcSystem.Powertrain, 'P')]
    [InlineData("B0001", DtcSystem.Body, 'B')]
    [InlineData("C0035", DtcSystem.Chassis, 'C')]
    [InlineData("U0100", DtcSystem.Network, 'U')]
    public void System_FromFirstLetter(string input, DtcSystem system, char letter)
    {
        var code = DtcCode.Parse(input);
        Assert.Equal(system, code.System);
        Assert.Equal(letter, code.SystemLetter);
    }

    [Theory]
    [InlineData("P0302", true)]
    [InlineData("P2096", true)]
    [InlineData("P1345", false)]
    [InlineData("P3000", false)]
    [InlineData("P33FF", false)]
    [InlineData("P3400", true)]
    [InlineData("P3A00", true)]
    [InlineData("P3FFF", true)]
    [InlineData("B0001", true)]
    [InlineData("B1000", false)]
    [InlineData("B2000", false)]
    [InlineData("C0035", true)]
    [InlineData("C1234", false)]
    [InlineData("U0100", true)]
    [InlineData("U1000", false)]
    [InlineData("U3000", false)]
    public void IsGeneric_FollowsSaeRanges(string input, bool generic)
    {
        var code = DtcCode.Parse(input);
        Assert.Equal(generic, code.IsGeneric);
        Assert.Equal(!generic, code.IsManufacturerSpecific);
    }

    [Theory]
    [InlineData("P0001", "Fuel and Air Metering and Auxiliary Emission Controls")]
    [InlineData("P0171", "Fuel and Air Metering")]
    [InlineData("P0201", "Fuel and Air Metering (Injector Circuit)")]
    [InlineData("P0302", "Ignition System or Misfire")]
    [InlineData("P0442", "Auxiliary Emission Controls")]
    [InlineData("P0505", "Vehicle Speed Controls and Idle Control System")]
    [InlineData("P0606", "Computer Output Circuit")]
    [InlineData("P0700", "Transmission")]
    [InlineData("P0840", "Transmission")]
    [InlineData("P0965", "Transmission")]
    [InlineData("P0A80", "Hybrid Propulsion")]
    [InlineData("P0C00", "Hybrid Propulsion")]
    [InlineData("P0D00", "Powertrain")]
    [InlineData("P2096", "Powertrain")]
    [InlineData("B0001", "Body")]
    [InlineData("C0035", "Chassis")]
    [InlineData("U0100", "Network Communication")]
    public void SubsystemDescription_UsesJ2012Grouping(string input, string expected)
    {
        Assert.Equal(expected, DtcCode.Parse(input).SubsystemDescription);
    }

    [Fact]
    public void ExtractAll_FindsDistinctCodesInOrder()
    {
        var text = "Scan showed P0302, p0171 and U0100-87; P0302 again. Hybrid P0A80.";

        var codes = DtcCode.ExtractAll(text).Select(c => c.Value).ToList();

        Assert.Equal(["P0302", "P0171", "U0100", "P0A80"], codes);
    }

    [Theory]
    [InlineData("XP0300 is not a code")]
    [InlineData("part number P03001")]
    [InlineData("P4300 is not valid")]
    [InlineData("no codes here")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractAll_IgnoresEmbeddedOrInvalidTokens(string? text)
    {
        Assert.Empty(DtcCode.ExtractAll(text));
    }
}
