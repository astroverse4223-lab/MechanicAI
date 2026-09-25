using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public sealed record ConversationSummary(Guid Id, string Title, ConversationKind Kind, DateTime UpdatedUtc, int MessageCount, bool IsPinned, Guid? VehicleId, Guid? SessionId);

public sealed record AssistantReply(
    Guid ConversationId,
    Guid MessageId,
    string Markdown,
    IReadOnlyList<Domain.ValueObjects.SourceCitation> Sources,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ToolInvocation> ToolCalls,
    string Provider,
    string Model,
    long ElapsedMs);

/// <summary>
/// The AI assistant: persistent conversations with tool calling. Every tool call and result
/// is stored verbatim so any claim can be audited back to real tool output.
/// </summary>
public sealed class AssistantService(
    IAiRouter router,
    AgentRunner agent,
    IAppDbContextFactory dbFactory,
    ISettingsStore settings,
    ILogger<AssistantService> logger,
    IConnectivityMonitor? connectivity = null)
{
    private const int HistoryMessages = 24;

    public event EventHandler<Guid>? ConversationChanged;

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.AiConversations.AsNoTracking()
            .OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.UpdatedUtc)
            .Take(take)
            .Select(c => new ConversationSummary(c.Id, c.Title, c.Kind, c.UpdatedUtc, c.Messages.Count(m => m.Role == MessageRole.User || m.Role == MessageRole.Assistant),
                c.IsPinned, c.VehicleId, c.DiagnosticSessionId))
            .ToListAsync(ct);
    }

    public async Task<AiConversation?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var conversation = await db.AiConversations.AsNoTracking().Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (conversation is not null) conversation.Messages = conversation.Messages.OrderBy(m => m.Sequence).ToList();
        return conversation;
    }

    public async Task<Guid> CreateAsync(ConversationKind kind = ConversationKind.Assistant, Guid? vehicleId = null, Guid? sessionId = null, string? title = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var conversation = new AiConversation
        {
            Kind = kind,
            VehicleId = vehicleId,
            DiagnosticSessionId = sessionId,
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : Text.Truncate(title, 120),
        };
        db.AiConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        ConversationChanged?.Invoke(this, conversation.Id);
        return conversation.Id;
    }

    public async Task<Result> RenameAsync(Guid id, string title, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var c = await db.AiConversations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Error.NotFound("Conversation");
        c.Title = Text.Truncate(title.Trim(), 120);
        await db.SaveChangesAsync(ct);
        ConversationChanged?.Invoke(this, id);
        return Result.Success();
    }

    public async Task<Result> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var c = await db.AiConversations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Error.NotFound("Conversation");
        c.IsPinned = pinned;
        await db.SaveChangesAsync(ct);
        ConversationChanged?.Invoke(this, id);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var c = await db.AiConversations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Error.NotFound("Conversation");
        db.AiConversations.Remove(c);
        await db.SaveChangesAsync(ct);
        ConversationChanged?.Invoke(this, id);
        return Result.Success();
    }

    public async Task<Result<AssistantReply>> SendAsync(Guid conversationId, string text, IReadOnlyList<ChatImage> images,
        Func<AgentEvent, Task>? onEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) && images.Count == 0) return Error.Validation("Type a message.");

        await using var db = await dbFactory.CreateAsync(ct);
        var conversation = await db.AiConversations.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return Error.NotFound("Conversation");

        var sensitivity = images.Count > 0 ? DataSensitivity.Images : DataSensitivity.General;
        var route = await router.ResolveChatAsync(AiTask.Assistant, sensitivity, requireVision: false, ct);
        if (!route.IsAvailable) return Error.NotConfigured(route.UnavailableReason!);
        var model = route.Model!;
        var s = settings.Current;

        // Private data stays local unless the technician allowed it in Privacy settings.
        var privateAllowed = model.IsLocal || (s.Privacy.AllowCloudAi && s.Privacy.AllowDocumentsInCloud);
        var allowed = agent.Tools
            .Where(t => !t.ReturnsPrivateData || privateAllowed || (t.Name == "analyze_image" && (model.IsLocal || s.Privacy.AllowImagesInCloud)))
            .Where(t => connectivity is not { IsOnline: false } || t.Name != "search_web")
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var vehicleId = conversation.VehicleId ?? s.ActiveVehicleId;
        var sessionId = conversation.DiagnosticSessionId ?? s.ActiveSessionId;
        var vehicleContext = vehicleId is { } vid
            ? await db.Vehicles.AsNoTracking().Where(v => v.Id == vid).Select(v => new { v.Year, v.Make, v.Model, v.Trim, v.Engine, v.Mileage, v.Vin, v.IsSample }).FirstOrDefaultAsync(ct)
            : null;
        var sessionContext = sessionId is { } sid
            ? await db.DiagnosticSessions.AsNoTracking().Where(x => x.Id == sid).Select(x => new { x.Title, x.Status }).FirstOrDefaultAsync(ct)
            : null;

        var vehicleText = vehicleContext is null ? null :
            $"{vehicleContext.Year} {vehicleContext.Make} {vehicleContext.Model} {vehicleContext.Trim} {vehicleContext.Engine}".Trim() +
            (vehicleContext.Mileage is { } m ? $", {m:N0} {(s.Diagnostics.Units == Settings.UnitSystem.Metric ? "km" : "mi")}" : string.Empty) +
            (vehicleContext.Vin is null ? string.Empty : $", VIN {vehicleContext.Vin}") +
            (vehicleContext.IsSample ? " (SAMPLE DATA)" : string.Empty) + $" [vehicle_id {vehicleId}]";
        var sessionText = sessionContext is null ? null : $"{sessionContext.Title} ({sessionContext.Status}) [session_id {sessionId}]";

        var systemPrompt = Prompts.BuildAssistantPrompt(s, vehicleText, sessionText, connectivity?.IsOnline ?? true,
            privateAllowed, DateTime.UtcNow);
        if (images.Count > 0) systemPrompt += $"\n- The technician attached {images.Count} image(s) to this message. Use analyze_image (index 0..{images.Count - 1}) to examine them.";

        var history = BuildHistory(conversation.Messages);
        var registry = new SourceRegistry();
        foreach (var citation in conversation.Messages.SelectMany(m => m.Citations))
        {
            registry.Register(citation);
        }

        var sequence = conversation.Messages.Count == 0 ? 0 : conversation.Messages.Max(m => m.Sequence);
        var userMessage = new AiConversationMessage
        {
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = text,
            Sequence = ++sequence,
            AttachmentIds = images.Count == 0 ? [] : [$"{images.Count} image(s)"],
        };
        db.AiConversationMessages.Add(userMessage);
        if (conversation.Messages.Count == 0 && conversation.Title == "New conversation") conversation.Title = Text.Truncate(Text.CollapseWhitespace(text), 80);
        conversation.Provider = model.Provider;
        conversation.Model = model.Model;
        await db.SaveChangesAsync(ct);

        AgentResult result;
        try
        {
            result = await agent.RunAsync(new AgentRequest
            {
                Model = model,
                SystemPrompt = systemPrompt,
                History = history,
                // Images go to the model directly only if it can see; otherwise via analyze_image.
                UserMessage = ChatMessage.User(text, model.Capabilities.HasFlag(ModelCapabilities.Vision) ? images : []),
                AllowedTools = allowed,
                Context = new ToolContext
                {
                    Sources = registry,
                    VehicleId = vehicleId,
                    SessionId = sessionId,
                    ConversationId = conversation.Id,
                    Images = images,
                    ModelIsLocal = model.IsLocal,
                },
                MaxRounds = s.Ai.MaxToolRounds,
            }, onEvent, ct);
        }
        catch (ExternalServiceException ex)
        {
            logger.LogWarning("Assistant request failed: {Kind} {Message}", ex.Kind, ex.UserMessage);
            return new Error(ex.Kind, ex.UserMessage);
        }

        // Persist the intermediate turns (tool calls and tool results) verbatim; the final
        // answer is stored separately below in its citation-validated form.
        var intermediate = result.NewMessages.ToList();
        if (intermediate.Count > 0 && intermediate[^1].Role == ChatRole.Assistant && intermediate[^1].ToolCalls.Count == 0)
        {
            intermediate.RemoveAt(intermediate.Count - 1);
        }

        foreach (var message in intermediate)
        {
            db.AiConversationMessages.Add(new AiConversationMessage
            {
                ConversationId = conversation.Id,
                Role = message.Role == ChatRole.Tool ? MessageRole.Tool : MessageRole.Assistant,
                Content = message.Content ?? string.Empty,
                ToolCallsJson = message.ToolCalls.Count == 0 ? null : Json.Serialize(message.ToolCalls),
                ToolName = message.ToolName,
                ToolCallId = message.ToolCallId,
                Sequence = ++sequence,
                Provider = model.Provider,
                Model = model.Model,
            });
        }

        var final = new AiConversationMessage
        {
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Content = result.FinalText,
            Citations = result.Citations.CitedSources.Count > 0 ? result.Citations.CitedSources.ToList() : [],
            ValidationWarnings = result.Citations.Warnings.ToList(),
            Sequence = ++sequence,
            Provider = result.Provider,
            Model = result.Model,
            LatencyMs = (int)result.ElapsedMs,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
        };
        db.AiConversationMessages.Add(final);
        conversation.Touch();
        await db.SaveChangesAsync(ct);
        ConversationChanged?.Invoke(this, conversation.Id);

        return new AssistantReply(conversation.Id, final.Id, result.FinalText, registry.Sources, result.Citations.Warnings, result.Invocations,
            result.Provider, result.Model, result.ElapsedMs);
    }

    /// <summary>Rebuilds provider-neutral history (recent turns only, tool results truncated).</summary>
    private static List<ChatMessage> BuildHistory(IEnumerable<AiConversationMessage> messages)
    {
        var ordered = messages.OrderBy(m => m.Sequence).ToList();
        var recent = ordered.Skip(Math.Max(0, ordered.Count - HistoryMessages)).ToList();

        // Never start history with a tool result or a tool-calling assistant turn whose results were cut off.
        while (recent.Count > 0 && (recent[0].Role == MessageRole.Tool || (recent[0].Role == MessageRole.Assistant && recent[0].ToolCallsJson is not null)))
        {
            recent.RemoveAt(0);
        }

        var list = new List<ChatMessage>();
        foreach (var m in recent)
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    list.Add(ChatMessage.User(m.Content));
                    break;
                case MessageRole.Assistant:
                    var calls = Json.Deserialize<List<ToolCall>>(m.ToolCallsJson) ?? [];
                    list.Add(ChatMessage.Assistant(m.Content, calls));
                    break;
                case MessageRole.Tool:
                    list.Add(ChatMessage.ToolResult(m.ToolCallId ?? string.Empty, m.ToolName ?? string.Empty, Text.Truncate(m.Content, 4000)));
                    break;
            }
        }

        return list;
    }
}
