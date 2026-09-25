using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Settings;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Ai;

/// <summary>
/// Chooses the model for each request.
/// <list type="bullet">
/// <item>Local: Ollama only — nothing leaves the workstation.</item>
/// <item>Cloud: the configured cloud provider, only if cloud AI is allowed in Privacy settings.</item>
/// <item>Hybrid: private documents and photos stay local; general reasoning goes to the cloud,
/// falling back to local when the cloud is unavailable.</item>
/// </list>
/// Every "unavailable" answer carries a reason the UI shows verbatim.
/// </summary>
public sealed class AiRouter(
    ISettingsStore settingsStore,
    ISecretStore secrets,
    OllamaManagement ollama,
    IHttpClientFactory httpFactory,
    ILogger<AiRouter> logger) : IAiRouter
{
    public const string StreamingClientName = "ai-stream";

    private static readonly string[] PreferredChatFamilies = ["qwen3", "qwen2.5", "llama3.1", "llama3.2", "mistral-nemo", "mistral", "gemma3", "phi4", "granite"];
    private static readonly string[] PreferredVisionFamilies = ["qwen2.5vl", "qwen2.5-vl", "llama3.2-vision", "gemma3", "llava", "minicpm-v", "moondream"];

    public void Invalidate() => ollama.Invalidate();

    public async Task<AiRoute> ResolveChatAsync(AiTask task, DataSensitivity sensitivity, bool requireVision = false,
        CancellationToken cancellationToken = default)
    {
        var s = settingsStore.Current;
        var ai = s.Ai;
        if (ai.Mode == AiMode.Disabled) return AiRoute.Unavailable("AI features are turned off. Enable them in Settings → AI.");

        var cloudPermitted = CloudPermitted(s.Privacy, sensitivity, out var privacyReason);
        var wantCloudFirst = ai.Mode == AiMode.Cloud || (ai.Mode == AiMode.Hybrid && sensitivity == DataSensitivity.General);

        if (wantCloudFirst)
        {
            if (!cloudPermitted && ai.Mode == AiMode.Cloud) return AiRoute.Unavailable(privacyReason!);
            if (cloudPermitted)
            {
                var (cloud, cloudReason) = await TryCloudAsync(ai, requireVision, cancellationToken);
                if (cloud is not null) return new AiRoute(cloud, null);
                if (ai.Mode == AiMode.Cloud) return AiRoute.Unavailable(cloudReason!);
                logger.LogInformation("Hybrid mode: cloud unavailable ({Reason}); using local model", cloudReason);
            }
        }

        var (local, localReason) = await TryLocalAsync(ai, requireVision, cancellationToken);
        if (local is not null) return new AiRoute(local, null);

        if (ai.Mode == AiMode.Hybrid && cloudPermitted && !wantCloudFirst)
        {
            var (cloud, _) = await TryCloudAsync(ai, requireVision, cancellationToken);
            if (cloud is not null) return new AiRoute(cloud, null);
        }

        if (ai.Mode == AiMode.Hybrid && !cloudPermitted && privacyReason is not null)
        {
            return AiRoute.Unavailable($"{localReason} ({privacyReason})");
        }

        return AiRoute.Unavailable(localReason!);
    }

    public async Task<IEmbeddingModel?> ResolveEmbeddingAsync(CancellationToken cancellationToken = default)
    {
        var s = settingsStore.Current;
        if (s.Ai.Mode == AiMode.Disabled) return null;

        if (s.Ai.UseCloudEmbeddings && s.Privacy.AllowCloudAi && s.Privacy.AllowDocumentsInCloud)
        {
            var key = await secrets.GetAsync(SecretNames.OpenAiApiKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(key) || OpenAiCompatibleChatModel.IsLoopback(s.Ai.OpenAiBaseUrl))
            {
                return new OpenAiCompatibleEmbeddingModel(httpFactory.CreateClient(StreamingClientName), key ?? string.Empty, s.Ai);
            }
        }

        var name = await FindLocalEmbeddingModelAsync(s.Ai, cancellationToken);
        return name is null ? null : new OllamaEmbeddingModel(httpFactory.CreateClient(StreamingClientName), s.Ai.OllamaBaseUrl, name);
    }

    public async Task<AiStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (refresh) ollama.Invalidate();
        var s = settingsStore.Current;
        var notes = new List<string>();
        var reachable = await ollama.PingAsync(cancellationToken);
        string? chat = null, vision = null, embedding = null;
        if (reachable)
        {
            var models = await SafeListAsync(cancellationToken);
            chat = PickChat(models, s.Ai)?.Name;
            vision = PickVision(models, s.Ai)?.Name;
            embedding = await FindLocalEmbeddingModelAsync(s.Ai, cancellationToken);
            if (chat is null) notes.Add("No local chat model installed. Example: ollama pull qwen3:8b");
            else if (!(PickChat(models, s.Ai)?.Capabilities.HasFlag(ModelCapabilities.Tools) ?? false))
            {
                notes.Add($"'{chat}' does not support tool calling; research tools run before the answer instead.");
            }

            if (vision is null) notes.Add("No local vision model — image analysis needs one (e.g. ollama pull qwen2.5vl:7b).");
            if (embedding is null) notes.Add($"No embedding model — the knowledge base uses keyword search only (ollama pull {s.Ai.OllamaEmbeddingModel}).");
        }
        else if (s.Ai.Mode is AiMode.Local or AiMode.Hybrid)
        {
            notes.Add($"Ollama is not reachable at {s.Ai.OllamaBaseUrl}.");
        }

        var cloudKey = s.Ai.CloudProvider == CloudAiProvider.Anthropic
            ? await secrets.GetAsync(SecretNames.AnthropicApiKey, cancellationToken)
            : await secrets.GetAsync(SecretNames.OpenAiApiKey, cancellationToken);
        var cloudConfigured = !string.IsNullOrWhiteSpace(cloudKey) && s.Privacy.AllowCloudAi && s.Ai.Mode is AiMode.Cloud or AiMode.Hybrid;
        if (s.Ai.Mode is AiMode.Cloud or AiMode.Hybrid)
        {
            if (!s.Privacy.AllowCloudAi) notes.Add("Cloud AI is not allowed in Privacy settings.");
            else if (string.IsNullOrWhiteSpace(cloudKey)) notes.Add("No cloud API key saved (Settings → Security → Credentials).");
        }

        var cloudModel = s.Ai.CloudProvider == CloudAiProvider.Anthropic ? s.Ai.AnthropicModel : s.Ai.OpenAiModel;
        return new AiStatus(s.Ai.Mode, reachable, chat, vision, embedding, embedding is not null, cloudConfigured,
            cloudConfigured ? cloudModel : null, notes);
    }

    private async Task<(IChatModel? Model, string? Reason)> TryLocalAsync(AiSettings ai, bool requireVision, CancellationToken ct)
    {
        if (!await ollama.PingAsync(ct))
        {
            return (null, $"Ollama is not reachable at {ai.OllamaBaseUrl}. Start Ollama (or change the URL in Settings → AI).");
        }

        var models = await SafeListAsync(ct);
        var pick = requireVision ? PickVision(models, ai) : PickChat(models, ai);
        if (pick is null)
        {
            return (null, requireVision
                ? "No local vision model is installed. Install one, e.g.: ollama pull qwen2.5vl:7b"
                : "No local chat model is installed. Install one, e.g.: ollama pull qwen3:8b");
        }

        return (new OllamaChatModel(httpFactory.CreateClient(StreamingClientName), ai.OllamaBaseUrl, pick.Name, pick.Capabilities, ai), null);
    }

    private async Task<(IChatModel? Model, string? Reason)> TryCloudAsync(AiSettings ai, bool requireVision, CancellationToken ct)
    {
        if (ai.CloudProvider == CloudAiProvider.Anthropic)
        {
            var key = await secrets.GetAsync(SecretNames.AnthropicApiKey, ct);
            if (string.IsNullOrWhiteSpace(key)) return (null, "No Anthropic API key is saved. Add it in Settings → Security → Credentials.");
            return (new AnthropicChatModel(key, ai), null);
        }

        var openAiKey = await secrets.GetAsync(SecretNames.OpenAiApiKey, ct);
        if (string.IsNullOrWhiteSpace(openAiKey) && !OpenAiCompatibleChatModel.IsLoopback(ai.OpenAiBaseUrl))
        {
            return (null, "No API key is saved for the OpenAI-compatible provider. Add it in Settings → Security → Credentials.");
        }

        if (requireVision && !ai.OpenAiSupportsVision) return (null, "The configured cloud model is not marked as vision-capable (Settings → AI).");
        return (new OpenAiCompatibleChatModel(httpFactory.CreateClient(StreamingClientName), openAiKey ?? string.Empty, ai), null);
    }

    private static bool CloudPermitted(PrivacySettings privacy, DataSensitivity sensitivity, out string? reason)
    {
        if (!privacy.AllowCloudAi)
        {
            reason = "cloud AI is not allowed in Settings → Privacy";
            return false;
        }

        switch (sensitivity)
        {
            case DataSensitivity.PrivateDocuments when !privacy.AllowDocumentsInCloud:
                reason = "private documents are kept local by your Privacy settings";
                return false;
            case DataSensitivity.Images when !privacy.AllowImagesInCloud:
                reason = "photos are kept local by your Privacy settings";
                return false;
            default:
                reason = null;
                return true;
        }
    }

    private async Task<IReadOnlyList<LocalModelInfo>> SafeListAsync(CancellationToken ct)
    {
        try
        {
            return await ollama.ListModelsAsync(ct);
        }
        catch (Application.Common.ExternalServiceException ex)
        {
            logger.LogWarning("Could not list Ollama models: {Message}", ex.UserMessage);
            return [];
        }
    }

    private async Task<string?> FindLocalEmbeddingModelAsync(AiSettings ai, CancellationToken ct)
    {
        if (!await ollama.PingAsync(ct)) return null;
        var models = await SafeListAsync(ct);
        var configured = Find(models, ai.OllamaEmbeddingModel);
        if (configured is not null) return configured.Name;
        return models.FirstOrDefault(m => m.Capabilities.HasFlag(ModelCapabilities.Embeddings))?.Name;
    }

    internal static LocalModelInfo? PickChat(IReadOnlyList<LocalModelInfo> models, AiSettings ai)
    {
        var configured = Find(models, ai.OllamaChatModel);
        if (configured is not null && configured.Capabilities.HasFlag(ModelCapabilities.Chat)) return configured;

        return models
            .Where(m => m.Capabilities.HasFlag(ModelCapabilities.Chat) && !m.Capabilities.HasFlag(ModelCapabilities.Embeddings))
            .OrderByDescending(m => m.Capabilities.HasFlag(ModelCapabilities.Tools))
            .ThenByDescending(m => FamilyRank(m.Name, PreferredChatFamilies))
            .ThenByDescending(m => SizeRank(m.ParameterSize))
            .FirstOrDefault();
    }

    internal static LocalModelInfo? PickVision(IReadOnlyList<LocalModelInfo> models, AiSettings ai)
    {
        var configured = Find(models, ai.OllamaVisionModel);
        if (configured is not null && configured.Capabilities.HasFlag(ModelCapabilities.Vision)) return configured;
        return models
            .Where(m => m.Capabilities.HasFlag(ModelCapabilities.Vision))
            .OrderByDescending(m => FamilyRank(m.Name, PreferredVisionFamilies))
            .ThenByDescending(m => SizeRank(m.ParameterSize))
            .FirstOrDefault();
    }

    private static LocalModelInfo? Find(IReadOnlyList<LocalModelInfo> models, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return models.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(m => m.Name.Equals(name + ":latest", StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(m => m.Name.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
    }

    private static int FamilyRank(string name, string[] preferred)
    {
        for (var i = 0; i < preferred.Length; i++)
        {
            if (name.StartsWith(preferred[i], StringComparison.OrdinalIgnoreCase)) return preferred.Length - i;
        }

        return 0;
    }

    /// <summary>Prefer ~7–14B models: capable enough for tool use, fast enough on a shop workstation.</summary>
    private static int SizeRank(string? parameterSize)
    {
        if (parameterSize is null) return 0;
        var digits = new string(parameterSize.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b)) return 0;
        return b switch
        {
            >= 7 and <= 15 => 3,
            > 15 and <= 34 => 2,
            >= 3 and < 7 => 1,
            _ => 0,
        };
    }
}
