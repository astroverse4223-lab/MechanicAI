using MechanicAI.Application.Abstractions;
using MechanicAI.Desktop.Views;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Navigation;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace MechanicAI.Desktop.Services;

/// <summary>Frame-based navigation. Pages are cached; their view models receive the navigation parameter.</summary>
public sealed class NavigationService(ISettingsStore settings, ILogger<NavigationService> logger) : INavigationService
{
    private static readonly IReadOnlyDictionary<PageKey, Type> Pages = new Dictionary<PageKey, Type>
    {
        [PageKey.Home] = typeof(HomePage),
        [PageKey.Vehicles] = typeof(VehiclesPage),
        [PageKey.Diagnostics] = typeof(DiagnosticsPage),
        [PageKey.Dtc] = typeof(DtcPage),
        [PageKey.LiveData] = typeof(LiveDataPage),
        [PageKey.KnowledgeBase] = typeof(KnowledgeBasePage),
        [PageKey.Wiring] = typeof(WiringPage),
        [PageKey.Research] = typeof(ResearchPage),
        [PageKey.Assistant] = typeof(AssistantPage),
        [PageKey.Training] = typeof(TrainingPage),
        [PageKey.Apprentice] = typeof(ApprenticePage),
        [PageKey.Shop] = typeof(ShopPage),
        [PageKey.History] = typeof(HistoryPage),
        [PageKey.Settings] = typeof(SettingsPage),
    };

    private Frame? _frame;

    public PageKey? CurrentPage { get; private set; }

    public bool CanGoBack => _frame?.CanGoBack == true;

    public event EventHandler<PageKey>? Navigated;

    /// <summary>Connects the service to the shell's content frame.</summary>
    public void Attach(Frame frame)
    {
        if (_frame is not null)
        {
            _frame.Navigated -= OnNavigated;
            _frame.Navigating -= OnNavigating;
        }

        _frame = frame;
        _frame.Navigated += OnNavigated;
        _frame.Navigating += OnNavigating;
    }

    public bool NavigateTo(PageKey page, object? parameter = null)
    {
        if (_frame is null) return false;
        NavigationTransitionInfo transition = settings.Current.Appearance.ReduceMotion
            ? new SuppressNavigationTransitionInfo()
            : new EntranceNavigationTransitionInfo();
        return _frame.Navigate(Pages[page], parameter, transition);
    }

    public bool GoBack()
    {
        if (_frame?.CanGoBack != true) return false;
        _frame.GoBack();
        return true;
    }

    private void OnNavigating(object sender, NavigatingCancelEventArgs e)
    {
        if (_frame?.Content is IViewModelPage page) page.ViewModel.OnNavigatedFrom();
    }

    private async void OnNavigated(object sender, NavigationEventArgs e)
    {
        CurrentPage = Pages.FirstOrDefault(p => p.Value == e.SourcePageType).Key;
        Navigated?.Invoke(this, CurrentPage.Value);
        if (e.Content is not IViewModelPage page) return;
        try
        {
            await page.ViewModel.OnNavigatedToAsync(e.Parameter);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Navigation to {Page} failed", e.SourcePageType.Name);
            page.ViewModel.ShowError(Application.Common.Error.FromException(ex));
        }
    }
}
