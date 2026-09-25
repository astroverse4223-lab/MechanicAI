using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Search;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;
using MechanicAI.Presentation.ViewModels;

namespace MechanicAI.Presentation.Tests;

public sealed class SearchActionRouterTests
{
    [Fact]
    public void Routes_every_action_kind_to_a_page()
    {
        foreach (var kind in Enum.GetValues<SearchActionKind>())
        {
            var (page, _) = SearchActionRouter.Route(new SearchAction(kind, "label", "x"));
            Assert.True(Enum.IsDefined(page));
        }
    }

    [Fact]
    public void Document_arguments_are_parsed_into_document_and_page()
    {
        var id = Guid.NewGuid();
        var (page, parameter) = SearchActionRouter.Route(new SearchAction(SearchActionKind.OpenWiring, "Open", $"{id}|12|{Guid.NewGuid()}"));

        Assert.Equal(PageKey.Wiring, page);
        var open = Assert.IsType<NavigationParameters.OpenDocument>(parameter);
        Assert.Equal(id, open.DocumentId);
        Assert.Equal(12, open.PageNumber);
    }

    [Fact]
    public void Diagnosis_and_dtc_actions_carry_their_text()
    {
        var (page, parameter) = SearchActionRouter.Route(new SearchAction(SearchActionKind.StartDiagnosis, "Start", "2017 Silverado P0171"));
        Assert.Equal(PageKey.Diagnostics, page);
        Assert.Equal("2017 Silverado P0171", Assert.IsType<NavigationParameters.StartDiagnosisFromText>(parameter).Text);

        var (dtcPage, code) = SearchActionRouter.Route(new SearchAction(SearchActionKind.OpenDtc, "Open", "P0301"));
        Assert.Equal(PageKey.Dtc, dtcPage);
        Assert.Equal("P0301", code);
    }

    [Fact]
    public void Invalid_ids_navigate_without_a_parameter()
    {
        var (page, parameter) = SearchActionRouter.Route(new SearchAction(SearchActionKind.OpenSession, "Open", "not-a-guid"));
        Assert.Equal(PageKey.Diagnostics, page);
        Assert.Null(parameter);
    }
}

public sealed class ShellViewModelTests
{
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly ISearchGateway _search = Substitute.For<ISearchGateway>();
    private readonly IConnectivityMonitor _connectivity = Substitute.For<IConnectivityMonitor>();
    private readonly ISystemGateway _system = Substitute.For<ISystemGateway>();
    private readonly IVehicleGateway _vehicles = Substitute.For<IVehicleGateway>();
    private readonly InMemorySettingsStore _settings = new();

    private ShellViewModel Create() => new(_navigation, _search, _connectivity, _settings, _system, _vehicles, new ImmediateDispatcher());

    [Fact]
    public async Task Initialize_navigates_home_and_locks_when_sign_in_is_required()
    {
        _system.IsSignInRequired.Returns(true);
        _system.HasPasswordAsync(Arg.Any<CancellationToken>()).Returns(true);
        _navigation.CurrentPage.Returns((PageKey?)null);
        var vm = Create();

        await vm.InitializeAsync();

        Assert.True(vm.IsLocked);
        _navigation.Received(1).NavigateTo(PageKey.Home, null);
    }

    [Fact]
    public async Task Unlock_with_wrong_password_shows_the_error_and_stays_locked()
    {
        _system.IsSignInRequired.Returns(true);
        _system.HasPasswordAsync(Arg.Any<CancellationToken>()).Returns(true);
        _system.SignInAsync("wrong", Arg.Any<CancellationToken>()).Returns(Result.Failure(new Error(ErrorKind.Unauthorized, "Incorrect password.")));
        var vm = Create();
        await vm.InitializeAsync();

        Assert.False(vm.UnlockCommand.CanExecute(null));
        vm.Password = "wrong";
        await vm.UnlockCommand.ExecuteAsync(null);

        Assert.True(vm.IsLocked);
        Assert.Equal("Incorrect password.", vm.UnlockError);
        Assert.Equal(string.Empty, vm.Password);
    }

    [Fact]
    public async Task Unlock_with_correct_password_unlocks()
    {
        _system.SignInAsync("correct horse", Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = Create();
        vm.IsLocked = true;
        vm.Password = "correct horse";

        await vm.UnlockCommand.ExecuteAsync(null);

        Assert.False(vm.IsLocked);
    }

    [Fact]
    public void Navigated_event_updates_selection_and_back_state()
    {
        var vm = Create();
        _navigation.CanGoBack.Returns(true);

        _navigation.Navigated += Raise.Event<EventHandler<PageKey>>(_navigation, PageKey.Dtc);

        Assert.Equal(PageKey.Dtc, vm.SelectedPage);
        Assert.True(vm.CanGoBack);
    }

    [Fact]
    public void Connectivity_changes_are_reflected()
    {
        var vm = Create();

        _connectivity.ConnectivityChanged += Raise.Event<EventHandler<bool>>(_connectivity, false);

        Assert.False(vm.IsOnline);
        Assert.StartsWith("Offline", vm.ConnectivityText);
    }

    [Fact]
    public async Task Submitting_a_query_navigates_to_the_best_action()
    {
        var parsed = QueryParser.Parse("P0301");
        _search.SearchAsync("P0301", false, Arg.Any<CancellationToken>()).Returns(new UniversalSearchResults(parsed,
            [new SearchAction(SearchActionKind.OpenDtc, "Open P0301", "P0301"), new SearchAction(SearchActionKind.AskAssistant, "Ask", "P0301")],
            [], null, null));
        var vm = Create();

        await vm.SubmitSearchAsync(null, "P0301");

        _navigation.Received(1).NavigateTo(PageKey.Dtc, "P0301");
    }

    [Fact]
    public async Task Typing_offers_parsed_actions_without_searching_or_recording_history()
    {
        var vm = Create();

        await vm.UpdateSuggestionsAsync("P0301 misfire");

        Assert.Contains(vm.Suggestions, s => s.Action.Kind == SearchActionKind.OpenDtc && s.Action.Argument == "P0301");
        Assert.Contains(vm.Suggestions, s => s.Action.Kind == SearchActionKind.StartDiagnosis);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);

        await vm.UpdateSuggestionsAsync("P");
        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task Choosing_a_suggestion_navigates_directly()
    {
        var vm = Create();
        var suggestion = new SearchSuggestion("Ask", null, "", new SearchAction(SearchActionKind.AskAssistant, "Ask", "why"));

        await vm.SubmitSearchAsync(suggestion, "ignored");

        _navigation.Received(1).NavigateTo(PageKey.Assistant, Arg.Is<object?>(new NavigationParameters.AskAssistant("why")));
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);
    }
}

public sealed class HomeViewModelTests
{
    [Fact]
    public void Quick_diagnosis_requires_text_and_navigates_with_the_active_vehicle()
    {
        var navigation = Substitute.For<INavigationService>();
        var settings = new InMemorySettingsStore();
        var vehicleId = Guid.NewGuid();
        settings.Current.ActiveVehicleId = vehicleId;
        var vm = new HomeViewModel(Substitute.For<IVehicleGateway>(), Substitute.For<IDiagnosticsGateway>(), Substitute.For<IDtcGateway>(),
            Substitute.For<IKnowledgeBaseGateway>(), Substitute.For<ISystemGateway>(), Substitute.For<IConnectivityMonitor>(), settings, navigation,
            new FixedClock(DateTime.UtcNow));

        Assert.False(vm.StartDiagnosisCommand.CanExecute(null));
        vm.QuickText = "  2019 F-150 misfire P0302 ";
        Assert.True(vm.StartDiagnosisCommand.CanExecute(null));
        vm.StartDiagnosisCommand.Execute(null);

        navigation.Received(1).NavigateTo(PageKey.Diagnostics,
            Arg.Is<object?>(new NavigationParameters.StartDiagnosisFromText("2019 F-150 misfire P0302", vehicleId)));
        Assert.Equal(string.Empty, vm.QuickText);
    }

    [Fact]
    public async Task Load_populates_dashboard_and_reports_missing_ai()
    {
        var vehicles = Substitute.For<IVehicleGateway>();
        vehicles.RecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new Application.DTOs.VehicleSummary(Guid.NewGuid(), "2018 Honda Civic", "2.0L", null, 50000, DistanceUnit.Miles, null, null, 0, 1, false, false,
                VehicleDataSource.Manual),
        ]);
        var diagnostics = Substitute.For<IDiagnosticsGateway>();
        diagnostics.ListAsync(Arg.Any<int>(), null, false, Arg.Any<CancellationToken>()).Returns([]);
        var dtcs = Substitute.For<IDtcGateway>();
        dtcs.CountAsync(Arg.Any<CancellationToken>()).Returns(4200);
        var kb = Substitute.For<IKnowledgeBaseGateway>();
        kb.GetStatsAsync(Arg.Any<CancellationToken>()).Returns((0, 0, 0, 0));
        var system = Substitute.For<ISystemGateway>();
        system.GetAiStatusAsync(false, Arg.Any<CancellationToken>()).Returns(new Application.Abstractions.Ai.AiStatus(
            Application.Settings.AiMode.Local, false, null, null, null, false, false, null, ["Ollama is not running."]));
        var vm = new HomeViewModel(vehicles, diagnostics, dtcs, kb, system, Substitute.For<IConnectivityMonitor>(), new InMemorySettingsStore(),
            Substitute.For<INavigationService>(), new FixedClock(DateTime.UtcNow));

        await vm.LoadAsync();

        Assert.Single(vm.RecentVehicles);
        Assert.True(vm.HasRecentVehicles);
        Assert.False(vm.HasOpenSessions);
        Assert.False(vm.IsAiReady);
        Assert.Contains("No AI model", vm.AiStatusText);
        Assert.Contains("Ollama is not running.", vm.AiNotes);
        Assert.Contains("4,200", vm.DtcDatabaseText);
        Assert.True(vm.ShowWelcome);
        Assert.False(vm.IsNoticeOpen);
    }
}
