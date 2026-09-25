using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Web research ranked by source authority, with AI summaries that cite retrieved sources.</summary>
public sealed partial class ResearchViewModel(
    IResearchGateway research,
    IVehicleGateway vehicles,
    ISettingsStore settings,
    IConnectivityMonitor connectivity,
    ILauncher launcher,
    IClipboard clipboard,
    IDispatcher dispatcher) : ViewModelBase
{
    public ObservableCollection<SourceItem> Sources { get; } = [];

    public ObservableCollection<CitationItem> SummarySources { get; } = [];

    public ObservableCollection<string> SummaryWarnings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseVehicleContext { get; set; } = true;

    [ObservableProperty]
    public partial string? VehicleContext { get; set; }

    [ObservableProperty]
    public partial string? SearchNotice { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeCommand))]
    public partial bool HasSources { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand))]
    public partial string? Summary { get; set; }

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    [ObservableProperty]
    public partial string? SummaryModel { get; set; }

    private string? _lastQuery;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(async ct =>
        {
            VehicleContext = settings.Current.ActiveVehicleId is { } id ? (await vehicles.GetAsync(id, ct))?.Description : null;
        });
        if (parameter is NavigationParameters.SearchWeb search && !string.IsNullOrWhiteSpace(search.Query))
        {
            Query = search.Query;
            await SearchAsync();
        }
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => RunAsync(async ct =>
    {
        if (!connectivity.IsOnline)
        {
            // Cached results may still be served; the service reports what it can.
            SearchNotice = "You appear to be offline — only cached results can be shown.";
        }
        else
        {
            SearchNotice = null;
        }

        var result = await research.SearchAsync(Query.Trim(), UseVehicleContext ? VehicleContext : null, ct);
        Sources.Clear();
        Summary = null;
        SummarySources.Clear();
        SummaryWarnings.Clear();
        if (!Check(result))
        {
            HasSources = false;
            return;
        }

        var value = result.Value!;
        _lastQuery = value.Query;
        foreach (var s in value.Sources) Sources.Add(new SourceItem(s));
        HasSources = Sources.Count > 0;
        SearchNotice = value.Notice ?? (value.FromCache ? $"Showing cached results from {Format.Local(value.RetrievedUtc)}." : SearchNotice);
        if (!HasSources) ShowNotice("No results", "The search returned no usable sources.", NoticeSeverity.Informational);
    }, "Searching…");

    [RelayCommand(CanExecute = nameof(HasSources))]
    private Task SummarizeAsync() => RunAsync(async ct =>
    {
        var selected = Sources.Where(s => s.IsSelected).Select(s => s.Source).ToList();
        if (selected.Count == 0)
        {
            ShowError(Error.Validation("Select at least one source to summarize."));
            return;
        }

        var buffer = new StringBuilder();
        Summary = string.Empty;
        SummarySources.Clear();
        SummaryWarnings.Clear();
        var result = await research.SummarizeAsync(_lastQuery ?? Query.Trim(), selected, text =>
        {
            lock (buffer) buffer.Append(text);
            dispatcher.Run(() =>
            {
                lock (buffer) Summary = buffer.ToString();
            });
            return Task.CompletedTask;
        }, ct);
        if (!Check(result))
        {
            Summary = null;
            return;
        }

        var value = result.Value!;
        Summary = value.Markdown;
        SummaryModel = $"AI summary by {value.Model} — verify against the cited sources.";
        foreach (var s in value.Sources) SummarySources.Add(CitationItem.From(s));
        foreach (var w in value.Warnings) SummaryWarnings.Add(w);
        foreach (var f in value.FetchFailures) SummaryWarnings.Add($"Could not read: {f}");
    }, "Reading sources and summarizing…");

    [RelayCommand]
    private async Task OpenSourceAsync(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            await launcher.OpenUriAsync(uri);
        }
    }

    [RelayCommand]
    private Task BookmarkAsync(SourceItem? source) => source is null
        ? Task.CompletedTask
        : RunAsync(async ct =>
        {
            await research.BookmarkAsync(source.Title, source.Url, source.Source.Type, ct);
            StatusMessage = $"Bookmarked \"{source.Title}\".";
        });

    [RelayCommand(CanExecute = nameof(HasSummary))]
    private void CopySummary()
    {
        if (Summary is null) return;
        var text = new StringBuilder(Summary);
        if (SummarySources.Count > 0)
        {
            text.AppendLine().AppendLine().AppendLine("Sources:");
            foreach (var s in SummarySources) text.AppendLine($"[{s.Label}] {s.Title} {s.Url}");
        }

        clipboard.SetText(text.ToString());
    }

    [RelayCommand]
    private void SelectAllSources()
    {
        foreach (var s in Sources) s.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSourceSelection()
    {
        foreach (var s in Sources) s.IsSelected = false;
    }
}
