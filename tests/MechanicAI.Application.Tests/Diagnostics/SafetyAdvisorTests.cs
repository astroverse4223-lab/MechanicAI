using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Tests.Diagnostics;

public class SafetyAdvisorTests
{
    [Fact]
    public void EveryVocabularyTagHasAWarning()
    {
        Assert.All(SafetyTags.All, tag =>
        {
            var warning = SafetyAdvisor.Get(tag);
            Assert.NotNull(warning);
            Assert.Equal(tag, warning.Tag);
            Assert.False(string.IsNullOrWhiteSpace(warning.Message));
        });
    }

    [Fact]
    public void Get_IsCaseInsensitiveAndNullForUnknown()
    {
        Assert.Equal(SafetyTags.HighVoltage, SafetyAdvisor.Get("HIGH-VOLTAGE")?.Tag);
        Assert.Null(SafetyAdvisor.Get("unknown"));
    }

    [Fact]
    public void ForTags_DeduplicatesSkipsUnknownAndSortsBySeverity()
    {
        var warnings = SafetyAdvisor.ForTags(["fuel", "exhaust-gas", "unknown", "FUEL", "high-voltage"]);

        Assert.Equal([SafetyTags.HighVoltage, SafetyTags.Fuel, SafetyTags.ExhaustGas], warnings.Select(w => w.Tag));
        Assert.Equal([SafetySeverity.Danger, SafetySeverity.Warning, SafetySeverity.Caution], warnings.Select(w => w.Severity));
    }

    [Fact]
    public void DetectTags_FindsTopicsInProcedureText()
    {
        var tags = SafetyAdvisor.DetectTags("Relieve fuel pressure, then with the engine running remove the ignition coil connector.");

        Assert.Equal([SafetyTags.Fuel, SafetyTags.Rotating, SafetyTags.IgnitionVoltage], tags);
    }

    [Theory]
    [InlineData("Code P0A80: replace hybrid battery pack", SafetyTags.HighVoltage)]
    [InlineData("EV will not charge", SafetyTags.HighVoltage)]
    [InlineData("SRS light is on", SafetyTags.Airbag)]
    [InlineData("Raise the vehicle on a lift", SafetyTags.Lifting)]
    [InlineData("Use a spring compressor on the strut", SafetyTags.Compressed)]
    [InlineData("Perform a battery load test", SafetyTags.HighCurrent)]
    [InlineData("Bleed the caliper", SafetyTags.Brakes)]
    [InlineData("Recover the R-1234yf refrigerant", SafetyTags.Refrigerant)]
    [InlineData("Never open the radiator cap hot", SafetyTags.HotSurfaces)]
    public void DetectTags_RecognizesEachTopic(string text, string tag)
    {
        Assert.Contains(tag, SafetyAdvisor.DetectTags(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Check every connector for corrosion")]
    public void DetectTags_ReturnsNothingForBenignText(string? text)
    {
        Assert.Empty(SafetyAdvisor.DetectTags(text));
    }

    [Fact]
    public void ForText_CombinesDetectionAndWarnings()
    {
        var warnings = SafetyAdvisor.ForText("Disable the airbag before removing the clockspring");

        var warning = Assert.Single(warnings);
        Assert.Equal(SafetyTags.Airbag, warning.Tag);
        Assert.Equal(SafetySeverity.Danger, warning.Severity);
    }
}
