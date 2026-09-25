using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Search;
using MechanicAI.Application.Services;
using MechanicAI.Application.Settings;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Main window: navigation menu, universal search, connectivity, active vehicle, and the sign-in lock.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly ISearchGateway _search;
    private readonly IConnectivityMonitor _connectivity;
    private readonly ISettingsStore _settings;
    private readonly ISystemGateway _system;
    private readonly IVehicleGateway _vehicles;
    private readonly IDispatcher _dispatcher;

    public ShellViewModel(
        INavigationService navigation,
        ISearchGateway search,
        IConnectivityMonitor connectivity,
        ISettingsStore settings,
        ISystemGateway system,
        IVehicleGateway vehicles,
        IDispatcher dispatcher)
    {
        _navigation = navigation;
        _search = search;
        _connectivity = connectivity;
        _settings = settings;
        _system = system;
        _vehicles = vehicles;
        _dispatcher = dispatcher;

        _navigation.Navigated += (_, page) =>
        {
            SelectedPage = page;
            CanGoBack = _navigation.CanGoBack;
        };
        _connectivity.ConnectivityChanged += (_, online) => _dispatcher.Run(() => IsOnline = online);
        _settings.Changed += (_, s) => _dispatcher.Run(() => _ = RefreshActiveVehicleAsync(s));
        IsOnline = _connectivity.IsOnline;
    }

    public IReadOnlyList<NavigationItem> NavigationItems => NavigationCatalog.Items;

    public ObservableCollection<SearchSuggestion> Suggestions { get; } = [];

    [ObservableProperty]
    public partial PageKey? SelectedPage { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectivityText))]
    [NotifyPropertyChangedFor(nameof(ConnectivityGlyph))]
    public partial bool IsOnline { get; set; }

    public string ConnectivityText => IsOnline ? "Online" : "Offline — local features only";

    public string ConnectivityGlyph => IsOnline ? "" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveVehicle))]
    public partial string? ActiveVehicleName { get; set; }

    public bool HasActiveVehicle => !string.IsNullOrWhiteSpace(ActiveVehicleName);

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLocked { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? UnlockError { get; set; }

    /// <summary>True until the database is ready and the first page is shown.</summary>
    [ObservableProperty]
    public partial bool IsStarting { get; set; } = true;

    [ObservableProperty]
    public partial string StartupMessage { get; set; } = "Preparing the local database…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupError))]
    public partial string? StartupError { get; set; }

    public bool HasStartupError => !string.IsNullOrWhiteSpace(StartupError);

    /// <summary>Called once the database is ready: applies the sign-in lock and opens the home page.</summary>
    public async Task InitializeAsync()
    {
        IsLocked = _system.IsSignInRequired && await _system.HasPasswordAsync(CancellationToken.None);
        await RefreshActiveVehicleAsync(_settings.Current);
        IsStarting = false;
        if (_navigation.CurrentPage is null) _navigation.NavigateTo(PageKey.Home);
    }

    /// <summary>Shows a fatal startup problem (the app stays open so the technician can read it).</summary>
    public void ReportStartupFailure(string message)
    {
        StartupMessage = "Mechanic AI could not start.";
        StartupError = message;
    }

    [RelayCommand]
    private void Navigate(PageKey page)
    {
        if (_navigation.CurrentPage != page) _navigation.NavigateTo(page);
    }

    [RelayCommand]
    private void GoBack() => _navigation.GoBack();

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        var result = await _system.SignInAsync(Password, CancellationToken.None);
        Password = string.Empty;
        if (result.IsSuccess)
        {
            UnlockError = null;
            IsLocked = false;
        }
        else
        {
            UnlockError = result.Error?.Message ?? "Sign-in failed.";
        }
    }

    private bool CanUnlock() => !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Refreshes suggestions for the text typed in the search box. Suggestions come from parsing
    /// the text (codes, VINs, vehicles, symptoms) — instant, offline, and without recording
    /// half-typed queries in search history. Submitting runs the full universal search.
    /// </summary>
    public Task UpdateSuggestionsAsync(string text)
    {
        SearchText = text;
        Suggestions.Clear();
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < 2) return Task.CompletedTask;
        var parsed = QueryParser.Parse(text.Trim());
        foreach (var action in UniversalSearchService.SuggestActions(parsed))
        {
            Suggestions.Add(new SearchSuggestion(action.Label, null, SearchActionRouter.Glyph(action.Kind), action));
        }

        return Task.CompletedTask;
    }

    /// <summary>Handles a chosen suggestion or a submitted query.</summary>
    public async Task SubmitSearchAsync(SearchSuggestion? chosen, string queryText)
    {
        if (chosen is not null)
        {
            ExecuteSuggestion(chosen);
            return;
        }

        if (string.IsNullOrWhiteSpace(queryText)) return;
        await RunAsync(async ct =>
        {
            var results = await _search.SearchAsync(queryText.Trim(), includeWeb: false, ct);
            var action = results.SuggestedActions.FirstOrDefault(a => a.Kind is not (SearchActionKind.AskAssistant or SearchActionKind.SearchWeb))
                         ?? results.Items.FirstOrDefault()?.PrimaryAction
                         ?? results.SuggestedActions.FirstOrDefault();
            if (action is null) return;
            var (page, parameter) = SearchActionRouter.Route(action);
            _navigation.NavigateTo(page, parameter);
        });
    }

    [RelayCommand]
    private void ExecuteSuggestion(SearchSuggestion suggestion)
    {
        var (page, parameter) = SearchActionRouter.Route(suggestion.Action);
        SearchText = string.Empty;
        Suggestions.Clear();
        _navigation.NavigateTo(page, parameter);
    }

    private async Task RefreshActiveVehicleAsync(AppSettings settings)
    {
        if (settings.ActiveVehicleId is not { } id)
        {
            ActiveVehicleName = null;
            return;
        }

        try
        {
            var vehicle = await _vehicles.GetAsync(id, CancellationToken.None);
            ActiveVehicleName = vehicle?.DisplayName;
        }
        catch (Exception ex)
        {
            OnUnexpectedException(ex);
        }
    }

    [RelayCommand]
    private void OpenActiveVehicle()
    {
        if (_settings.Current.ActiveVehicleId is { } id) _navigation.NavigateTo(PageKey.Vehicles, id);
    }
}
