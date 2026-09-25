using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using AppChatMessage = MechanicAI.Application.Abstractions.Ai.ChatMessage;
using AppChatRole = MechanicAI.Application.Abstractions.Ai.ChatRole;

namespace MechanicAI.Infrastructure.Ai;

/// <summary>
/// Claude via the official Anthropic C# SDK. Requests are streamed (long outputs never hit
/// HTTP timeouts), tools stream their inputs eagerly (inputs are validated by the agent loop
/// before execution), and — unless disabled — refusals are retried server-side on
/// Anthropic's recommended fallback model (<c>fallbacks: "default"</c>).
/// </summary>
public sealed class AnthropicChatModel : IChatModel
{
    private const string Service = "Anthropic";
    private readonly AnthropicClient _client;
    private readonly AiSettings _settings;
    private readonly string _model;

    public AnthropicChatModel(string apiKey, AiSettings settings)
    {
        _settings = settings;
        _model = string.IsNullOrWhiteSpace(settings.AnthropicModel) ? "claude-opus-5" : settings.AnthropicModel.Trim();
        _client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = string.IsNullOrWhiteSpace(settings.AnthropicBaseUrl) ? "https://api.anthropic.com" : settings.AnthropicBaseUrl.TrimEnd('/'),
            MaxRetries = 2,
            Timeout = TimeSpan.FromSeconds(Math.Max(60, settings.RequestTimeoutSeconds)),
        };
    }

    public string Provider => "Anthropic (cloud)";

    public string Model => _model;

    public bool IsLocal => false;

    public ModelCapabilities Capabilities =>
        ModelCapabilities.Chat | ModelCapabilities.Tools | ModelCapabilities.Vision | ModelCapabilities.StructuredOutput | ModelCapabilities.Thinking;

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var parameters = BuildParams(request);
        var toolBlocks = new Dictionary<long, (string Id, string Name, StringBuilder Json)>();
        int? inputTokens = null;
        int? outputTokens = null;
        string? stopReason = null;
        string? refusalDetail = null;

        var events = _client.Beta.Messages.CreateStreaming(parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                BetaRawMessageStreamEvent streamEvent;
                try
                {
                    if (!await events.MoveNextAsync()) break;
                    streamEvent = events.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not ExternalServiceException)
                {
                    throw Map(ex);
                }

                if (streamEvent.TryPickStart(out var start))
                {
                    inputTokens = (int?)start.Message.Usage.InputTokens;
                }
                else if (streamEvent.TryPickContentBlockStart(out var blockStart))
                {
                    if (blockStart.ContentBlock.TryPickBetaToolUse(out var toolUse))
                    {
                        toolBlocks[blockStart.Index] = (toolUse.ID, toolUse.Name, new StringBuilder());
                    }
                }
                else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
                {
                    if (blockDelta.Delta.TryPickText(out var text))
                    {
                        if (!string.IsNullOrEmpty(text.Text)) yield return new TextDeltaUpdate(text.Text);
                    }
                    else if (blockDelta.Delta.TryPickInputJson(out var inputJson) && toolBlocks.TryGetValue(blockDelta.Index, out var tool))
                    {
                        tool.Json.Append(inputJson.PartialJson);
                    }
                }
                else if (streamEvent.TryPickContentBlockStop(out var blockStop))
                {
                    if (toolBlocks.Remove(blockStop.Index, out var tool))
                    {
                        var json = tool.Json.Length == 0 ? "{}" : tool.Json.ToString();
                        yield return new ToolCallUpdate(new ToolCall(tool.Id, tool.Name, json));
                    }
                }
                else if (streamEvent.TryPickDelta(out var messageDelta))
                {
                    outputTokens = (int?)messageDelta.Usage.OutputTokens;
                    stopReason = messageDelta.Delta.StopReason?.ToString();
                    if (messageDelta.Delta.StopDetails is { } details)
                    {
                        refusalDetail = details.ToString();
                    }
                }
            }
        }
        finally
        {
            await events.DisposeAsync();
        }

        var reason = NormalizeStopReason(stopReason) switch
        {
            "tool_use" => ChatFinishReason.ToolCalls,
            "max_tokens" => ChatFinishReason.Length,
            "refusal" => ChatFinishReason.Refusal,
            _ => ChatFinishReason.Stop,
        };

        yield return new CompletionUpdate(reason, inputTokens, outputTokens,
            reason == ChatFinishReason.Refusal ? "Claude declined this request" + (refusalDetail is null ? "." : $" ({Text.Truncate(refusalDetail, 200)}).") : null);
    }

    internal MessageCreateParams BuildParams(ChatRequest request)
    {
        var messages = new List<BetaMessageParam>();
        List<BetaContentBlockParam>? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is { Count: > 0 })
            {
                messages.Add(new BetaMessageParam { Role = Role.User, Content = pendingToolResults });
            }

            pendingToolResults = null;
        }

        foreach (var message in request.Messages)
        {
            switch (message.Role)
            {
                case AppChatRole.Tool:
                    pendingToolResults ??= [];
                    pendingToolResults.Add(new BetaToolResultBlockParam
                    {
                        ToolUseID = message.ToolCallId ?? string.Empty,
                        Content = message.Content ?? string.Empty,
                        IsError = message.IsError,
                    });
                    break;

                case AppChatRole.Assistant:
                {
                    FlushToolResults();
                    var blocks = new List<BetaContentBlockParam>();
                    if (!string.IsNullOrEmpty(message.Content)) blocks.Add(new BetaTextBlockParam { Text = message.Content });
                    foreach (var call in message.ToolCalls)
                    {
                        blocks.Add(new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = ParseInput(call.ArgumentsJson) });
                    }

                    if (blocks.Count > 0) messages.Add(new BetaMessageParam { Role = Role.Assistant, Content = blocks });
                    break;
                }

                default:
                {
                    FlushToolResults();
                    var blocks = new List<BetaContentBlockParam>();
                    foreach (var image in message.Images)
                    {
                        blocks.Add(new BetaImageBlockParam
                        {
                            Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(image.Data), MediaType = image.MediaType },
                        });
                    }

                    blocks.Add(new BetaTextBlockParam { Text = string.IsNullOrEmpty(message.Content) ? "(no text)" : message.Content });
                    messages.Add(new BetaMessageParam { Role = Role.User, Content = blocks });
                    break;
                }
            }
        }

        FlushToolResults();

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = Math.Clamp(request.MaxOutputTokens ?? _settings.AnthropicMaxTokens, 1024, 128000),
            Messages = messages,
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) parameters = parameters with { System = request.SystemPrompt };

        if (request.Tools.Count > 0)
        {
            parameters = parameters with { Tools = request.Tools.Select(t => (BetaToolUnion)ToTool(t)).ToList() };
        }

        var effort = ParseEffort(_settings.AnthropicEffort);
        if (request.Format == ResponseFormat.Json && request.JsonSchema is { } schema)
        {
            parameters = parameters with
            {
                OutputConfig = new BetaOutputConfig
                {
                    Effort = effort,
                    Format = new BetaJsonOutputFormat { Schema = ToDictionary(schema) },
                },
            };
        }
        else if (effort is not null)
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = effort } };
        }

        // Sampling parameters are rejected by current Claude models; only older ones accept them.
        if (SupportsSampling(_model))
        {
#pragma warning disable CS0618 // Deprecated only for models after Opus 4.6; SupportsSampling gates this to older models.
            parameters = parameters with { Temperature = request.Temperature ?? _settings.Temperature };
#pragma warning restore CS0618
        }

        if (_settings.AnthropicRefusalFallback)
        {
            parameters = parameters with
            {
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new BetaFallbacksParam(JsonSerializer.SerializeToElement("default")),
            };
        }

        return parameters;
    }

    private static BetaTool ToTool(ToolDefinition definition)
    {
        var properties = new Dictionary<string, JsonElement>();
        List<string> required = [];
        if (definition.ParametersSchema.ValueKind == JsonValueKind.Object)
        {
            if (definition.ParametersSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in props.EnumerateObject()) properties[p.Name] = p.Value.Clone();
            }

            if (definition.ParametersSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                required = req.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToList();
            }
        }

        return new BetaTool
        {
            Name = definition.Name,
            Description = definition.Description,
            EagerInputStreaming = true,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static Dictionary<string, JsonElement> ParseInput(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
            ? schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") };

    private static Effort? ParseEffort(string? effort) => effort?.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => null,
    };

    /// <summary>Older Claude models accept temperature; Opus 4.7+/Sonnet 5/Fable/Opus 5.x reject it.</summary>
    internal static bool SupportsSampling(string model)
    {
        var m = model.ToLowerInvariant();
        return m.StartsWith("claude-3", StringComparison.Ordinal)
               || m.StartsWith("claude-haiku-4-5", StringComparison.Ordinal)
               || m.StartsWith("claude-sonnet-4-5", StringComparison.Ordinal)
               || m.StartsWith("claude-sonnet-4-6", StringComparison.Ordinal)
               || m.StartsWith("claude-sonnet-4-0", StringComparison.Ordinal)
               || m.StartsWith("claude-sonnet-4-2025", StringComparison.Ordinal)
               || m.StartsWith("claude-opus-4-0", StringComparison.Ordinal)
               || m.StartsWith("claude-opus-4-1", StringComparison.Ordinal)
               || m.StartsWith("claude-opus-4-5", StringComparison.Ordinal)
               || m.StartsWith("claude-opus-4-6", StringComparison.Ordinal)
               || m.StartsWith("claude-opus-4-2025", StringComparison.Ordinal);
    }

    private static string? NormalizeStopReason(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim().Trim('"');
        return s.ToLowerInvariant() switch
        {
            "tooluse" or "tool_use" => "tool_use",
            "maxtokens" or "max_tokens" => "max_tokens",
            "refusal" => "refusal",
            _ => s.ToLowerInvariant(),
        };
    }

    private static ExternalServiceException Map(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException or AnthropicForbiddenException => new ExternalServiceException(Service, ErrorKind.Unauthorized,
            "Anthropic rejected the API key. Update it in Settings → Security → Credentials.", ex),
        AnthropicRateLimitException => new ExternalServiceException(Service, ErrorKind.RateLimited,
            "Anthropic rate limit reached. Wait a moment and try again.", ex),
        AnthropicNotFoundException => new ExternalServiceException(Service, ErrorKind.NotConfigured,
            "Anthropic did not recognize the configured model. Check the model name in Settings → AI.", ex),
        AnthropicBadRequestException or AnthropicUnprocessableEntityException => new ExternalServiceException(Service, ErrorKind.InvalidResponse,
            "Anthropic rejected the request: " + Text.Truncate(ex.Message, 300), ex),
        Anthropic5xxException => new ExternalServiceException(Service, ErrorKind.Unavailable,
            "Anthropic is having problems. Try again shortly.", ex),
        AnthropicIOException or HttpRequestException => new ExternalServiceException(Service, ErrorKind.Unavailable,
            "Could not reach Anthropic. Check your internet connection.", ex),
        TaskCanceledException or TimeoutException => new ExternalServiceException(Service, ErrorKind.Timeout,
            "Anthropic did not respond in time.", ex),
        AnthropicException => new ExternalServiceException(Service, ErrorKind.InvalidResponse,
            "Anthropic returned an error: " + Text.Truncate(ex.Message, 300), ex),
        _ => new ExternalServiceException(Service, ErrorKind.Unexpected, "Unexpected error talking to Anthropic.", ex),
    };
}
