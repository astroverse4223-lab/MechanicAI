using System.Net.NetworkInformation;
using MechanicAI.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Platform;

/// <summary>
/// Tracks internet reachability: OS network-change events plus a lightweight periodic probe
/// (a network interface can be "up" without internet access, e.g. shop Wi-Fi captive portals).
/// </summary>
public sealed class ConnectivityMonitor : BackgroundService, IConnectivityMonitor
{
    private static readonly Uri[] Probes =
    [
        new("https://vpic.nhtsa.dot.gov/"),
        new("https://www.msftconnecttest.com/connecttest.txt"),
    ];

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ConnectivityMonitor> _logger;
    private volatile bool _isOnline = true;

    public ConnectivityMonitor(IHttpClientFactory httpFactory, ILogger<ConnectivityMonitor> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _isOnline = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (!e.IsAvailable) SetOnline(false);
            else _ = CheckNowAsync();
        };
    }

    public bool IsOnline => _isOnline;

    public DateTime? LastChangedUtc { get; private set; }

    public event EventHandler<bool>? ConnectivityChanged;

    public async Task<bool> CheckNowAsync(CancellationToken ct = default)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            SetOnline(false);
            return false;
        }

        var client = _httpFactory.CreateClient(nameof(ConnectivityMonitor));
        foreach (var probe in Probes)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                using var request = new HttpRequestMessage(HttpMethod.Head, probe);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                SetOnline(true);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
            }
        }

        SetOnline(false);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckNowAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connectivity probe failed");
            }

            try
            {
                await Task.Delay(_isOnline ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetOnline(bool online)
    {
        if (_isOnline == online) return;
        _isOnline = online;
        LastChangedUtc = DateTime.UtcNow;
        _logger.LogInformation("Connectivity changed: {State}", online ? "online" : "offline");
        ConnectivityChanged?.Invoke(this, online);
    }
}
