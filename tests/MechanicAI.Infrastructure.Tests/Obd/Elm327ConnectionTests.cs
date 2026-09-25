using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Infrastructure.Obd;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MechanicAI.Infrastructure.Tests.Obd;

public class Elm327ParsingTests
{
    [Fact]
    public void Clean_RemovesPromptBlankLinesAndStatusChatter()
    {
        var lines = Elm327Connection.Clean("SEARCHING...\r\n41 0C 1A F8\r\r BUS INIT: ...OK\r\n\r\n>");

        Assert.Equal(["41 0C 1A F8"], lines);
    }

    [Fact]
    public void ParsePayloads_OneLinePerEcu()
    {
        var payloads = Elm327Connection.ParsePayloads("41 0C 1A F8\r41 0C 1A F0\r\r>");

        Assert.Equal(2, payloads.Count);
        Assert.Equal(new byte[] { 0x41, 0x0C, 0x1A, 0xF8 }, payloads[0]);
        Assert.Equal(new byte[] { 0x41, 0x0C, 0x1A, 0xF0 }, payloads[1]);
    }

    [Fact]
    public void ParsePayloads_ReassemblesIsoTpMultiFrameResponse()
    {
        // Mode 09 PID 02 (VIN) over CAN: length line then numbered frames, possibly out of order.
        const string raw = "014\r1: 46 57 31 45 35 30 4A\r0: 49 02 01 31 46 54\r2: 46 41 30 30 30 30 31\r\r>";

        var payload = Assert.Single(Elm327Connection.ParsePayloads(raw));

        Assert.Equal(0x14, payload.Length);
        Assert.Equal(new byte[] { 0x49, 0x02, 0x01 }, payload[..3]);
        Assert.Equal("1FTFW1E50JFA00001", System.Text.Encoding.ASCII.GetString(payload[3..]));
    }

    [Theory]
    [InlineData("NO DATA\r\r>")]
    [InlineData("?\r\r>")]
    [InlineData("CAN ERROR\r\r>")]
    [InlineData("OK\r\r>")]
    [InlineData("\r\r>")]
    public void ParsePayloads_ReturnsNothingForErrorsAndAcknowledgements(string raw)
    {
        Assert.Empty(Elm327Connection.ParsePayloads(raw));
    }

    [Theory]
    [InlineData("410C1AF8", new byte[] { 0x41, 0x0C, 0x1A, 0xF8 })]
    [InlineData("41", new byte[] { 0x41 })]
    [InlineData("4", new byte[0])]
    [InlineData("ZZ", new byte[0])]
    [InlineData("", new byte[0])]
    public void HexToBytes(string hex, byte[] expected)
    {
        Assert.Equal(expected, Elm327Connection.HexToBytes(hex));
    }

    [Fact]
    public void ParseDtcPayloads_CanFramesSkipCountByteAndPadding()
    {
        byte[] payload = [0x43, 0x03, 0x01, 0x71, 0x03, 0x02, 0xC1, 0x00, 0x00, 0x00];

        var codes = Elm327Connection.ParseDtcPayloads([payload], 0x43, isCan: true, "Stored");

        Assert.Equal(["P0171", "P0302", "U0100"], codes.Select(c => c.Code));
        Assert.All(codes, c => Assert.Equal("Stored", c.Status));
    }

    [Fact]
    public void ParseDtcPayloads_LegacyFramesHaveNoCountByteAndAreDeduplicated()
    {
        byte[] first = [0x43, 0x01, 0x71, 0x03, 0x02, 0x00, 0x00];
        byte[] second = [0x43, 0x01, 0x71, 0x00, 0x00, 0x00, 0x00];
        byte[] otherService = [0x47, 0x04, 0x20, 0x00, 0x00, 0x00, 0x00];

        var codes = Elm327Connection.ParseDtcPayloads([first, second, otherService], 0x43, isCan: false, "Stored");

        Assert.Equal(["P0171", "P0302"], codes.Select(c => c.Code));
    }
}

public class Elm327SimulatorTests
{
    private static readonly ObdAdapterInfo HealthyAdapter = new("sim:healthy", "Simulator", ObdAdapterKind.Simulator, null, true);
    private static readonly ObdAdapterInfo LeanAdapter = new("sim:lean", "Simulator", ObdAdapterKind.Simulator, null, true);

    private static Task<Elm327Connection> Connect(SimulatorScenario scenario, ObdAdapterInfo adapter) =>
        Elm327Connection.OpenAsync(new SimulatedEcuTransport(scenario), adapter, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task OpenAsync_InitializesAdapterAndDetectsCanProtocol()
    {
        await using var connection = await Connect(SimulatorScenario.HealthyWarmUp, HealthyAdapter);

        Assert.True(connection.IsConnected);
        Assert.Contains("ELM327", connection.AdapterVersion, StringComparison.Ordinal);
        Assert.Contains("15765", connection.Protocol, StringComparison.Ordinal);
        Assert.Same(connection, connection.Scanner);
        Assert.Same(connection, connection.LiveData);
    }

    [Fact]
    public async Task SupportedPids_AreDecodedFromBitmaps()
    {
        await using var connection = await Connect(SimulatorScenario.HealthyWarmUp, HealthyAdapter);

        var supported = await connection.LiveData.GetSupportedPidsAsync(CancellationToken.None);

        Assert.Superset(new HashSet<string> { "RPM", "ECT", "STFT1", "LTFT1", "MAF", "O2S1B1", "FUEL_LVL", "BARO", "VPWR", "AAT" }, supported.ToHashSet());
        Assert.DoesNotContain("FP", supported);
        Assert.DoesNotContain("EOT", supported);
    }

    [Fact]
    public async Task ReadAsync_DecodesPlausibleLiveData()
    {
        await using var connection = await Connect(SimulatorScenario.HealthyWarmUp, HealthyAdapter);
        await connection.LiveData.GetSupportedPidsAsync(CancellationToken.None);

        var readings = (await connection.LiveData.ReadAsync(["RPM", "ECT", "VPWR", "BARO", "SPEED", "FP", "NOT_A_PID"], CancellationToken.None))
            .ToDictionary(r => r.Key, r => r.Value);

        Assert.Equal(["RPM", "ECT", "VPWR", "BARO", "SPEED"], readings.Keys);
        Assert.InRange(readings["RPM"], 600, 2600);
        Assert.InRange(readings["ECT"], 40, 95);
        Assert.InRange(readings["VPWR"], 14.0, 14.4);
        Assert.Equal(100, readings["BARO"]);
        Assert.Equal(0, readings["SPEED"]);
    }

    [Fact]
    public async Task HealthyEngine_HasNoCodesOrFreezeFrameAndNoVin()
    {
        await using var connection = await Connect(SimulatorScenario.HealthyWarmUp, HealthyAdapter);
        var scanner = connection.Scanner;

        Assert.Empty(await scanner.ReadStoredDtcsAsync(CancellationToken.None));
        Assert.Empty(await scanner.ReadPendingDtcsAsync(CancellationToken.None));
        Assert.Empty(await scanner.ReadPermanentDtcsAsync(CancellationToken.None));
        Assert.Null(await scanner.ReadFreezeFrameAsync(CancellationToken.None));
        Assert.Null(await scanner.ReadVinAsync(CancellationToken.None)); // the simulator never pretends to be a real vehicle

        var monitors = await scanner.ReadMonitorStatusAsync(CancellationToken.None);
        Assert.NotNull(monitors);
        Assert.False(monitors.MilOn);
        Assert.Equal(0, monitors.DtcCount);
        Assert.Contains(monitors.Monitors, m => m.Monitor == "Misfire" && m.Available);
    }

    [Fact]
    public async Task LeanScenario_ReportsP0171WithFreezeFrameAndClears()
    {
        await using var connection = await Connect(SimulatorScenario.LeanVacuumLeak, LeanAdapter);
        var scanner = connection.Scanner;

        var stored = Assert.Single(await scanner.ReadStoredDtcsAsync(CancellationToken.None));
        Assert.Equal(new ScannedDtc("P0171", "Stored", null), stored);
        Assert.Equal("P0171", Assert.Single(await scanner.ReadPermanentDtcsAsync(CancellationToken.None)).Code);

        var monitors = await scanner.ReadMonitorStatusAsync(CancellationToken.None);
        Assert.True(monitors!.MilOn);
        Assert.Equal(1, monitors.DtcCount);

        var freezeFrame = await scanner.ReadFreezeFrameAsync(CancellationToken.None);
        Assert.NotNull(freezeFrame);
        Assert.Equal("P0171", freezeFrame.TriggerDtc);
        Assert.Contains("Engine speed", freezeFrame.Values.Keys);
        Assert.EndsWith("rpm", freezeFrame.Values["Engine speed"], StringComparison.Ordinal);

        Assert.True(await scanner.ClearDtcsAsync(CancellationToken.None));
        Assert.Empty(await scanner.ReadStoredDtcsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LeanScenario_ShowsPositiveFuelTrim()
    {
        await using var connection = await Connect(SimulatorScenario.LeanVacuumLeak, LeanAdapter);

        var readings = await connection.LiveData.ReadAsync(["LTFT1"], CancellationToken.None);

        Assert.True(Assert.Single(readings).Value > 5);
    }

    [Fact]
    public async Task SendRawAsync_ReturnsCleanedResponse()
    {
        await using var connection = await Connect(SimulatorScenario.HealthyWarmUp, HealthyAdapter);

        Assert.Equal("14.2V", await connection.SendRawAsync("AT RV", CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_ClosesTransport()
    {
        var transport = new SimulatedEcuTransport(SimulatorScenario.HealthyWarmUp);
        var connection = await Elm327Connection.OpenAsync(transport, HealthyAdapter, NullLogger.Instance, CancellationToken.None);

        await connection.DisposeAsync();

        Assert.False(transport.IsOpen);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task OpenAsync_RejectsDevicesThatAreNotElm327()
    {
        var transport = Substitute.For<IObdTransport>();
        transport.ReadUntilPromptAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns("garbage\r>");

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() =>
            Elm327Connection.OpenAsync(transport, HealthyAdapter, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(ErrorKind.Unavailable, ex.Kind);
        await transport.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_ReportsWhenVehicleDoesNotRespond()
    {
        var transport = Substitute.For<IObdTransport>();
        var last = string.Empty;
        transport.WriteLineAsync(Arg.Do<string>(c => last = c), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        transport.ReadUntilPromptAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => last switch
            {
                "ATZ" => "ELM327 v1.5\r>",
                "0100" => "SEARCHING...\rUNABLE TO CONNECT\r>",
                _ => "OK\r>",
            });

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() =>
            Elm327Connection.OpenAsync(transport, HealthyAdapter, NullLogger.Instance, CancellationToken.None));

        Assert.Contains("ignition", ex.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatorProvider_OffersLabeledAdaptersAndConnects()
    {
        var provider = new SimulatorObdProvider(NullLogger<SimulatorObdProvider>.Instance);

        var adapters = await provider.DiscoverAsync(CancellationToken.None);

        Assert.Equal(2, adapters.Count);
        Assert.All(adapters, a =>
        {
            Assert.True(a.IsSimulator);
            Assert.Contains("SIMULATOR", a.DisplayName, StringComparison.Ordinal);
            Assert.True(provider.CanConnect(a));
        });
        Assert.False(provider.CanConnect(new ObdAdapterInfo("COM3", "USB", ObdAdapterKind.SerialUsb, "COM3", false)));

        await using var connection = await provider.ConnectAsync(adapters.Single(a => a.Id == "sim:lean"), CancellationToken.None);
        Assert.NotEmpty(await connection.Scanner.ReadStoredDtcsAsync(CancellationToken.None));
    }
}
