using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using MechanicAI.Infrastructure.Http;

namespace MechanicAI.Infrastructure.Ai;

/// <summary>
/// Any server implementing the OpenAI Chat Completions API (OpenAI, Azure OpenAI-compatible
/// gateways, OpenRouter, Groq, LM Studio, vLLM...). Streams via server-sent events and
/// assembles tool-call argument fragments by index.
/// </summary>
public sealed class OpenAiCompatibleChatModel(HttpClient http, string apiKey, AiSettings settings) : IChatModel
{
    private const string Service = "Cloud AI (OpenAI-compatible)";

    public string Provider => "OpenAI-compatible (cloud)";

    public string Model => settings.OpenAiModel;

    public bool IsLocal => IsLoopback(settings.OpenAiBaseUrl);

    public ModelCapabilities Capabilities =>
        ModelCapabilities.Chat | ModelCapabilities.Tools | ModelCapabilities.StructuredOutput |
        (settings.OpenAiSupportsVision ? ModelCapabilities.Vision : ModelCapabilities.None);

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{settings.OpenAiBaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(BuildBody(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey)) httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.RequestTimeoutSeconds)));
        using var response = await HttpCall.SendAsync(http, httpRequest, Service, timeout.Token, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);

        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finish = null;
        int? inputTokens = null, outputTokens = null;
        string? line;
        while ((line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException ex)
            {
                throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "The AI provider returned malformed streaming data.", ex);
            }

            if (node?["error"] is JsonNode err)
            {
                throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "AI provider error: " + Text.Truncate(err["message"]?.ToString() ?? err.ToJsonString(), 300));
            }

            if (node?["usage"] is JsonObject usage)
            {
                inputTokens = usage["prompt_tokens"]?.GetValue<int>();
                outputTokens = usage["completion_tokens"]?.GetValue<int>();
            }

            if (node?["choices"] is not JsonArray choices || choices.Count == 0) continue;
            var choice = choices[0];
            finish = choice?["finish_reason"]?.GetValue<string>() ?? finish;
            var delta = choice?["delta"];
            var content = delta?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content)) yield return new TextDeltaUpdate(content);

            if (delta?["tool_calls"] is JsonArray toolDeltas)
            {
                foreach (var td in toolDeltas)
                {
                    var index = td?["index"]?.GetValue<int>() ?? 0;
                    if (!calls.TryGetValue(index, out var entry)) entry = (null, null, new StringBuilder());
                    entry.Id ??= td?["id"]?.GetValue<string>();
                    entry.Name ??= td?["function"]?["name"]?.GetValue<string>();
                    entry.Args.Append(td?["function"]?["arguments"]?.GetValue<string>());
                    calls[index] = entry;
                }
            }
        }

        foreach (var (index, call) in calls)
        {
            if (string.IsNullOrWhiteSpace(call.Name)) continue;
            yield return new ToolCallUpdate(new ToolCall(call.Id ?? $"call_{index}", call.Name, call.Args.Length == 0 ? "{}" : call.Args.ToString()));
        }

        var reason = finish switch
        {
            "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
            "length" => ChatFinishReason.Length,
            "content_filter" => ChatFinishReason.Refusal,
            _ => calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
        };
        yield return new CompletionUpdate(reason, inputTokens, outputTokens);
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.Tool:
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content ?? string.Empty });
                    break;
                case ChatRole.Assistant:
                {
                    var obj = new JsonObject { ["role"] = "assistant", ["content"] = m.Content ?? string.Empty };
                    if (m.ToolCalls.Count > 0)
                    {
                        obj["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                        }).ToArray());
                    }

                    messages.Add(obj);
                    break;
                }

                default:
                    if (m.Images.Count > 0 && Capabilities.HasFlag(ModelCapabilities.Vision))
                    {
                        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content ?? string.Empty } };
                        foreach (var image in m.Images)
                        {
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" },
                            });
                        }

                        messages.Add(new JsonObject { ["role"] = m.Role == ChatRole.System ? "system" : "user", ["content"] = parts });
                    }
                    else
                    {
                        messages.Add(new JsonObject { ["role"] = m.Role == ChatRole.System ? "system" : "user", ["content"] = m.Content ?? string.Empty });
                    }

                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = settings.OpenAiModel,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = request.Temperature ?? settings.Temperature,
        };
        if (request.MaxOutputTokens is { } max) body["max_tokens"] = max;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersSchema.GetRawText()),
                },
            }).ToArray());
        }

        if (request.Format == ResponseFormat.Json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body;
    }

    internal static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
}

public sealed class OpenAiCompatibleEmbeddingModel(HttpClient http, string apiKey, AiSettings settings) : IEmbeddingModel
{
    public string Provider => "OpenAI-compatible (cloud)";

    public string Model => settings.OpenAiEmbeddingModel;

    public bool IsLocal => OpenAiCompatibleChatModel.IsLoopback(settings.OpenAiBaseUrl);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var results = new List<float[]>(inputs.Count);
        foreach (var batch in inputs.Chunk(64))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.OpenAiBaseUrl.TrimEnd('/')}/embeddings")
            {
                Content = JsonContent.Create(new { model = settings.OpenAiEmbeddingModel, input = batch }),
            };
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await HttpCall.SendAsync(http, request, "Cloud embeddings", cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = HttpCall.ParseDocument(json, "Cloud embeddings");
            var data = doc.RootElement.GetProperty("data").EnumerateArray()
                .OrderBy(d => d.TryGetProperty("index", out var i) ? i.GetInt32() : 0)
                .Select(d => d.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray())
                .ToList();
            if (data.Count != batch.Length)
            {
                throw new ExternalServiceException("Cloud embeddings", ErrorKind.InvalidResponse, "Embedding response did not match the request.");
            }

            results.AddRange(data);
        }

        return results;
    }
}
