using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.Interfaces;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Domain.Entities;

public class AiConversation : Entity, IAggregateRoot, IVehicleScoped
{
    public string Title { get; set; } = "New conversation";

    public ConversationKind Kind { get; set; } = ConversationKind.Assistant;

    public Guid? VehicleId { get; set; }

    public Guid? DiagnosticSessionId { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public bool IsPinned { get; set; }

    public List<AiConversationMessage> Messages { get; set; } = [];
}

/// <summary>
/// A persisted chat message. Tool calls and tool results are stored verbatim so the
/// conversation can be audited: every fact the AI used can be traced to real tool output.
/// </summary>
public class AiConversationMessage : Entity
{
    public Guid ConversationId { get; set; }

    public MessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>For assistant messages: serialized tool calls requested by the model.</summary>
    public string? ToolCallsJson { get; set; }

    /// <summary>For tool messages: the tool that produced this result.</summary>
    public string? ToolName { get; set; }

    public string? ToolCallId { get; set; }

    public List<SourceCitation> Citations { get; set; } = [];

    /// <summary>Warnings raised by output validation (e.g. an invalid citation was removed).</summary>
    public List<string> ValidationWarnings { get; set; } = [];

    public List<string> AttachmentIds { get; set; } = [];

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public int? LatencyMs { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int Sequence { get; set; }
}
