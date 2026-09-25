using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MechanicAI.Presentation;

public static class DependencyInjection
{
    /// <summary>
    /// Registers gateways and view models. The host must also register the application
    /// workstation services (<c>AddMechanicAiWorkstation</c>), infrastructure, and the UI services
    /// (<see cref="Abstractions.INavigationService"/>, <see cref="Abstractions.IDialogService"/>,
    /// <see cref="Abstractions.IDispatcher"/>, <see cref="Abstractions.IFilePicker"/>,
    /// <see cref="Abstractions.IClipboard"/>, <see cref="Abstractions.ILauncher"/>).
    /// </summary>
    /// <remarks>
    /// View models are singletons: pages are cached, and state such as the open diagnostic
    /// session or a live-data stream survives navigating between pages.
    /// </remarks>
    public static IServiceCollection AddMechanicAiPresentation(this IServiceCollection services)
    {
        services.TryAddSingleton<IVehicleGateway, VehicleGateway>();
        services.TryAddSingleton<IDiagnosticsGateway, DiagnosticsGateway>();
        services.TryAddSingleton<IDtcGateway, DtcGateway>();
        services.TryAddSingleton<ILiveDataGateway, LiveDataGateway>();
        services.TryAddSingleton<IKnowledgeBaseGateway, KnowledgeBaseGateway>();
        services.TryAddSingleton<IWiringGateway, WiringGateway>();
        services.TryAddSingleton<IResearchGateway, ResearchGateway>();
        services.TryAddSingleton<IAssistantGateway, AssistantGateway>();
        services.TryAddSingleton<ITrainingGateway, TrainingGateway>();
        services.TryAddSingleton<IApprenticeGateway, ApprenticeGateway>();
        services.TryAddSingleton<IShopGateway, ShopGateway>();
        services.TryAddSingleton<IHistoryGateway, HistoryGateway>();
        services.TryAddSingleton<ISearchGateway, SearchGateway>();
        services.TryAddSingleton<ISystemGateway, SystemGateway>();

        services.TryAddSingleton<ShellViewModel>();
        services.TryAddSingleton<HomeViewModel>();
        services.TryAddSingleton<VehiclesViewModel>();
        services.TryAddSingleton<DiagnosticsViewModel>();
        services.TryAddSingleton<DtcLookupViewModel>();
        services.TryAddSingleton<LiveDataViewModel>();
        services.TryAddSingleton<KnowledgeBaseViewModel>();
        services.TryAddSingleton<WiringViewModel>();
        services.TryAddSingleton<ResearchViewModel>();
        services.TryAddSingleton<AssistantViewModel>();
        services.TryAddSingleton<TrainingViewModel>();
        services.TryAddSingleton<ApprenticeViewModel>();
        services.TryAddSingleton<ShopViewModel>();
        services.TryAddSingleton<HistoryViewModel>();
        services.TryAddSingleton<SettingsViewModel>();
        return services;
    }
}
