using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Wiring diagrams: identifiers found on the diagram and circuit questions answered only from those identifiers.</summary>
public sealed partial class WiringViewModel(
    IWiringGateway wiring,
    IKnowledgeBaseGateway knowledgeBase,
    IFilePicker files,
    ILauncher launcher,
    IDispatcher dispatcher) : ViewModelBase
{
    private List<WiringLabelItem> _allLabels = [];

    public ObservableCollection<DocumentItem> Diagrams { get; } = [];

    public ObservableCollection<WiringLabelItem> Labels { get; } = [];

    public ObservableCollection<WiringLabelItem> Highlights { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public IReadOnlyList<string> QuickQuestions { get; } = WiringService.QuickQuestions.Select(q => q.Prompt).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagram))]
    [NotifyCanExecuteChangedFor(nameof(OpenDiagramCommand))]
    public partial DocumentItem? SelectedDiagram { get; set; }

    public bool HasDiagram => SelectedDiagram is not null;

    [ObservableProperty]
    public partial string? PageNumberText { get; set; }

    [ObservableProperty]
    public partial string? LabelFilter { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string? Question { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    public partial string? Answer { get; set; }

    public bool HasAnswer => !string.IsNullOrWhiteSpace(Answer);

    [ObservableProperty]
    public partial string? AnswerSource { get; set; }

    [ObservableProperty]
    public partial bool HasDiagrams { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(LoadCoreAsync);
        if (parameter is NavigationParameters.OpenDocument open)
        {
            SelectedDiagram = Diagrams.FirstOrDefault(d => d.Id == open.DocumentId);
            PageNumberText = open.PageNumber?.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadCoreAsync);

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        var selected = SelectedDiagram?.Id;
        var docs = await wiring.ListDiagramsAsync(ct);
        Diagrams.Clear();
        foreach (var d in docs) Diagrams.Add(DocumentItem.From(d));
        HasDiagrams = Diagrams.Count > 0;
        SelectedDiagram = Diagrams.FirstOrDefault(d => d.Id == selected) ?? Diagrams.FirstOrDefault();
    }

    partial void OnSelectedDiagramChanged(DocumentItem? value)
    {
        _allLabels = [];
        Labels.Clear();
        Highlights.Clear();
        Answer = null;
        Warnings.Clear();
        if (value is not null) _ = RunAsync(LoadLabelsAsync);
    }

    partial void OnLabelFilterChanged(string? value) => ApplyLabelFilter();

    /// <summary>Parses the page box; null means "all pages". Returns false when the text is not a valid page.</summary>
    internal bool TryGetPage(out int? page)
    {
        page = null;
        if (string.IsNullOrWhiteSpace(PageNumberText)) return true;
        var parsed = Format.ParseInt(PageNumberText);
        if (parsed is null || parsed < 1 || (SelectedDiagram is { PageCount: > 0 } d && parsed > d.PageCount)) return false;
        page = parsed;
        return true;
    }

    [RelayCommand]
    private Task ReloadLabelsAsync() => RunAsync(LoadLabelsAsync);

    private async Task LoadLabelsAsync(CancellationToken ct)
    {
        if (SelectedDiagram is not { } diagram) return;
        if (!TryGetPage(out var page))
        {
            ShowError(Error.Validation($"Enter a page between 1 and {diagram.PageCount}."));
            return;
        }

        var labels = await wiring.ExtractLabelsAsync(diagram.Id, page, ct);
        _allLabels = labels.Select(WiringLabelItem.From).ToList();
        ApplyLabelFilter();
    }

    private void ApplyLabelFilter()
    {
        Labels.Clear();
        var filter = LabelFilter?.Trim();
        foreach (var label in _allLabels.Where(l => string.IsNullOrEmpty(filter) ||
                                                    l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                                    l.KindText.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Labels.Add(label);
        }
    }

    private bool CanAsk() => !string.IsNullOrWhiteSpace(Question);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task AskAsync() => AskCoreAsync(Question!.Trim());

    [RelayCommand]
    private Task AskQuickAsync(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return Task.CompletedTask;
        Question = prompt;
        return AskCoreAsync(prompt);
    }

    private Task AskCoreAsync(string question) => RunAsync(async ct =>
    {
        if (!TryGetPage(out var page))
        {
            ShowError(Error.Validation("Enter a valid page number or leave it blank."));
            return;
        }

        var buffer = new StringBuilder();
        Answer = string.Empty;
        Highlights.Clear();
        Warnings.Clear();
        var result = await wiring.AskAsync(question, SelectedDiagram?.Id, page, text =>
        {
            lock (buffer) buffer.Append(text);
            dispatcher.Run(() =>
            {
                lock (buffer) Answer = buffer.ToString();
            });
            return Task.CompletedTask;
        }, ct);
        if (!Check(result))
        {
            Answer = null;
            return;
        }

        var answer = result.Value!;
        Answer = answer.Markdown;
        AnswerSource = answer.AnsweredByAi
            ? $"Answered by {answer.Model}. Only identifiers found on the diagram are allowed; verify on the vehicle."
            : "No AI model — showing identifiers found on the diagram.";
        foreach (var h in answer.Highlights) Highlights.Add(WiringLabelItem.From(h));
        foreach (var w in answer.Warnings) Warnings.Add(w);
    }, "Reading the diagram…");

    [RelayCommand]
    private async Task UploadDiagramAsync()
    {
        var file = await files.PickFileAsync(KnowledgeBaseViewModel.UploadExtensions);
        if (file is null) return;
        if (KnowledgeBaseViewModel.ValidateUpload(file) is { } problem)
        {
            ShowError(Error.Validation(problem));
            return;
        }

        await RunAsync(async ct =>
        {
            await using var stream = await file.OpenReadAsync(ct);
            var result = await knowledgeBase.UploadAsync(stream, file.FileName, new DocumentUploadOptions { Kind = DocumentKind.WiringDiagram }, ct);
            if (!Check(result)) return;
            await LoadCoreAsync(ct);
            SelectedDiagram = Diagrams.FirstOrDefault(d => d.Id == result.Value) ?? SelectedDiagram;
            ShowSuccess("Diagram added. Identifiers become available once indexing finishes.");
        }, "Uploading…");
    }

    [RelayCommand(CanExecute = nameof(HasDiagram))]
    private Task OpenDiagramAsync() => RunAsync(async ct =>
    {
        if (SelectedDiagram is not { } diagram) return;
        var document = await knowledgeBase.GetAsync(diagram.Id, ct);
        if (document is null || !await launcher.OpenFileAsync(knowledgeBase.GetFilePath(document))) ShowError(Error.NotFound("The diagram file"));
    });
}
