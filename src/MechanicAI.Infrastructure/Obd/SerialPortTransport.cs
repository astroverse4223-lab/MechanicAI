using System.IO.Ports;
using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Obd;

/// <summary>
/// Serial link to an adapter. USB adapters (FTDI/CH340) and paired Bluetooth Classic (SPP)
/// adapters both appear as COM ports on Windows, so this covers both.
/// </summary>
public sealed class SerialPortTransport(string portName, int baudRate) : IObdTransport
{
    private SerialPort? _port;

    public string Description => $"{portName} @ {baudRate} baud";

    public bool IsSimulated => false;

    public bool IsOpen => _port?.IsOpen == true;

    public Task OpenAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII,
                NewLine = "\r",
                ReadTimeout = 500,
                WriteTimeout = 2000,
                DtrEnable = true,
                RtsEnable = true,
            };
            _port.Open();
            _port.DiscardInBuffer();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ExternalServiceException("OBD adapter", ErrorKind.Unavailable, $"{portName} is in use by another program.", ex);
        }
        catch (IOException ex)
        {
            throw new ExternalServiceException("OBD adapter", ErrorKind.Unavailable, $"{portName} could not be opened. Is the adapter connected?", ex);
        }
    }, cancellationToken);

    public Task WriteLineAsync(string command, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new InvalidOperationException("Port is not open.");
        return Task.Run(() =>
        {
            port.DiscardInBuffer();
            port.Write(command + "\r");
        }, cancellationToken);
    }

    public Task<string> ReadUntilPromptAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new InvalidOperationException("Port is not open.");
        return Task.Run(() =>
        {
            var sb = new StringBuilder();
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (port.BytesToRead > 0)
                {
                    sb.Append(port.ReadExisting());
                    if (sb.ToString().Contains('>', StringComparison.Ordinal)) return sb.ToString();
                }
                else
                {
                    Thread.Sleep(10);
                }
            }

            throw new ExternalServiceException("OBD adapter", ErrorKind.Timeout, "The adapter stopped responding.");
        }, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_port?.IsOpen == true) _port.Close();
        }
        catch (IOException)
        {
        }

        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Discovers COM ports and connects ELM327 adapters over them (auto-detecting baud rate).</summary>
public sealed class SerialObdProvider(ISettingsStore settings, ILogger<SerialObdProvider> logger) : IObdProvider
{
    private static readonly int[] BaudRates = [38400, 115200, 9600, 230400, 500000];

    public string Name => "Serial / USB / Bluetooth (SPP)";

    public Task<IReadOnlyList<ObdAdapterInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ObdAdapterInfo> ports = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ObdAdapterInfo($"serial:{p}", $"{p} — USB or Bluetooth serial adapter", ObdAdapterKind.SerialUsb, p, false))
            .ToList();
        return Task.FromResult(ports);
    }

    public bool CanConnect(ObdAdapterInfo adapter) => adapter.Id.StartsWith("serial:", StringComparison.Ordinal) && adapter.Port is not null;

    public async Task<IObdConnection> ConnectAsync(ObdAdapterInfo adapter, CancellationToken cancellationToken)
    {
        var preferred = settings.Current.LiveData.BaudRate;
        ExternalServiceException? last = null;
        foreach (var baud in BaudRates.Prepend(preferred).Distinct())
        {
            try
            {
                var connection = await Elm327Connection.OpenAsync(new SerialPortTransport(adapter.Port!, baud), adapter, logger, cancellationToken);
                if (baud != preferred || settings.Current.LiveData.LastPort != adapter.Port)
                {
                    await settings.UpdateAsync(s =>
                    {
                        s.LiveData.BaudRate = baud;
                        s.LiveData.LastPort = adapter.Port;
                    }, cancellationToken);
                }

                return connection;
            }
            catch (ExternalServiceException ex) when (ex.Kind is ErrorKind.Timeout or ErrorKind.Unavailable && !ex.UserMessage.Contains("vehicle", StringComparison.OrdinalIgnoreCase)
                                                     && !ex.UserMessage.Contains("in use", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                logger.LogDebug("No ELM327 response on {Port} at {Baud} baud", adapter.Port, baud);
            }
        }

        throw last ?? new ExternalServiceException("OBD adapter", ErrorKind.Unavailable, "No OBD adapter responded on this port.");
    }
}
