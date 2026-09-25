using MechanicAI.Application.LiveData;

namespace MechanicAI.Application.Tests.LiveData;

public class ObdPidsTests
{
    private static double Decode(string key, params byte[] data) => ObdPids.Find(key)!.Decode(data);

    [Fact]
    public void Keys_AreUniqueAndRangesAreSane()
    {
        Assert.Equal(ObdPids.All.Count, ObdPids.All.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ObdPids.All, p =>
        {
            Assert.True(p.Min < p.Max, p.Key);
            Assert.InRange(p.DataBytes, 1, 4);
            _ = p.Decode([]); // decoding a short/empty payload must not throw
        });
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        Assert.Equal(0x0C, ObdPids.Find("rpm")!.Pid);
        Assert.Null(ObdPids.Find("NOPE"));
    }

    [Fact]
    public void ForPid_ReturnsEveryDefinitionSharingAPid()
    {
        Assert.Equal(["AFR_B1S1", "AFR_B1S1_mA"], ObdPids.ForPid(0x34).Select(p => p.Key));
    }

    [Theory]
    [InlineData("RPM", new byte[] { 0x1A, 0xF8 }, 1726)]
    [InlineData("ECT", new byte[] { 0x7B }, 83)]
    [InlineData("IAT", new byte[] { 0x00 }, -40)]
    [InlineData("STFT1", new byte[] { 0x80 }, 0)]
    [InlineData("LTFT2", new byte[] { 0x00 }, -100)]
    [InlineData("LOAD", new byte[] { 0xFF }, 100)]
    [InlineData("MAF", new byte[] { 0x01, 0x2C }, 3.0)]
    [InlineData("TIMING", new byte[] { 0x80 }, 0)]
    [InlineData("VPWR", new byte[] { 0x37, 0x6E }, 14.19)]
    [InlineData("FP", new byte[] { 0x64 }, 300)]
    [InlineData("O2S1B1", new byte[] { 0x5A, 0xFF }, 0.45)]
    [InlineData("AFR_B1S1", new byte[] { 0x80, 0x00, 0x80, 0x00 }, 1.0)]
    [InlineData("AFR_B1S1_mA", new byte[] { 0x80, 0x00, 0x80, 0x00 }, 0)]
    [InlineData("CAT_B1S1", new byte[] { 0x11, 0x94 }, 410)]
    [InlineData("FUEL_RATE", new byte[] { 0x00, 0x64 }, 5)]
    public void Decode_UsesJ1979Formulas(string key, byte[] data, double expected)
    {
        Assert.Equal(expected, Decode(key, data), 2);
    }

    [Fact]
    public void Decode_FuelTrimRange()
    {
        Assert.Equal(99.22, Decode("STFT1", 0xFF), 2);
    }

    [Fact]
    public void DecodeSupportBitmap_ListsSupportedPids()
    {
        // Classic example response to 01 00: 41 00 BE 1F A8 13
        var pids = ObdPids.DecodeSupportBitmap(0x00, [0xBE, 0x1F, 0xA8, 0x13]).ToList();

        Assert.Equal(new byte[] { 0x01, 0x03, 0x04, 0x05, 0x06, 0x07, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x13, 0x15, 0x1C, 0x1F, 0x20 }, pids);
    }

    [Fact]
    public void DecodeSupportBitmap_OffsetsByBasePidAndToleratesShortData()
    {
        Assert.Equal(new byte[] { 0x21, 0x40 }, ObdPids.DecodeSupportBitmap(0x20, [0x80, 0x00, 0x00, 0x01]).ToArray());
        Assert.Equal(new byte[] { 0x41 }, ObdPids.DecodeSupportBitmap(0x40, [0x80]).ToArray());
    }

    [Theory]
    [InlineData(0x01, 0x33, "P0133")]
    [InlineData(0x01, 0x71, "P0171")]
    [InlineData(0x0A, 0x80, "P0A80")]
    [InlineData(0x3A, 0x80, "P3A80")]
    [InlineData(0x41, 0x23, "C0123")]
    [InlineData(0x81, 0x00, "B0100")]
    [InlineData(0xC1, 0x00, "U0100")]
    [InlineData(0xD0, 0x01, "U1001")]
    public void DecodeDtc_UsesTopTwoBitsForSystem(byte high, byte low, string expected)
    {
        Assert.Equal(expected, ObdPids.DecodeDtc(high, low));
    }
}
