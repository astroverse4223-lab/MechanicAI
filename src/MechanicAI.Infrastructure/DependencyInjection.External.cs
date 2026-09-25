using System.Net;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Infrastructure.Ai;
using MechanicAI.Infrastructure.Documents;
using MechanicAI.Infrastructure.Imaging;
using MechanicAI.Infrastructure.Persistence;
using MechanicAI.Infrastructure.Vehicles;
using MechanicAI.Infrastructure.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace MechanicAI.Infrastructure;

public static partial class DependencyInjection
{
    private const string UserAgent = "MechanicAI/1.0 (automotive diagnostic workstation)";

    static partial void AddExternalServices(IServiceCollection services)
    {
        services.TryAddSingleton<IResponseCache, DbResponseCache>();

        // Vehicle data (NHTSA): short requests → standard resilience (retry with backoff,
        // circuit breaker, per-attempt and total timeouts, rate limiting).
        services.AddHttpClient(NhtsaVpicClient.HttpClientName, c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                c.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                o.Retry.MaxRetryAttempts = 2;
            });
        services.TryAddSingleton<NhtsaVpicClient>();
        services.TryAddSingleton<IVinDecoder>(sp => sp.GetRequiredService<NhtsaVpicClient>());
        services.TryAddSingleton<IVehicleCatalog>(sp => sp.GetRequiredService<NhtsaVpicClient>());
        services.TryAddSingleton<NhtsaSafetyClient>();
        services.TryAddSingleton<IRecallProvider>(sp => sp.GetRequiredService<NhtsaSafetyClient>());
        services.TryAddSingleton<IComplaintProvider>(sp => sp.GetRequiredService<NhtsaSafetyClient>());

        // Web search APIs.
        services.AddHttpClient(WebSearchProviderFactory.HttpClientName, c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                c.Timeout = TimeSpan.FromSeconds(60);
            })
            .AddStandardResilienceHandler(o =>
            {
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(20);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);
                o.Retry.MaxRetryAttempts = 1;
            });
        services.TryAddSingleton<IWebSearchProviderFactory, WebSearchProviderFactory>();

        // Page fetching: redirects are followed manually so every hop is re-validated (SSRF).
        services.AddHttpClient(WebPageFetcher.HttpClientName, c =>
            {
                c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                c.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
        services.TryAddSingleton<IWebPageFetcher, WebPageFetcher>();

        // AI: management calls are short; chat/embedding/pull streams are long-lived and are
        // bounded by per-request cancellation instead of HttpClient timeouts.
        services.AddHttpClient(OllamaManagement.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient(AiRouter.StreamingClientName, c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        });
        services.TryAddSingleton<OllamaManagement>();
        services.TryAddSingleton<IOllamaManagement>(sp => sp.GetRequiredService<OllamaManagement>());
        services.TryAddSingleton<IAiRouter, AiRouter>();

        // Documents & imaging.
        services.TryAddSingleton<PdfTextExtractor>();
        services.TryAddSingleton<PlainTextExtractor>();
        services.TryAddSingleton<IDocumentTextExtractor>(sp => new CompositeTextExtractor(
            [sp.GetRequiredService<PdfTextExtractor>(), sp.GetRequiredService<PlainTextExtractor>()]));
        services.TryAddSingleton<IBarcodeReader, ZxingBarcodeReader>();

        // OBD-II: serial (USB and Bluetooth SPP) adapters plus the clearly labeled simulator.
        services.AddSingleton<IObdProvider, Obd.SerialObdProvider>();
        services.AddSingleton<IObdProvider, Obd.SimulatorObdProvider>();
    }
}
