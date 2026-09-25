using MechanicAI.Application.Ai;
using MechanicAI.Application.Ai.Tools;
using MechanicAI.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MechanicAI.Application;

public static class DependencyInjection
{
    /// <summary>Registers the application layer (use cases, diagnostic engine, AI orchestration).</summary>
    public static IServiceCollection AddMechanicAiApplication(this IServiceCollection services)
    {
        // Core use cases (stateless apart from change events; contexts are created per operation).
        services.TryAddSingleton<VehicleService>();
        services.TryAddSingleton<DtcService>();
        services.TryAddSingleton<DiagnosticSessionService>();
        services.TryAddSingleton<HistoryService>();
        services.TryAddSingleton<SearchHistoryService>();
        services.TryAddSingleton<ShopService>();
        services.TryAddSingleton<ReportService>();
        services.TryAddSingleton<SampleDataService>();
        services.TryAddSingleton<WorkstationAuthService>();

        return services;
    }

    /// <summary>
    /// Registers workstation features that depend on local indexes, background processing,
    /// AI routing, and OBD providers (the desktop app).
    /// </summary>
    public static IServiceCollection AddMechanicAiWorkstation(this IServiceCollection services)
    {
        services.AddMechanicAiApplication();
        services.TryAddSingleton<ResearchService>();
        services.TryAddSingleton<KnowledgeBaseService>();
        services.TryAddSingleton<WiringService>();
        services.TryAddSingleton<ImageAnalysisService>();
        services.TryAddSingleton<LiveDataService>();
        services.TryAddSingleton<UniversalSearchService>();
        services.TryAddSingleton<AssistantService>();
        services.TryAddSingleton<DiagnosticAiAdvisor>();
        services.TryAddSingleton<TrainingService>();
        services.TryAddSingleton<ApprenticeService>();

        // AI tools — the only way the model can obtain facts.
        services.AddSingleton<IAiTool, DecodeVinTool>();
        services.AddSingleton<IAiTool, SearchVehicleTool>();
        services.AddSingleton<IAiTool, SearchDtcTool>();
        services.AddSingleton<IAiTool, SearchWebTool>();
        services.AddSingleton<IAiTool, SearchRecallsTool>();
        services.AddSingleton<IAiTool, SearchDocumentsTool>();
        services.AddSingleton<IAiTool, SearchKnowledgeBaseTool>();
        services.AddSingleton<IAiTool, AnalyzeImageTool>();
        services.AddSingleton<IAiTool, SearchWiringTool>();
        services.AddSingleton<IAiTool, AnalyzeLiveDataTool>();
        services.AddSingleton<IAiTool, GetVehicleHistoryTool>();
        services.AddSingleton<IAiTool, GetDiagnosticSessionTool>();
        services.AddSingleton<IAiTool, SaveDiagnosticStepTool>();
        services.TryAddSingleton<AgentRunner>();
        return services;
    }
}
