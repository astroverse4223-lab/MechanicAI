using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>AI assistant chat with persistent conversations, streamed replies, tool activity, and citations.</summary>
public sealed partial class AssistantViewModel(
    IAssistantGateway assistant,
    IDispatcher dispatcher,
    IDialogService dialogs,
    IFilePicker files,
    IClipboard clipboard,
    ISettingsStore settings,
    IClock clock) : ViewModelBase
{
    private static readonly IReadOnlyList<string> ImageExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private const long MaxImageBytes = 20L * 1024 * 1024;
    private readonly List<ChatImage> _pendingImages = [];

    public ObservableCollection<ConversationItem> Conversations { get; } = [];

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];

    public ObservableCollection<string> Attachments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteConversationCommand), nameof(RenameConversationCommand), nameof(TogglePinCommand))]
    public partial ConversationItem? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial Guid? CurrentConversationId { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSending { get; set; }

    [ObservableProperty]
    public partial bool HasAttachments { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await RunAsync(LoadConversationsAsync);
        if (parameter is NavigationParameters.AskAssistant ask && !string.IsNullOrWhiteSpace(ask.Text))
        {
            await NewConversationAsync();
            Draft = ask.Text;
            await SendAsync();
        }
        else if (CurrentConversationId is null && Conversations.Count > 0)
        {
            SelectedConversation = Conversations[0];
        }
    }

    private async Task LoadConversationsAsync(CancellationToken ct)
    {
        var list = await assistant.ListAsync(ct);
        var now = clock.UtcNow;
        var current = CurrentConversationId;
        Conversations.Clear();
        foreach (var c in list.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.UpdatedUtc)) Conversations.Add(ConversationItem.From(c, now));
        _ignoreSelection = true;
        SelectedConversation = Conversations.FirstOrDefault(c => c.Id == current);
        _ignoreSelection = false;
    }

    private bool _ignoreSelection;

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        if (_ignoreSelection || value is null || value.Id == CurrentConversationId) return;
        _ = RunAsync(ct => LoadConversationAsync(value.Id, ct));
    }

    private async Task LoadConversationAsync(Guid id, CancellationToken ct)
    {
        var conversation = await assistant.GetAsync(id, ct);
        Messages.Clear();
        if (conversation is null)
        {
            CurrentConversationId = null;
            ShowError(Error.NotFound("Conversation"));
            IsEmpty = true;
            return;
        }

        CurrentConversationId = conversation.Id;
        foreach (var m in conversation.Messages.OrderBy(m => m.Sequence))
        {
            if (m.Role is not (MessageRole.User or MessageRole.Assistant) || string.IsNullOrWhiteSpace(m.Content)) continue;
            var item = new ChatMessageItem(m.Role == MessageRole.User, m.Content);
            if (m.Role == MessageRole.Assistant) item.Footer = BuildFooter(m);
            Messages.Add(item);
        }

        IsEmpty = Messages.Count == 0;
    }

    private static string? BuildFooter(AiConversationMessage m)
    {
        var parts = new List<string>();
        if (m.Citations.Count > 0) parts.Add("Sources: " + string.Join("; ", m.Citations.Select(c => $"[{c.Label}] {c.Title}")));
        if (m.ValidationWarnings.Count > 0) parts.Add("⚠ " + string.Join(" ", m.ValidationWarnings));
        if (!string.IsNullOrWhiteSpace(m.Model)) parts.Add(m.Model);
        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    [RelayCommand]
    private Task NewConversationAsync() => RunAsync(async ct =>
    {
        var s = settings.Current;
        var id = await assistant.CreateAsync(s.ActiveVehicleId, s.ActiveSessionId, ct);
        CurrentConversationId = id;
        Messages.Clear();
        IsEmpty = true;
        await LoadConversationsAsync(ct);
    });

    private bool CanSend() => !IsSending && (!string.IsNullOrWhiteSpace(Draft) || _pendingImages.Count > 0);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (text.Length == 0 && _pendingImages.Count == 0)
        {
            ShowError(Error.Validation("Type a message."));
            return;
        }

        if (CurrentConversationId is null)
        {
            await NewConversationAsync();
            if (CurrentConversationId is null) return;
        }

        var conversationId = CurrentConversationId.Value;
        var images = _pendingImages.ToList();
        Draft = string.Empty;
        _pendingImages.Clear();
        Attachments.Clear();
        HasAttachments = false;

        Messages.Add(new ChatMessageItem(true, images.Count == 0 ? text : $"{text}\n[{images.Count} image(s) attached]"));
        var reply = new ChatMessageItem(false, string.Empty) { Activity = "Thinking…" };
        Messages.Add(reply);
        IsEmpty = false;
        IsSending = true;
        var buffer = new StringBuilder();
        try
        {
            await RunAsync(async ct =>
            {
                var result = await assistant.SendAsync(conversationId, text, images, e =>
                {
                    switch (e)
                    {
                        case AgentTextDelta delta:
                            lock (buffer) buffer.Append(delta.Text);
                            dispatcher.Run(() =>
                            {
                                lock (buffer) reply.Text = buffer.ToString();
                            });
                            break;
                        case AgentToolStarted started:
                            dispatcher.Run(() => reply.Activity = $"Using {started.Tool}…");
                            break;
                        case AgentToolFinished finished:
                            dispatcher.Run(() => reply.Activity = finished.IsError ? $"{finished.Tool} failed" : $"{finished.Tool}: {finished.Summary}");
                            break;
                        case AgentStatus status:
                            dispatcher.Run(() => reply.Activity = status.Message);
                            break;
                    }

                    return Task.CompletedTask;
                }, ct);

                reply.Activity = null;
                if (!result.IsSuccess)
                {
                    reply.Text = result.Error!.Kind == ErrorKind.Cancelled ? "(stopped)" : result.Error.Message;
                    ShowError(result.Error);
                    return;
                }

                var value = result.Value!;
                reply.Text = value.Markdown;
                var footer = new List<string>();
                if (value.Sources.Count > 0) footer.Add("Sources: " + string.Join("; ", value.Sources.Select(s => $"[{s.Label}] {s.Title}")));
                if (value.ToolCalls.Count > 0) footer.Add("Tools used: " + string.Join(", ", value.ToolCalls.Select(t => t.Tool).Distinct()));
                if (value.Warnings.Count > 0) footer.Add("⚠ " + string.Join(" ", value.Warnings));
                footer.Add($"{value.Provider} · {value.Model} · {value.ElapsedMs / 1000.0:0.0}s");
                reply.Footer = string.Join(Environment.NewLine, footer);
                await LoadConversationsAsync(ct);
            });
        }
        finally
        {
            if (reply.Activity is not null)
            {
                reply.Activity = null;
                if (string.IsNullOrEmpty(reply.Text)) reply.Text = "(stopped)";
            }

            IsSending = false;
        }
    }

    [RelayCommand]
    private void Stop() => CancelOperation();

    [RelayCommand]
    private async Task AttachImageAsync()
    {
        var picked = await files.PickFilesAsync(ImageExtensions);
        foreach (var file in picked)
        {
            if (file.SizeBytes is > MaxImageBytes)
            {
                ShowError(Error.Validation($"{file.FileName} is larger than 20 MB."));
                continue;
            }

            await using var stream = await file.OpenReadAsync(CancellationToken.None);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/jpeg",
            };
            _pendingImages.Add(new ChatImage(memory.ToArray(), mediaType));
            Attachments.Add(file.FileName);
        }

        HasAttachments = Attachments.Count > 0;
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearAttachments()
    {
        _pendingImages.Clear();
        Attachments.Clear();
        HasAttachments = false;
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool HasSelectedConversation() => SelectedConversation is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedConversation))]
    private async Task DeleteConversationAsync()
    {
        if (SelectedConversation is not { } conversation) return;
        if (!await dialogs.ConfirmAsync("Delete conversation?", $"Delete \"{conversation.Title}\"?", "Delete", "Cancel", destructive: true)) return;
        await RunAsync(async ct =>
        {
            if (!Check(await assistant.DeleteAsync(conversation.Id, ct))) return;
            if (CurrentConversationId == conversation.Id)
            {
                CurrentConversationId = null;
                Messages.Clear();
                IsEmpty = true;
            }

            await LoadConversationsAsync(ct);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConversation))]
    private async Task RenameConversationAsync()
    {
        if (SelectedConversation is not { } conversation) return;
        var title = await dialogs.PromptAsync("Rename conversation", "Title", conversation.Title, "Rename");
        if (string.IsNullOrWhiteSpace(title)) return;
        await RunAsync(async ct =>
        {
            if (Check(await assistant.RenameAsync(conversation.Id, title.Trim(), ct))) await LoadConversationsAsync(ct);
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConversation))]
    private Task TogglePinAsync() => RunAsync(async ct =>
    {
        if (SelectedConversation is not { } conversation) return;
        if (Check(await assistant.SetPinnedAsync(conversation.Id, !conversation.IsPinned, ct))) await LoadConversationsAsync(ct);
    });

    [RelayCommand]
    private void CopyMessage(ChatMessageItem? message)
    {
        if (message is not null && !string.IsNullOrEmpty(message.Text)) clipboard.SetText(message.Text);
    }
}
