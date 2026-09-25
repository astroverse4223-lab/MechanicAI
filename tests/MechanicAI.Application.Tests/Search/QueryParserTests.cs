using MechanicAI.Application.Search;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Tests.Search;

public class QueryParserTests
{
    [Fact]
    public void Parse_FullDiagnosticQuery()
    {
        var q = QueryParser.Parse("2017 Silverado 5.3 P0300 misfire at idle 150k miles");

        Assert.Equal(["P0300"], q.Dtcs);
        Assert.Equal(2017, q.Year);
        Assert.Equal("Chevrolet", q.Make);
        Assert.Equal("Silverado", q.Model);
        Assert.Equal(5.3m, q.DisplacementLiters);
        Assert.Equal("5.3L", q.Engine);
        Assert.Equal(150_000, q.Mileage);
        Assert.Equal(["Misfire"], q.Symptoms);
        Assert.Contains("At idle", q.Conditions);
        Assert.True(q.HasVehicle);
        Assert.True(q.LooksLikeDiagnosis);
        Assert.Equal("2017 Chevrolet Silverado 5.3L", q.VehicleDescription);
        Assert.Equal(SearchIntent.Diagnostic, q.PrimaryIntent);
        Assert.Contains(q.Intents, i => i.Intent == SearchIntent.Dtc);
        Assert.DoesNotContain("P0300", q.Remainder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Silverado", q.Remainder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_CodeOnlyQueryIsDtcLookup()
    {
        var q = QueryParser.Parse("p0171");

        Assert.Equal(["P0171"], q.Dtcs);
        Assert.Equal(string.Empty, q.Remainder);
        Assert.Equal(SearchIntent.Dtc, q.PrimaryIntent);
        Assert.Equal(0.95, q.Intents[0].Score);
    }

    [Fact]
    public void Parse_BareFourDigitCodeIsAssumedPowertrain()
    {
        Assert.Equal(["P0302"], QueryParser.Parse("0302").Dtcs);
        Assert.Empty(QueryParser.Parse("1302").Dtcs);
    }

    [Fact]
    public void Parse_Vin()
    {
        var q = QueryParser.Parse("decode 1FTFW1E59JFA00001");

        Assert.Equal("1FTFW1E59JFA00001", q.Vin);
        Assert.Empty(q.Dtcs);
        Assert.Null(q.Year);
        Assert.Equal(SearchIntent.Vin, q.PrimaryIntent);
    }

    [Theory]
    [InlineData("2008 ram 1500 hemi", "Dodge")]
    [InlineData("2015 ram 1500 hemi", "Ram")]
    public void Parse_RamMakeDependsOnYear(string query, string make)
    {
        var q = QueryParser.Parse(query);

        Assert.Equal(make, q.Make);
        Assert.Equal("Ram 1500", q.Model);
        Assert.Equal("HEMI", q.Engine);
    }

    [Fact]
    public void Parse_PrefersLongestModelName()
    {
        var q = QueryParser.Parse("2014 jeep grand cherokee 3.6 pentastar");

        Assert.Equal("Jeep", q.Make);
        Assert.Equal("Grand Cherokee", q.Model);
        Assert.Equal("3.6L Pentastar", q.Engine);
    }

    [Fact]
    public void Parse_CompactModelAliasesAndCylinderLayout()
    {
        var q = QueryParser.Parse("f150 v8 won't start");

        Assert.Equal("Ford", q.Make);
        Assert.Equal("F-150", q.Model);
        Assert.Equal("V8", q.Engine);
        Assert.Contains("No start", q.Symptoms);
    }

    [Fact]
    public void Parse_VoltageIsNotMistakenForDisplacement()
    {
        var q = QueryParser.Parse("o2 sensor reads 0.8 v at idle");

        Assert.Null(q.DisplacementLiters);
        Assert.Null(q.Engine);
    }

    [Theory]
    [InlineData("142,000 miles", 142_000)]
    [InlineData("98k mi", 98_000)]
    [InlineData("at 120k", 120_000)]
    [InlineData("5 miles", null)]
    public void Parse_Mileage(string query, int? expected)
    {
        Assert.Equal(expected, QueryParser.Parse(query).Mileage);
    }

    [Fact]
    public void Parse_ShortSymptomWordsNeedWordBoundaries()
    {
        Assert.DoesNotContain("Check engine light", QueryParser.Parse("customer wants to cancel").Symptoms);
        Assert.Contains("Check engine light", QueryParser.Parse("CEL is on").Symptoms);
    }

    [Fact]
    public void Parse_ComponentAndRepairIntent()
    {
        var q = QueryParser.Parse("how to replace ignition coil");

        Assert.Contains("ignition coil", q.Components);
        Assert.Equal(SearchIntent.Repair, q.PrimaryIntent);
        Assert.False(q.HasVehicle);
    }

    [Fact]
    public void Parse_ComponentLocationIntent()
    {
        var q = QueryParser.Parse("where is the crank sensor on a 2012 civic");

        Assert.Equal("Honda", q.Make);
        Assert.Equal("Civic", q.Model);
        Assert.Equal(SearchIntent.Component, q.PrimaryIntent);
    }

    [Fact]
    public void Parse_WiringIntent()
    {
        var q = QueryParser.Parse("wiring diagram for fuel pump relay");

        Assert.Equal(SearchIntent.Wiring, q.PrimaryIntent);
        Assert.Contains("fuel pump relay", q.Components);
    }

    [Fact]
    public void Parse_VehicleOnlyQuery()
    {
        var q = QueryParser.Parse("2019 toyota camry");

        Assert.Equal(SearchIntent.Vehicle, q.PrimaryIntent);
        Assert.Equal(0.8, q.Intents[0].Score);
        Assert.False(q.LooksLikeDiagnosis);
    }

    [Fact]
    public void Parse_EmptyInputFallsBackToWeb()
    {
        var q = QueryParser.Parse(null);

        Assert.Equal(string.Empty, q.Original);
        Assert.Equal(SearchIntent.Web, q.PrimaryIntent);
        Assert.False(q.HasVehicle);
    }

    [Fact]
    public void Classify_WebIsAlwaysAFallbackAndScoresAreCapped()
    {
        var parsed = new ParsedQuery { Original = "x", Dtcs = ["P0300"], Symptoms = ["a", "b", "c", "d"], Remainder = "rough" };

        var intents = IntentClassifier.Classify(parsed, "what should i check to diagnose this problem issue test");

        Assert.Contains(intents, i => i.Intent == SearchIntent.Web);
        Assert.All(intents, i => Assert.InRange(i.Score, 0, 1));
        Assert.Equal((SearchIntent.Diagnostic, 1.0), intents[0]);
    }
}

public class VehicleLexiconTests
{
    [Theory]
    [InlineData(2010, "Dodge")]
    [InlineData(2011, "Ram")]
    [InlineData(null, "Ram")]
    public void ResolveRamMake_SplitAt2011(int? year, string expected)
    {
        Assert.Equal(expected, VehicleLexicon.ResolveRamMake(year));
    }

    [Fact]
    public void Models_IncludeCompactAliases()
    {
        Assert.Equal(("CR-V", "Honda"), VehicleLexicon.Models["crv"]);
        Assert.Equal(("CR-V", "Honda"), VehicleLexicon.Models["CR-V"]);
        Assert.Equal(("Silverado 2500HD", "Chevrolet"), VehicleLexicon.Models["silverado2500hd"]);
    }

    [Fact]
    public void MakeAliasesAndEngineFamilies()
    {
        Assert.Equal("Chevrolet", VehicleLexicon.MakeAliases["Chevy"]);
        Assert.Equal("Mercedes-Benz", VehicleLexicon.MakeAliases["benz"]);
        Assert.Equal("Power Stroke diesel", VehicleLexicon.EngineFamilies["powerstroke"]);
        Assert.All(VehicleLexicon.Models.Values, v => Assert.False(string.IsNullOrWhiteSpace(v.Make)));
    }
}
