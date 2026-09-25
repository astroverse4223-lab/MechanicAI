using MechanicAI.Application;
using MechanicAI.Infrastructure;
using MechanicAI.Infrastructure.Persistence;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Presentation.Tests;

/// <summary>
/// Builds the same container as the desktop app (minus WinUI services, which are substituted)
/// against a real SQLite database in a temp folder, and exercises view models end to end.
/// </summary>
public sealed class CompositionTests : IAsyncLifetime
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "mechanicai-presentation-tests", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IHostApplicationLifetime>(Substitute.For<IHostApplicationLifetime>());
        services.AddMechanicAiInfrastructure(o => o.DataRoot = _dataRoot);
        services.AddMechanicAiWorkstation();
        services.AddMechanicAiPresentation();
        services.AddSingleton(Substitute.For<INavigationService>());
        services.AddSingleton(Substitute.For<IDialogService>());
        services.AddSingleton<IDispatcher, ImmediateDispatcher>();
        services.AddSingleton(Substitute.For<IFilePicker>());
        services.AddSingleton(Substitute.For<IClipboard>());
        services.AddSingleton(Substitute.For<ILauncher>());
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await _services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(typeof(ShellViewModel))]
    [InlineData(typeof(HomeViewModel))]
    [InlineData(typeof(VehiclesViewModel))]
    [InlineData(typeof(DiagnosticsViewModel))]
    [InlineData(typeof(DtcLookupViewModel))]
    [InlineData(typeof(LiveDataViewModel))]
    [InlineData(typeof(KnowledgeBaseViewModel))]
    [InlineData(typeof(WiringViewModel))]
    [InlineData(typeof(ResearchViewModel))]
    [InlineData(typeof(AssistantViewModel))]
    [InlineData(typeof(TrainingViewModel))]
    [InlineData(typeof(ApprenticeViewModel))]
    [InlineData(typeof(ShopViewModel))]
    [InlineData(typeof(HistoryViewModel))]
    [InlineData(typeof(SettingsViewModel))]
    public void Every_view_model_resolves(Type viewModelType)
    {
        Assert.NotNull(_services.GetRequiredService(viewModelType));
    }

    [Fact]
    public async Task Dtc_lookup_works_offline_against_seeded_reference_content()
    {
        var vm = _services.GetRequiredService<DtcLookupViewModel>();

        await vm.OnNavigatedToAsync("U0100");

        Assert.False(vm.IsNoticeOpen, vm.NoticeMessage);
        Assert.Equal("U0100", vm.DetailCode);
        Assert.False(vm.IsUnknownCode);
        Assert.NotEmpty(vm.Definitions);
    }

    [Fact]
    public async Task A_diagnostic_session_can_be_started_from_text_and_a_result_recorded()
    {
        var vm = _services.GetRequiredService<DiagnosticsViewModel>();

        await vm.OnNavigatedToAsync(new Navigation.NavigationParameters.StartDiagnosisFromText("2017 Chevrolet Silverado 5.3 P0442 small evap leak"));

        Assert.False(vm.IsNoticeOpen, vm.LastError?.Detail ?? vm.NoticeMessage);
        Assert.True(vm.HasSession);
        Assert.NotEmpty(vm.Causes);
        Assert.True(vm.ActiveTest is not null, $"{vm.StageLabel} causes={vm.Causes.Count} ranked={vm.RankedTests.Count} :: {string.Join(" | ", vm.Timeline.Select(t => t.Title + ": " + t.Detail))}");

        var before = vm.Timeline.Count;
        vm.SelectedOutcome = vm.ActiveTest!.Outcomes[0];
        await vm.RecordResultCommand.ExecuteAsync(null);

        Assert.False(vm.IsNoticeOpen, vm.LastError?.Detail ?? vm.NoticeMessage);
        Assert.Single(vm.CompletedTests);
        Assert.True(vm.Timeline.Count > before);

        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CompletedTests, t => t.IsCompleted);
    }

    [Fact]
    public async Task Home_dashboard_loads_with_an_empty_database()
    {
        var vm = _services.GetRequiredService<HomeViewModel>();

        await vm.LoadAsync();

        Assert.False(vm.HasRecentVehicles);
        Assert.Contains("definitions", vm.DtcDatabaseText);
        Assert.False(string.IsNullOrWhiteSpace(vm.AiStatusText));
    }

    [Fact]
    public async Task Vehicle_can_be_added_manually_and_appears_in_history()
    {
        var vehicles = _services.GetRequiredService<VehiclesViewModel>();
        await vehicles.OnNavigatedToAsync(null);
        vehicles.NewVehicleCommand.Execute(null);
        vehicles.EditYear = "2016";
        vehicles.EditMake = "Toyota";
        vehicles.EditModel = "Camry";
        await vehicles.SaveVehicleCommand.ExecuteAsync(null);

        Assert.Null(vehicles.EditorError);
        Assert.Equal("2016 Toyota Camry", vehicles.SelectedVehicle?.DisplayName);

        var history = _services.GetRequiredService<HistoryViewModel>();
        await history.OnNavigatedToAsync(vehicles.SelectedVehicle!.Id);
        Assert.Equal(vehicles.SelectedVehicle.Id, history.SelectedVehicle?.Id);
        Assert.False(history.IsNoticeOpen, history.NoticeMessage);
    }
}
