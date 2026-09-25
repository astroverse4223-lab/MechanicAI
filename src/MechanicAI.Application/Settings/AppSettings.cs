using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Settings;

/// <summary>
/// User-configurable settings persisted as JSON. Contains NO secrets: API keys and
/// tokens live in <see cref="Abstractions.ISecretStore"/>.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public AiSettings Ai { get; set; } = new();

    public SearchSettings Search { get; set; } = new();

    public VehicleDataSettings VehicleData { get; set; } = new();

    public AppearanceSettings Appearance { get; set; } = new();

    public PrivacySettings Privacy { get; set; } = new();

    public DiagnosticsSettings Diagnostics { get; set; } = new();

    public SecuritySettings Security { get; set; } = new();

    public ShopServerSettings ShopServer { get; set; } = new();

    public LiveDataSettings LiveData { get; set; } = new();

    public bool FirstRunCompleted { get; set; }

    public bool SampleDataInstalled { get; set; }

    public Guid? ActiveVehicleId { get; set; }

    public Guid? ActiveSessionId { get; set; }

    public AppSettings Clone() => Common.Json.Deserialize<AppSettings>(Common.Json.Serialize(this)) ?? new AppSettings();
}

public enum AiMode
{
    /// <summary>Only local models (Ollama). Nothing leaves the workstation.</summary>
    Local,
    /// <summary>Only the configured cloud provider.</summary>
    Cloud,
    /// <summary>Local models for private documents/images; cloud for advanced reasoning.</summary>
    Hybrid,
    /// <summary>AI features disabled; deterministic features still work.</summary>
    Disabled,
}

public enum CloudAiProvider { Anthropic, OpenAiCompatible }

public sealed class AiSettings
{
    public AiMode Mode { get; set; } = AiMode.Local;

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Chat model for reasoning and tool calling. Empty = auto-select from installed models.</summary>
    public string OllamaChatModel { get; set; } = string.Empty;

    /// <summary>Vision-capable model for image and wiring analysis. Empty = auto-select.</summary>
    public string OllamaVisionModel { get; set; } = string.Empty;

    public string OllamaEmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>Whether to request "thinking" from reasoning models (null = model default).</summary>
    public bool? OllamaThink { get; set; } = false;

    public string OllamaKeepAlive { get; set; } = "10m";

    public CloudAiProvider CloudProvider { get; set; } = CloudAiProvider.Anthropic;

    public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";

    public string AnthropicModel { get; set; } = "claude-opus-5";

    /// <summary>Claude effort level (low, medium, high, xhigh, max). Empty = model default.</summary>
    public string AnthropicEffort { get; set; } = "high";

    /// <summary>Output cap for Claude (thinking + text). Requests are streamed, so a large cap is safe.</summary>
    public int AnthropicMaxTokens { get; set; } = 64000;

    /// <summary>
    /// Opt into server-side refusal fallback (<c>fallbacks: "default"</c>): if Claude's safety
    /// classifiers decline a request, the API re-runs it on Anthropic's recommended fallback model.
    /// Disable when routing through a proxy that rejects unknown fields.
    /// </summary>
    public bool AnthropicRefusalFallback { get; set; } = true;

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string OpenAiModel { get; set; } = "gpt-4.1";

    public string OpenAiEmbeddingModel { get; set; } = "text-embedding-3-small";

    public bool OpenAiSupportsVision { get; set; } = true;

    /// <summary>Where embeddings are computed. Local keeps document text on the workstation.</summary>
    public bool UseCloudEmbeddings { get; set; }

    public double Temperature { get; set; } = 0.2;

    public int ContextSize { get; set; } = 16384;

    public int MaxOutputTokens { get; set; } = 4096;

    public int MaxToolRounds { get; set; } = 8;

    public int RequestTimeoutSeconds { get; set; } = 240;
}

public enum WebSearchProviderKind { None, Brave, Tavily, SearXng }

public sealed class SearchSettings
{
    public WebSearchProviderKind Provider { get; set; } = WebSearchProviderKind.None;

    public string SearxngBaseUrl { get; set; } = "http://localhost:8080";

    public int MaxResults { get; set; } = 10;

    /// <summary>Fetch and read the top pages so summaries are grounded in page content, not snippets.</summary>
    public bool FetchPages { get; set; } = true;

    public int PagesToFetch { get; set; } = 5;

    public bool IncludeForums { get; set; } = true;

    public bool IncludeSocialMedia { get; set; }

    /// <summary>Domains the technician trusts (boosted in ranking).</summary>
    public List<string> PreferredDomains { get; set; } = [];

    /// <summary>Domains never shown.</summary>
    public List<string> BlockedDomains { get; set; } = [];

    public int CacheHours { get; set; } = 72;
}

public sealed class VehicleDataSettings
{
    public string VpicBaseUrl { get; set; } = "https://vpic.nhtsa.dot.gov/api/";

    public string NhtsaApiBaseUrl { get; set; } = "https://api.nhtsa.gov/";

    public int CacheDays { get; set; } = 30;

    public bool AutoCheckRecalls { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 20;
}

public enum AppTheme { System, Dark, Light }

public enum UiDensity { Comfortable, Compact }

public sealed class AppearanceSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Accent color as #RRGGBB, or empty to follow Windows.</summary>
    public string AccentColor { get; set; } = "#3D8BFD";

    public UiDensity Density { get; set; } = UiDensity.Comfortable;

    public bool ReduceMotion { get; set; }
}

public sealed class PrivacySettings
{
    /// <summary>
    /// Anonymous usage telemetry. Off by default. The app contains no telemetry
    /// uploader; this flag exists so a future opt-in feature has a consent gate.
    /// </summary>
    public bool TelemetryEnabled { get; set; }

    /// <summary>Master switch: may any data be sent to a cloud AI provider?</summary>
    public bool AllowCloudAi { get; set; }

    /// <summary>May private knowledge-base passages be sent to cloud AI (Hybrid/Cloud modes)?</summary>
    public bool AllowDocumentsInCloud { get; set; }

    /// <summary>May photos be sent to cloud AI for analysis?</summary>
    public bool AllowImagesInCloud { get; set; }

    /// <summary>May customer names/contact info be included in AI prompts? (Vehicle data is always allowed.)</summary>
    public bool IncludeCustomerInfoInAi { get; set; }
}

public enum UnitSystem { Imperial, Metric }

public enum PressureUnit { Psi, Kpa, Bar }

public enum TemperatureUnit { Fahrenheit, Celsius }

public sealed class DiagnosticsSettings
{
    public UnitSystem Units { get; set; } = UnitSystem.Imperial;

    public PressureUnit Pressure { get; set; } = PressureUnit.Psi;

    public TemperatureUnit Temperature { get; set; } = TemperatureUnit.Fahrenheit;

    public bool ShowProbabilities { get; set; } = true;

    /// <summary>Posterior probability at which a cause is considered isolated (pending confirmation).</summary>
    public double IsolationThreshold { get; set; } = 0.80;

    /// <summary>Posterior below which a cause is shown as ruled out.</summary>
    public double RuleOutThreshold { get; set; } = 0.02;

    public string TechnicianName { get; set; } = string.Empty;

    public bool TechnicianHighVoltageCertified { get; set; }

    public string ShopName { get; set; } = string.Empty;
}

public sealed class SecuritySettings
{
    /// <summary>Require the workstation password when the app starts.</summary>
    public bool RequireSignIn { get; set; }

    /// <summary>Lock after this many idle minutes (0 = never).</summary>
    public int AutoLockMinutes { get; set; }
}

public sealed class ShopServerSettings
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "https://localhost:7443";

    public string? UserEmail { get; set; }

    public bool ShareLiveData { get; set; }

    public DateTime? LastSyncUtc { get; set; }
}

public sealed class LiveDataSettings
{
    public string? LastPort { get; set; }

    public int BaudRate { get; set; } = 38400;

    public int PollIntervalMs { get; set; } = 200;

    public List<string> DefaultPids { get; set; } =
        ["RPM", "SPEED", "ECT", "LOAD", "TPS", "MAF", "MAP", "STFT1", "LTFT1", "STFT2", "LTFT2", "O2S1B1", "O2S1B2", "VPWR", "TIMING", "FRP"];
}

public static class SettingsExtensions
{
    public static string FormatTemperatureC(this DiagnosticsSettings s, double celsius) =>
        s.Temperature == TemperatureUnit.Celsius ? $"{celsius:0} °C" : $"{celsius * 9 / 5 + 32:0} °F";

    public static string FormatPressureKpa(this DiagnosticsSettings s, double kpa) => s.Pressure switch
    {
        PressureUnit.Kpa => $"{kpa:0} kPa",
        PressureUnit.Bar => $"{kpa / 100:0.00} bar",
        _ => $"{kpa * 0.1450377:0.0} psi",
    };

    public static string FormatSpeedKph(this DiagnosticsSettings s, double kph) =>
        s.Units == UnitSystem.Metric ? $"{kph:0} km/h" : $"{kph * 0.621371:0} mph";

    public static string FormatDistance(this DiagnosticsSettings s, int? value, DistanceUnit unit) =>
        value is null ? "—" : unit == DistanceUnit.Kilometers ? $"{value:N0} km" : $"{value:N0} mi";
}
