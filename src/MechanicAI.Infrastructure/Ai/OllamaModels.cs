using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using MechanicAI.Infrastructure.Http;

namespace MechanicAI.Infrastructure.Ai;

/// <summary>Chat model served by a local Ollama instance. Nothing leaves the workstation.</summary>
public sealed partial class OllamaChatModel(
    HttpClient http,
    string baseUrl,
    string model,
    ModelCapabilities capabilities,
    AiSettings settings) : IChatModel
{
    private const string Service = "Ollama";

    public string Provider => "Ollama (local)";

    public string Model => model;

    public bool IsLocal => true;

    public ModelCapabilities Capabilities => capabilities;

    public async IAsyncEnumerable<ChatStreamUpdate> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildBody(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.RequestTimeoutSeconds)));
        HttpResponseMessage response;
        try
        {
            response = await HttpCall.SendAsync(http, httpRequest, Service, timeout.Token, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (ExternalServiceException ex) when (ex.Kind == ErrorKind.NotFound)
        {
            throw new ExternalServiceException(Service, ErrorKind.NotConfigured,
                $"The Ollama model '{model}' is not installed. Install it with: ollama pull {model}", ex);
        }
        catch (ExternalServiceException ex) when (ex.Kind == ErrorKind.Unavailable)
        {
            throw new ExternalServiceException(Service, ErrorKind.Unavailable,
                $"Ollama is not reachable at {baseUrl}. Start Ollama or check the URL in Settings → AI.", ex);
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream);
            var inThink = false;
            var callIndex = 0;
            string? line;
            while ((line = await ReadLineAsync(reader, timeout.Token, cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException ex)
                {
                    throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "Ollama returned malformed streaming data.", ex);
                }

                if (node?["error"]?.GetValue<string>() is { } error)
                {
                    throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, $"Ollama error: {Text.Truncate(error, 300)}");
                }

                var message = node?["message"];
                var content = message?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(content))
                {
                    // Some reasoning models emit <think>…</think> inline; never show that as answer text.
                    var visible = StripThinking(content, ref inThink);
                    if (visible.Length > 0) yield return new TextDeltaUpdate(visible);
                }

                if (message?["tool_calls"] is JsonArray calls)
                {
                    foreach (var call in calls)
                    {
                        var function = call?["function"];
                        var name = function?["name"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var args = function?["arguments"];
                        var argsJson = args switch
                        {
                            null => "{}",
                            JsonValue v when v.TryGetValue<string>(out var s) => s,
                            _ => args.ToJsonString(),
                        };
                        var id = call?["id"]?.GetValue<string>() ?? $"ollama_call_{callIndex}";
                        callIndex++;
                        yield return new ToolCallUpdate(new ToolCall(id, name, argsJson));
                    }
                }

                if (node?["done"]?.GetValue<bool>() == true)
                {
                    var reason = node["done_reason"]?.GetValue<string>();
                    var finish = callIndex > 0 ? ChatFinishReason.ToolCalls : reason == "length" ? ChatFinishReason.Length : ChatFinishReason.Stop;
                    yield return new CompletionUpdate(finish, node["prompt_eval_count"]?.GetValue<int>(), node["eval_count"]?.GetValue<int>());
                    yield break;
                }
            }
        }

        yield return new CompletionUpdate(ChatFinishReason.Stop, null, null);
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken timeoutToken, CancellationToken userToken)
    {
        try
        {
            return await reader.ReadLineAsync(timeoutToken);
        }
        catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
        {
            throw new ExternalServiceException(Service, ErrorKind.Timeout, "The local model took too long to respond. Try a smaller model or raise the AI timeout.");
        }
    }

    internal JsonObject BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }

        foreach (var m in request.Messages)
        {
            var obj = new JsonObject
            {
                ["role"] = m.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.Assistant => "assistant",
                    ChatRole.Tool => "tool",
                    _ => "user",
                },
                ["content"] = m.Content ?? string.Empty,
            };

            if (m.Images.Count > 0 && capabilities.HasFlag(ModelCapabilities.Vision))
            {
                obj["images"] = new JsonArray(m.Images.Select(i => (JsonNode)Convert.ToBase64String(i.Data)).ToArray());
            }

            if (m.ToolCalls.Count > 0)
            {
                obj["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = c.Name,
                        ["arguments"] = SafeParse(c.ArgumentsJson),
                    },
                }).ToArray());
            }

            if (m.Role == ChatRole.Tool && m.ToolName is not null) obj["tool_name"] = m.ToolName;
            messages.Add(obj);
        }

        var options = new JsonObject
        {
            ["temperature"] = request.Temperature ?? settings.Temperature,
            ["num_ctx"] = Math.Clamp(settings.ContextSize, 2048, 262144),
        };
        if (request.MaxOutputTokens is { } max) options["num_predict"] = max;

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["options"] = options,
            ["keep_alive"] = settings.OllamaKeepAlive,
        };

        if (capabilities.HasFlag(ModelCapabilities.Thinking) && settings.OllamaThink is { } think)
        {
            body["think"] = think;
        }

        if (request.Tools.Count > 0 && capabilities.HasFlag(ModelCapabilities.Tools))
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

        if (request.Format == ResponseFormat.Json)
        {
            body["format"] = request.JsonSchema is { } schema ? JsonNode.Parse(schema.GetRawText()) : "json";
        }

        return body;
    }

    private static JsonNode SafeParse(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    internal static string StripThinking(string content, ref bool inThink)
    {
        var sb = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            if (inThink)
            {
                var end = content.IndexOf("</think>", i, StringComparison.Ordinal);
                if (end < 0) return sb.ToString();
                inThink = false;
                i = end + "</think>".Length;
            }
            else
            {
                var start = content.IndexOf("<think>", i, StringComparison.Ordinal);
                if (start < 0)
                {
                    sb.Append(content, i, content.Length - i);
                    break;
                }

                sb.Append(content, i, start - i);
                inThink = true;
                i = start + "<think>".Length;
            }
        }

        return sb.ToString();
    }
}

/// <summary>Embedding model served by Ollama (/api/embed).</summary>
public sealed class OllamaEmbeddingModel(HttpClient http, string baseUrl, string model) : IEmbeddingModel
{
    public string Provider => "Ollama (local)";

    public string Model => model;

    public bool IsLocal => true;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (inputs.Count == 0) return [];
        var results = new List<float[]>(inputs.Count);
        foreach (var batch in inputs.Chunk(32))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/embed")
            {
                Content = JsonContent.Create(new { model, input = batch, truncate = true }),
            };
            using var response = await HttpCall.SendAsync(http, request, "Ollama", cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = HttpCall.ParseDocument(json, "Ollama");
            if (!doc.RootElement.TryGetProperty("embeddings", out var embeddings) || embeddings.GetArrayLength() != batch.Length)
            {
                throw new ExternalServiceException("Ollama", ErrorKind.InvalidResponse, "Ollama returned an unexpected embedding response.");
            }

            foreach (var e in embeddings.EnumerateArray())
            {
                var vector = new float[e.GetArrayLength()];
                var i = 0;
                foreach (var v in e.EnumerateArray()) vector[i++] = v.GetSingle();
                results.Add(vector);
            }
        }

        return results;
    }
}

/// <summary>Ollama server management: health, installed models with capabilities, model pulls.</summary>
public sealed partial class OllamaManagement(IHttpClientFactory httpFactory, ISettingsStore settingsStore) : IOllamaManagement
{
    public const string HttpClientName = "ollama";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTime At, string Url, IReadOnlyList<LocalModelInfo> Models)? _cache;

    private string BaseUrl => settingsStore.Current.Ai.OllamaBaseUrl.TrimEnd('/');

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await httpFactory.CreateClient(HttpClientName).GetAsync($"{BaseUrl}/api/version", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
    }

    public async Task<IReadOnlyList<LocalModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var url = BaseUrl;
        if (_cache is { } c && c.Url == url && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60)) return c.Models;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } c2 && c2.Url == url && DateTime.UtcNow - c2.At < TimeSpan.FromSeconds(60)) return c2.Models;
            var http = httpFactory.CreateClient(HttpClientName);
            var json = await HttpCall.GetStringAsync(http, $"{url}/api/tags", "Ollama", cancellationToken);
            using var doc = HttpCall.ParseDocument(json, "Ollama");
            var models = new List<LocalModelInfo>();
            if (doc.RootElement.TryGetProperty("models", out var list))
            {
                foreach (var m in list.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                    string? family = null, parameterSize = null;
                    if (m.TryGetProperty("details", out var details))
                    {
                        family = details.TryGetProperty("family", out var f) ? f.GetString() : null;
                        parameterSize = details.TryGetProperty("parameter_size", out var p) ? p.GetString() : null;
                    }

                    var (caps, context) = await ShowAsync(http, url, name, cancellationToken);
                    models.Add(new LocalModelInfo(name, size, caps, family, parameterSize, context));
                }
            }

            _cache = (DateTime.UtcNow, url, models);
            return models;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cache = null;

    private static async Task<(ModelCapabilities, int?)> ShowAsync(HttpClient http, string url, string name, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/show") { Content = JsonContent.Create(new { model = name }) };
            using var response = await HttpCall.SendAsync(http, request, "Ollama", ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var caps = ModelCapabilities.None;
            if (doc.RootElement.TryGetProperty("capabilities", out var list))
            {
                foreach (var cap in list.EnumerateArray().Select(e => e.GetString()))
                {
                    caps |= cap switch
                    {
                        "completion" => ModelCapabilities.Chat | ModelCapabilities.StructuredOutput,
                        "tools" => ModelCapabilities.Tools,
                        "vision" => ModelCapabilities.Vision,
                        "embedding" => ModelCapabilities.Embeddings,
                        "thinking" => ModelCapabilities.Thinking,
                        _ => ModelCapabilities.None,
                    };
                }
            }
            else
            {
                caps = GuessCapabilities(name);
            }

            int? context = null;
            if (doc.RootElement.TryGetProperty("model_info", out var info))
            {
                foreach (var p in info.EnumerateObject())
                {
                    if (p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.TryGetInt32(out var ctx)) context = ctx;
                }
            }

            return (caps, context);
        }
        catch (Exception ex) when (ex is ExternalServiceException or JsonException)
        {
            return (GuessCapabilities(name), null);
        }
    }

    /// <summary>Fallback for older Ollama versions without a capabilities list.</summary>
    internal static ModelCapabilities GuessCapabilities(string name)
    {
        var n = name.ToLowerInvariant();
        if (EmbeddingNameRegex().IsMatch(n)) return ModelCapabilities.Embeddings;
        var caps = ModelCapabilities.Chat | ModelCapabilities.StructuredOutput;
        if (VisionNameRegex().IsMatch(n)) caps |= ModelCapabilities.Vision;
        if (ToolNameRegex().IsMatch(n)) caps |= ModelCapabilities.Tools;
        return caps;
    }

    public async Task PullModelAsync(string model, IProgress<ModelPullProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\')) throw new ArgumentException("Invalid model name.");
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/pull") { Content = JsonContent.Create(new { model, stream = true }) };
        using var response = await HttpCall.SendAsync(http, request, "Ollama", cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new ExternalServiceException("Ollama", ErrorKind.InvalidResponse, $"Model download failed: {error.GetString()}");
            }

            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            long? completed = root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv) ? cv : null;
            long? total = root.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv) ? tv : null;
            progress?.Report(new ModelPullProgress(status, completed, total));
        }

        Invalidate();
    }

    [GeneratedRegex("embed|bge|minilm|e5-|gte-|snowflake-arctic-embed|paraphrase")]
    private static partial Regex EmbeddingNameRegex();

    [GeneratedRegex("vl|vision|llava|bakllava|moondream|minicpm-v|gemma3|llama4|mistral-small3")]
    private static partial Regex VisionNameRegex();

    [GeneratedRegex("qwen2\\.5|qwen3|llama3\\.[123]|mistral|mixtral|command-r|firefunction|hermes|granite|phi4|gpt-oss|nemotron|devstral|smollm2")]
    private static partial Regex ToolNameRegex();
}
