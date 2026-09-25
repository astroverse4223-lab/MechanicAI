using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Private knowledge base: upload and index service documents, search passages, ask cited questions.</summary>
public sealed partial class KnowledgeBaseViewModel : ViewModelBase
{
    public static readonly IReadOnlyList<string> UploadExtensions =
        [".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".txt", ".md", ".markdown", ".csv", ".log"];

    private static readonly DocumentKind[] KindValues = Enum.GetValues<DocumentKind>();
    private static readonly DocumentQuestionTemplate[] TemplateValues = Enum.GetValues<DocumentQuestionTemplate>();

    private readonly IKnowledgeBaseGateway _kb;
    private readonly IFilePicker _files;
    private readonly IDialogService _dialogs;
    private readonly ILauncher _launcher;
    private readonly IDispatcher _dispatcher;

    public KnowledgeBaseViewModel(
        IKnowledgeBaseGateway kb,
        IFilePicker files,
        IDialogService dialogs,
        ILauncher launcher,
        IDispatcher dispatcher,
        IBackgroundTaskQueue queue)
    {
        _kb = kb;
        _files = files;
        _dialogs = dialogs;
        _launcher = launcher;
        _dispatcher = dispatcher;
        _kb.DocumentChanged += (_, _) => _dispatcher.Run(() =>
        {
            if (!IsBusy) _ = RunAsync(LoadCoreAsync);
        });
        queue.ProgressChanged += (_, p) => _dispatcher.Run(() =>
            IndexingStatus = p.Completed || p.Failed ? null : $"{p.Name}: {p.Stage}{(p.Fraction is { } f ? $" ({f:P0})" : string.Empty)}");
    }

    public ObservableCollection<DocumentItem> Documents { get; } = [];

    public ObservableCollection<PassageItem> Passages { get; } = [];

    public ObservableCollection<CitationItem> AnswerSources { get; } = [];

    public ObservableCollection<string> AnswerWarnings { get; } = [];

    /// <summary>"All kinds" followed by each document kind.</summary>
    public IReadOnlyList<string> FilterKinds { get; } = new[] { "All kinds" }.Concat(KindValues.Select(k => k.ToString())).ToArray();

    public IReadOnlyList<string> UploadKinds { get; } = KindValues.Select(k => k.ToString()).ToList();

    public IReadOnlyList<string> Templates { get; } = TemplateValues.Select(KnowledgeBaseService.TemplateLabel).ToList();

    public string SupportedTypes => KnowledgeBaseService.SupportedFileTypesDescription;

    [ObservableProperty]
    public partial int FilterKindIndex { get; set; }

    [ObservableProperty]
    public partial int UploadKindIndex { get; set; } = Array.IndexOf(KindValues, DocumentKind.ServiceManual);

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenDocumentCommand), nameof(DeleteDocumentCommand), nameof(ReindexCommand))]
    public partial DocumentItem? SelectedDocument { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? IndexingStatus { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand), nameof(SearchPassagesCommand))]
    public partial string? Question { get; set; }

    [ObservableProperty]
    public partial int TemplateIndex { get; set; }

    [ObservableProperty]
    public partial bool LimitToSelectedDocument { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    public partial string? Answer { get; set; }

    public bool HasAnswer => !string.IsNullOrWhiteSpace(Answer);

    [ObservableProperty]
    public partial string? AnswerModel { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(LoadCoreAsync);
        if (parameter is NavigationParameters.OpenDocument open)
        {
            SelectedDocument = Documents.FirstOrDefault(d => d.Id == open.DocumentId);
            if (SelectedDocument is not null) await OpenDocumentAsync();
        }
    }

    partial void OnFilterKindIndexChanged(int value) => _ = RunAsync(LoadCoreAsync);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadCoreAsync);

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        DocumentKind? kind = FilterKindIndex > 0 && FilterKindIndex <= KindValues.Length ? KindValues[FilterKindIndex - 1] : null;
        var selected = SelectedDocument?.Id;
        var docs = await _kb.ListAsync(kind, Clean(SearchText), ct);
        Documents.Clear();
        foreach (var d in docs) Documents.Add(DocumentItem.From(d));
        SelectedDocument = Documents.FirstOrDefault(d => d.Id == selected);
        var (documents, ready, chunks, embedded) = await _kb.GetStatsAsync(ct);
        StatsText = $"{documents:N0} documents · {ready:N0} ready · {chunks:N0} passages · {embedded:N0} with semantic embeddings";
    }

    /// <summary>Validates a file before upload. Returns an error message or null.</summary>
    internal static string? ValidateUpload(PickedFile file)
    {
        var extension = Path.GetExtension(file.FileName);
        if (!KnowledgeBaseService.IsSupportedExtension(extension))
        {
            return $"{file.FileName}: unsupported file type. Supported: {KnowledgeBaseService.SupportedFileTypesDescription}.";
        }

        if (file.SizeBytes is { } size && size > KnowledgeBaseService.MaxUploadBytes)
        {
            return $"{file.FileName}: larger than the {Format.Size(KnowledgeBaseService.MaxUploadBytes)} limit.";
        }

        return file.SizeBytes == 0 ? $"{file.FileName}: the file is empty." : null;
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        var picked = await _files.PickFilesAsync(UploadExtensions);
        if (picked.Count == 0) return;
        var kind = KindValues[Math.Clamp(UploadKindIndex, 0, KindValues.Length - 1)];
        await RunAsync(async ct =>
        {
            var problems = new List<string>();
            var uploaded = 0;
            foreach (var file in picked)
            {
                if (ValidateUpload(file) is { } problem)
                {
                    problems.Add(problem);
                    continue;
                }

                await using var stream = await file.OpenReadAsync(ct);
                var result = await _kb.UploadAsync(stream, file.FileName, new DocumentUploadOptions { Kind = kind }, ct);
                if (result.IsSuccess) uploaded++;
                else problems.Add($"{file.FileName}: {result.Error!.Message}");
            }

            await LoadCoreAsync(ct);
            if (problems.Count > 0)
            {
                ShowError(Error.Validation(string.Join(Environment.NewLine, problems)));
            }
            else
            {
                ShowSuccess($"{uploaded} document(s) added. Indexing continues in the background.");
            }
        }, "Uploading…");
    }

    private bool HasSelectedDocument() => SelectedDocument is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedDocument))]
    private async Task OpenDocumentAsync()
    {
        if (SelectedDocument is not { } doc) return;
        await RunAsync(async ct =>
        {
            var entity = await _kb.GetAsync(doc.Id, ct);
            if (entity is null)
            {
                ShowError(Error.NotFound("Document"));
                return;
            }

            if (!await _launcher.OpenFileAsync(_kb.GetFilePath(entity))) ShowError(Error.NotFound("The document file"));
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDocument))]
    private async Task DeleteDocumentAsync()
    {
        if (SelectedDocument is not { } doc) return;
        if (!await _dialogs.ConfirmAsync("Delete document?", $"Delete \"{doc.Title}\" and its search index?", "Delete", "Cancel", destructive: true)) return;
        await RunAsync(async ct =>
        {
            if (!Check(await _kb.DeleteAsync(doc.Id, ct))) return;
            SelectedDocument = null;
            await LoadCoreAsync(ct);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedDocument))]
    private Task ReindexAsync() => RunAsync(async ct =>
    {
        if (SelectedDocument is not { } doc) return;
        if (Check(await _kb.ReindexAsync(doc.Id, ct))) StatusMessage = $"Re-indexing \"{doc.Title}\"…";
    });

    private bool CanAsk() => !string.IsNullOrWhiteSpace(Question);

    private KnowledgeSearchOptions BuildOptions() => new()
    {
        Limit = 8,
        DocumentId = LimitToSelectedDocument ? SelectedDocument?.Id : null,
    };

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task SearchPassagesAsync() => RunAsync(async ct =>
    {
        var hits = await _kb.SearchAsync(Question!.Trim(), BuildOptions(), ct);
        Passages.Clear();
        foreach (var h in hits) Passages.Add(PassageItem.From(h));
        if (Passages.Count == 0) ShowNotice("No passages", "Nothing in your documents matches. Try different words or add documents.", NoticeSeverity.Informational);
    });

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task AskAsync() => RunAsync(async ct =>
    {
        var template = TemplateValues[Math.Clamp(TemplateIndex, 0, TemplateValues.Length - 1)];
        var buffer = new StringBuilder();
        Answer = string.Empty;
        AnswerSources.Clear();
        AnswerWarnings.Clear();
        AnswerModel = null;
        var result = await _kb.AskAsync(Question!.Trim(), template, BuildOptions(), text =>
        {
            lock (buffer) buffer.Append(text);
            _dispatcher.Run(() =>
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
        AnswerModel = answer.AnsweredByAi ? $"Answered by {answer.Model} from your documents" : "No AI model — showing the most relevant passages";
        foreach (var s in answer.Sources) AnswerSources.Add(CitationItem.From(s));
        foreach (var w in answer.Warnings) AnswerWarnings.Add(w);
        Passages.Clear();
        foreach (var p in answer.Passages) Passages.Add(PassageItem.From(p));
    }, "Searching your documents…");
}
