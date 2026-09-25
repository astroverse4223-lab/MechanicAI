using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MechanicAI.Application.Settings;

namespace MechanicAI.Application.Abstractions.Ai;

public enum ChatRole { System, User, Assistant, Tool }

public sealed record ChatImage(byte[] Data, string MediaType);

/// <summary>A tool invocation requested by the model. Arguments are raw JSON from the model.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Provider-neutral chat message.</summary>
public sealed class ChatMessage
{
    public ChatRole Role { get; init; }

    public string? Content { get; init; }

    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    /// <summary>Assistant messages: tool calls the model requested.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Tool messages: the call this result answers.</summary>
    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    /// <summary>Tool messages: true when the tool failed or its input was invalid.</summary>
    public bool IsError { get; init; }

    public static ChatMessage User(string text, IReadOnlyList<ChatImage>? images = null) =>
        new() { Role = ChatRole.User, Content = text, Images = images ?? [] };

    public static ChatMessage Assistant(string? text, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new() { Role = ChatRole.Assistant, Content = text, ToolCalls = toolCalls ?? [] };

    public static ChatMessage ToolResult(string toolCallId, string toolName, string content, bool isError = false) =>
        new() { Role = ChatRole.Tool, ToolCallId = toolCallId, ToolName = toolName, Content = content, IsError = isError };
}

/// <summary>A tool the model may call. <see cref="ParametersSchema"/> is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement ParametersSchema);

public enum ResponseFormat { Text, Json }

public sealed class ChatRequest
{
    public string? SystemPrompt { get; init; }

    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    public IReadOnlyList<ToolDefinition> Tools { get; init; } = [];

    public ResponseFormat Format { get; init; } = ResponseFormat.Text;

    /// <summary>Optional JSON Schema for structured output when <see cref="Format"/> is Json.</summary>
    public JsonElement? JsonSchema { get; init; }

    /// <summary>Sampling temperature. Ignored by providers/models that do not accept it.</summary>
    public double? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }
}

public enum ChatFinishReason { Stop, ToolCalls, Length, Refusal, Error }

public abstract record ChatStreamUpdate;

public sealed record TextDeltaUpdate(string Text) : ChatStreamUpdate;

/// <summary>A fully assembled tool call (arguments complete).</summary>
public sealed record ToolCallUpdate(ToolCall Call) : ChatStreamUpdate;

public sealed record CompletionUpdate(ChatFinishReason Reason, int? InputTokens, int? OutputTokens, string? Detail = null) : ChatStreamUpdate;

public sealed record ChatCompletion(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    ChatFinishReason FinishReason,
    int? InputTokens,
    int? OutputTokens,
    string Provider,
    string Model,
    string? Detail = null);

[Flags]
public enum ModelCapabilities
{
    None = 0,
    Chat = 1,
    Tools = 2,
    Vision = 4,
    Embeddings = 8,
    Thinking = 16,
    StructuredOutput = 32,
}

/// <summary>A chat-capable model behind a provider (Ollama, Anthropic, OpenAI-compatible).</summary>
public interface IChatModel
{
    string Provider { get; }

    string Model { get; }

    /// <summary>True when inference runs on this workstation (data never leaves it).</summary>
    bool IsLocal { get; }

    ModelCapabilities Capabilities { get; }

    IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, CancellationToken cancellationToken);
}

public interface IEmbeddingModel
{
    string Provider { get; }

    string Model { get; }

    bool IsLocal { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

public static class ChatModelExtensions
{
    /// <summary>Runs a request to completion, aggregating the stream.</summary>
    public static async Task<ChatCompletion> CompleteAsync(this IChatModel model, ChatRequest request, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        CompletionUpdate? done = null;
        await foreach (var update in model.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (update)
            {
                case TextDeltaUpdate t:
                    text.Append(t.Text);
                    break;
                case ToolCallUpdate c:
                    calls.Add(c.Call);
                    break;
                case CompletionUpdate c:
                    done = c;
                    break;
            }
        }

        return new ChatCompletion(
            text.ToString(),
            calls,
            done?.Reason ?? (calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop),
            done?.InputTokens,
            done?.OutputTokens,
            model.Provider,
            model.Model,
            done?.Detail);
    }

    /// <summary>Streams only text deltas (convenience for UIs).</summary>
    public static async IAsyncEnumerable<string> StreamTextAsync(this IChatModel model, ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var update in model.StreamAsync(request, ct).ConfigureAwait(false))
        {
            if (update is TextDeltaUpdate t) yield return t.Text;
        }
    }
}

/// <summary>What an AI request is for; drives routing between local and cloud models.</summary>
public enum AiTask
{
    Assistant,
    Diagnostic,
    DocumentQuestion,
    ImageAnalysis,
    WiringAnalysis,
    WebSummary,
    Classification,
    Training,
    LiveDataAnalysis,
}

/// <summary>Sensitivity of the data included in a request.</summary>
public enum DataSensitivity
{
    /// <summary>Vehicle/diagnostic data and public web content.</summary>
    General,
    /// <summary>Passages from the technician's private knowledge base.</summary>
    PrivateDocuments,
    /// <summary>Photos from the shop floor.</summary>
    Images,
}

public sealed record AiRoute(IChatModel? Model, string? UnavailableReason)
{
    public bool IsAvailable => Model is not null;

    public static AiRoute Unavailable(string reason) => new(null, reason);
}

public sealed record AiStatus(
    AiMode Mode,
    bool LocalReachable,
    string? LocalChatModel,
    string? LocalVisionModel,
    string? EmbeddingModel,
    bool EmbeddingAvailable,
    bool CloudConfigured,
    string? CloudModel,
    IReadOnlyList<string> Notes)
{
    public bool AnyChatAvailable => (LocalReachable && LocalChatModel is not null) || CloudConfigured;
}

/// <summary>Chooses a model for each task according to AI mode, privacy settings, and capabilities.</summary>
public interface IAiRouter
{
    Task<AiRoute> ResolveChatAsync(AiTask task, DataSensitivity sensitivity, bool requireVision = false,
        CancellationToken cancellationToken = default);

    Task<IEmbeddingModel?> ResolveEmbeddingAsync(CancellationToken cancellationToken = default);

    Task<AiStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default);

    void Invalidate();
}

public sealed record LocalModelInfo(
    string Name,
    long SizeBytes,
    ModelCapabilities Capabilities,
    string? Family,
    string? ParameterSize,
    int? ContextLength);

public sealed record ModelPullProgress(string Status, long? Completed, long? Total);

/// <summary>Management operations for the local Ollama server.</summary>
public interface IOllamaManagement
{
    Task<bool> PingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    Task PullModelAsync(string model, IProgress<ModelPullProgress>? progress, CancellationToken cancellationToken);
}
