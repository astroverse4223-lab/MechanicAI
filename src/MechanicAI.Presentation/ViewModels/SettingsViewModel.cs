using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>
/// Settings: AI providers (Ollama / Anthropic / OpenAI-compatible), privacy, web search, units,
/// appearance, security, and data. API keys go to the encrypted secret store and are never shown.
/// </summary>
public sealed partial class SettingsViewModel(
    ISettingsStore settings,
    ISecretStore secrets,
    ISystemGateway system,
    IDialogService dialogs,
    ILauncher launcher,
    IAppPaths paths,
    IDispatcher dispatcher) : ViewModelBase
{
    public static readonly IReadOnlyList<string> AnthropicEffortLevels = new[] { "", "low", "medium", "high", "xhigh", "max" };

    public IReadOnlyList<string> AiModes { get; } = new[] { "Local (Ollama only)", "Cloud (provider only)", "Hybrid (local for private data)", "Disabled" };

    public IReadOnlyList<string> CloudProviders { get; } = new[] { "Anthropic (Claude)", "OpenAI-compatible" };

    public IReadOnlyList<string> EffortLevels { get; } = new[] { "Model default", "Low", "Medium", "High", "Extra high", "Max" };

    public IReadOnlyList<string> SearchProviders { get; } = new[] { "None (offline only)", "Brave Search", "Tavily", "SearXNG (self-hosted)" };

    public IReadOnlyList<string> Themes { get; } = new[] { "Use Windows setting", "Dark", "Light" };

    public IReadOnlyList<string> UnitSystems { get; } = new[] { "Imperial", "Metric" };

    public IReadOnlyList<string> PressureUnits { get; } = new[] { "psi", "kPa", "bar" };

    public IReadOnlyList<string> TemperatureUnits { get; } = new[] { "°F", "°C" };

    public ObservableCollection<string> AiStatusLines { get; } = [];

    public ObservableCollection<ModelItem> LocalModels { get; } = [];

    // ---------------------------------------------------------------- AI

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesLocal), nameof(UsesCloud))]
    public partial int AiModeIndex { get; set; }

    public bool UsesLocal => AiModeIndex is 0 or 2;

    public bool UsesCloud => AiModeIndex is 1 or 2;

    [ObservableProperty]
    public partial string OllamaBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OllamaChatModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OllamaVisionModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OllamaEmbeddingModel { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnthropic), nameof(IsOpenAi))]
    public partial int CloudProviderIndex { get; set; }

    public bool IsAnthropic => CloudProviderIndex == 0;

    public bool IsOpenAi => CloudProviderIndex == 1;

    [ObservableProperty]
    public partial string AnthropicBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AnthropicModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int AnthropicEffortIndex { get; set; }

    [ObservableProperty]
    public partial string OpenAiBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAiModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAiEmbeddingModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseCloudEmbeddings { get; set; }

    [ObservableProperty]
    public partial double Temperature { get; set; }

    /// <summary>New key typed by the user; empty keeps the stored key.</summary>
    [ObservableProperty]
    public partial string AnthropicApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAiApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AnthropicKeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OpenAiKeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? PullModelName { get; set; }

    [ObservableProperty]
    public partial string? PullProgress { get; set; }

    // ---------------------------------------------------------------- privacy

    [ObservableProperty]
    public partial bool AllowCloudAi { get; set; }

    [ObservableProperty]
    public partial bool AllowDocumentsInCloud { get; set; }

    [ObservableProperty]
    public partial bool AllowImagesInCloud { get; set; }

    [ObservableProperty]
    public partial bool IncludeCustomerInfoInAi { get; set; }

    // ---------------------------------------------------------------- search

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrave), nameof(IsTavily), nameof(IsSearxng), nameof(UsesWebSearch))]
    public partial int SearchProviderIndex { get; set; }

    public bool UsesWebSearch => SearchProviderIndex > 0;

    public bool IsBrave => SearchProviderIndex == 1;

    public bool IsTavily => SearchProviderIndex == 2;

    public bool IsSearxng => SearchProviderIndex == 3;

    [ObservableProperty]
    public partial string SearxngBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchKeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IncludeForums { get; set; }

    // ---------------------------------------------------------------- workstation

    [ObservableProperty]
    public partial string TechnicianName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ShopName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HighVoltageCertified { get; set; }

    [ObservableProperty]
    public partial int UnitSystemIndex { get; set; }

    [ObservableProperty]
    public partial int PressureUnitIndex { get; set; }

    [ObservableProperty]
    public partial int TemperatureUnitIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowProbabilities { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool ReduceMotion { get; set; }

    [ObservableProperty]
    public partial double PollIntervalMs { get; set; }

    // ---------------------------------------------------------------- security

    [ObservableProperty]
    public partial bool RequireSignIn { get; set; }

    [ObservableProperty]
    public partial bool HasPassword { get; set; }

    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    public string DataFolder => paths.DataRoot;

    public string AppVersion { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public override Task OnNavigatedToAsync(object? parameter) => RunAsync(async ct =>
    {
        LoadFrom(settings.Current);
        await RefreshSecretStatusAsync(ct);
        HasPassword = await system.HasPasswordAsync(ct);
    });

    internal void LoadFrom(AppSettings s)
    {
        AiModeIndex = (int)s.Ai.Mode;
        OllamaBaseUrl = s.Ai.OllamaBaseUrl;
        OllamaChatModel = s.Ai.OllamaChatModel;
        OllamaVisionModel = s.Ai.OllamaVisionModel;
        OllamaEmbeddingModel = s.Ai.OllamaEmbeddingModel;
        CloudProviderIndex = (int)s.Ai.CloudProvider;
        AnthropicBaseUrl = s.Ai.AnthropicBaseUrl;
        AnthropicModel = s.Ai.AnthropicModel;
        AnthropicEffortIndex = Math.Max(0, AnthropicEffortLevels.ToList().IndexOf(s.Ai.AnthropicEffort.ToLowerInvariant()));
        OpenAiBaseUrl = s.Ai.OpenAiBaseUrl;
        OpenAiModel = s.Ai.OpenAiModel;
        OpenAiEmbeddingModel = s.Ai.OpenAiEmbeddingModel;
        UseCloudEmbeddings = s.Ai.UseCloudEmbeddings;
        Temperature = s.Ai.Temperature;

        AllowCloudAi = s.Privacy.AllowCloudAi;
        AllowDocumentsInCloud = s.Privacy.AllowDocumentsInCloud;
        AllowImagesInCloud = s.Privacy.AllowImagesInCloud;
        IncludeCustomerInfoInAi = s.Privacy.IncludeCustomerInfoInAi;

        SearchProviderIndex = (int)s.Search.Provider;
        SearxngBaseUrl = s.Search.SearxngBaseUrl;
        IncludeForums = s.Search.IncludeForums;

        TechnicianName = s.Diagnostics.TechnicianName;
        ShopName = s.Diagnostics.ShopName;
        HighVoltageCertified = s.Diagnostics.TechnicianHighVoltageCertified;
        UnitSystemIndex = (int)s.Diagnostics.Units;
        PressureUnitIndex = (int)s.Diagnostics.Pressure;
        TemperatureUnitIndex = (int)s.Diagnostics.Temperature;
        ShowProbabilities = s.Diagnostics.ShowProbabilities;
        ThemeIndex = (int)s.Appearance.Theme;
        ReduceMotion = s.Appearance.ReduceMotion;
        PollIntervalMs = s.LiveData.PollIntervalMs;
        RequireSignIn = s.Security.RequireSignIn;

        AnthropicApiKey = OpenAiApiKey = SearchApiKey = string.Empty;
    }

    private async Task RefreshSecretStatusAsync(CancellationToken ct)
    {
        var names = await secrets.ListNamesAsync(ct);
        AnthropicKeyStatus = names.Contains(SecretNames.AnthropicApiKey) ? "A key is saved (encrypted)" : "No key saved";
        OpenAiKeyStatus = names.Contains(SecretNames.OpenAiApiKey) ? "A key is saved (encrypted)" : "No key saved";
        var searchSecret = SearchSecretName;
        SearchKeyStatus = searchSecret is null ? string.Empty : names.Contains(searchSecret) ? "A key is saved (encrypted)" : "No key saved";
    }

    private string? SearchSecretName => SearchProviderIndex switch
    {
        1 => SecretNames.BraveApiKey,
        2 => SecretNames.TavilyApiKey,
        3 => SecretNames.SearxngApiKey,
        _ => null,
    };

    partial void OnSearchProviderIndexChanged(int value) => _ = RefreshSecretStatusSafeAsync();

    private async Task RefreshSecretStatusSafeAsync()
    {
        try
        {
            await RefreshSecretStatusAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            OnUnexpectedException(ex);
        }
    }

    private static bool IsHttpUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Returns the validation errors for the current form (empty when valid).</summary>
    internal IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (UsesLocal && !IsHttpUrl(OllamaBaseUrl)) errors.Add("Ollama URL must be an http(s) address, e.g. http://localhost:11434.");
        if (UsesCloud && IsAnthropic && !IsHttpUrl(AnthropicBaseUrl)) errors.Add("Anthropic base URL must be an http(s) address.");
        if (UsesCloud && IsAnthropic && string.IsNullOrWhiteSpace(AnthropicModel)) errors.Add("Enter the Claude model to use.");
        if (UsesCloud && IsOpenAi && !IsHttpUrl(OpenAiBaseUrl)) errors.Add("OpenAI-compatible base URL must be an http(s) address.");
        if (UsesCloud && IsOpenAi && string.IsNullOrWhiteSpace(OpenAiModel)) errors.Add("Enter the model to use.");
        if (IsSearxng && !IsHttpUrl(SearxngBaseUrl)) errors.Add("SearXNG URL must be an http(s) address.");
        if (double.IsNaN(Temperature) || Temperature is < 0 or > 2) errors.Add("Temperature must be between 0 and 2.");
        if (double.IsNaN(PollIntervalMs) || PollIntervalMs is < 50 or > 5000) errors.Add("Live-data poll interval must be between 50 and 5000 ms.");
        if (UsesCloud && !AllowCloudAi) errors.Add("Cloud or Hybrid mode needs \"Allow sending data to cloud AI\" turned on in Privacy.");
        return errors;
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async ct =>
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            ShowError(Error.Validation(string.Join(Environment.NewLine, errors)));
            return;
        }

        await settings.UpdateAsync(s =>
        {
            s.Ai.Mode = (AiMode)AiModeIndex;
            s.Ai.OllamaBaseUrl = OllamaBaseUrl.Trim().TrimEnd('/');
            s.Ai.OllamaChatModel = OllamaChatModel.Trim();
            s.Ai.OllamaVisionModel = OllamaVisionModel.Trim();
            s.Ai.OllamaEmbeddingModel = OllamaEmbeddingModel.Trim();
            s.Ai.CloudProvider = (CloudAiProvider)CloudProviderIndex;
            s.Ai.AnthropicBaseUrl = AnthropicBaseUrl.Trim().TrimEnd('/');
            s.Ai.AnthropicModel = AnthropicModel.Trim();
            s.Ai.AnthropicEffort = AnthropicEffortLevels[Math.Clamp(AnthropicEffortIndex, 0, AnthropicEffortLevels.Count - 1)];
            s.Ai.OpenAiBaseUrl = OpenAiBaseUrl.Trim().TrimEnd('/');
            s.Ai.OpenAiModel = OpenAiModel.Trim();
            s.Ai.OpenAiEmbeddingModel = OpenAiEmbeddingModel.Trim();
            s.Ai.UseCloudEmbeddings = UseCloudEmbeddings;
            s.Ai.Temperature = Math.Round(Temperature, 2);

            s.Privacy.AllowCloudAi = AllowCloudAi;
            s.Privacy.AllowDocumentsInCloud = AllowCloudAi && AllowDocumentsInCloud;
            s.Privacy.AllowImagesInCloud = AllowCloudAi && AllowImagesInCloud;
            s.Privacy.IncludeCustomerInfoInAi = AllowCloudAi && IncludeCustomerInfoInAi;

            s.Search.Provider = (WebSearchProviderKind)SearchProviderIndex;
            s.Search.SearxngBaseUrl = SearxngBaseUrl.Trim().TrimEnd('/');
            s.Search.IncludeForums = IncludeForums;

            s.Diagnostics.TechnicianName = TechnicianName.Trim();
            s.Diagnostics.ShopName = ShopName.Trim();
            s.Diagnostics.TechnicianHighVoltageCertified = HighVoltageCertified;
            s.Diagnostics.Units = (UnitSystem)UnitSystemIndex;
            s.Diagnostics.Pressure = (PressureUnit)PressureUnitIndex;
            s.Diagnostics.Temperature = (TemperatureUnit)TemperatureUnitIndex;
            s.Diagnostics.ShowProbabilities = ShowProbabilities;
            s.Appearance.Theme = (AppTheme)ThemeIndex;
            s.Appearance.ReduceMotion = ReduceMotion;
            s.LiveData.PollIntervalMs = (int)PollIntervalMs;
            s.Security.RequireSignIn = RequireSignIn && HasPassword;
        }, ct);

        if (!string.IsNullOrWhiteSpace(AnthropicApiKey)) await secrets.SetAsync(SecretNames.AnthropicApiKey, AnthropicApiKey.Trim(), ct);
        if (!string.IsNullOrWhiteSpace(OpenAiApiKey)) await secrets.SetAsync(SecretNames.OpenAiApiKey, OpenAiApiKey.Trim(), ct);
        if (!string.IsNullOrWhiteSpace(SearchApiKey) && SearchSecretName is { } searchSecret) await secrets.SetAsync(searchSecret, SearchApiKey.Trim(), ct);
        AnthropicApiKey = OpenAiApiKey = SearchApiKey = string.Empty;
        system.InvalidateAiRouting();
        await RefreshSecretStatusAsync(ct);
        if (RequireSignIn && !HasPassword)
        {
            RequireSignIn = false;
            ShowNotice("Saved", "Settings saved. Set a workstation password before requiring sign-in.", NoticeSeverity.Warning);
        }
        else
        {
            ShowSuccess("Settings saved.");
        }
    });

    [RelayCommand]
    private void Revert() => LoadFrom(settings.Current);

    [RelayCommand]
    private async Task ClearKeyAsync(string? which)
    {
        var name = which switch
        {
            "anthropic" => SecretNames.AnthropicApiKey,
            "openai" => SecretNames.OpenAiApiKey,
            "search" => SearchSecretName,
            _ => null,
        };
        if (name is null) return;
        var label = SecretNames.DisplayNames.GetValueOrDefault(name, name);
        if (!await dialogs.ConfirmAsync("Remove key?", $"Remove the saved {label}?", "Remove", "Cancel", destructive: true)) return;
        await RunAsync(async ct =>
        {
            await secrets.DeleteAsync(name, ct);
            system.InvalidateAiRouting();
            await RefreshSecretStatusAsync(ct);
        });
    }

    [RelayCommand]
    private Task TestAiAsync() => RunAsync(async ct =>
    {
        AiStatusLines.Clear();
        var status = await system.GetAiStatusAsync(refresh: true, ct);
        AiStatusLines.Add($"Mode: {status.Mode}");
        AiStatusLines.Add(status.LocalReachable ? $"Ollama reachable — chat: {status.LocalChatModel ?? "none"}, vision: {status.LocalVisionModel ?? "none"}" : "Ollama not reachable");
        AiStatusLines.Add(status.EmbeddingAvailable ? $"Embeddings: {status.EmbeddingModel}" : "Embeddings unavailable — keyword search only");
        AiStatusLines.Add(status.CloudConfigured ? $"Cloud: {status.CloudModel}" : "Cloud provider not configured");
        foreach (var note in status.Notes) AiStatusLines.Add(note);
        if (status.AnyChatAvailable) ShowSuccess("An AI model is available.");
        else ShowNotice("No AI model", "No chat model is available with the saved settings.", NoticeSeverity.Warning);
    }, "Checking AI providers…");

    [RelayCommand]
    private Task ListModelsAsync() => RunAsync(async ct =>
    {
        LocalModels.Clear();
        if (!await system.PingOllamaAsync(ct))
        {
            ShowError(new Error(ErrorKind.Unavailable, $"Ollama is not reachable at {OllamaBaseUrl}. Is it installed and running?"));
            return;
        }

        foreach (var m in await system.ListLocalModelsAsync(ct))
        {
            var caps = new List<string>();
            if (m.Capabilities.HasFlag(ModelCapabilities.Tools)) caps.Add("tools");
            if (m.Capabilities.HasFlag(ModelCapabilities.Vision)) caps.Add("vision");
            if (m.Capabilities.HasFlag(ModelCapabilities.Embeddings)) caps.Add("embeddings");
            LocalModels.Add(new ModelItem(m.Name, $"{m.ParameterSize ?? m.Family ?? string.Empty} · {Format.Size(m.SizeBytes)} · {Format.Join(caps, "chat")}"));
        }

        if (LocalModels.Count == 0) StatusMessage = "No models installed. Pull one below (for example qwen3:8b and nomic-embed-text).";
    });

    [RelayCommand]
    private Task PullModelAsync() => RunAsync(async ct =>
    {
        var model = PullModelName?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            ShowError(Error.Validation("Enter a model name, e.g. nomic-embed-text."));
            return;
        }

        var progress = new Progress<ModelPullProgress>(p => dispatcher.Run(() =>
            PullProgress = p.Total is > 0 && p.Completed is { } done
                ? string.Create(CultureInfo.CurrentCulture, $"{p.Status} {100.0 * done / p.Total.Value:0}%")
                : p.Status));
        await system.PullModelAsync(model, progress, ct);
        PullProgress = $"{model} installed.";
        system.InvalidateAiRouting();
        await ListModelsAsync();
    }, "Downloading model…");

    [RelayCommand]
    private Task SetPasswordAsync() => RunAsync(async ct =>
    {
        if (NewPassword != ConfirmPassword)
        {
            ShowError(Error.Validation("The new passwords do not match."));
            return;
        }

        var result = await system.SetPasswordAsync(HasPassword ? CurrentPassword : null, NewPassword, ct);
        CurrentPassword = NewPassword = ConfirmPassword = string.Empty;
        if (!Check(result)) return;
        HasPassword = true;
        ShowSuccess("Workstation password set.");
    });

    [RelayCommand]
    private Task RemovePasswordAsync() => RunAsync(async ct =>
    {
        var result = await system.RemovePasswordAsync(CurrentPassword, ct);
        CurrentPassword = string.Empty;
        if (!Check(result)) return;
        HasPassword = false;
        RequireSignIn = false;
        await settings.UpdateAsync(s => s.Security.RequireSignIn = false, ct);
        ShowSuccess("Workstation password removed.");
    });

    [RelayCommand]
    private Task InstallSampleDataAsync() => RunAsync(async ct =>
    {
        var result = await system.InstallSampleDataAsync(ct);
        if (!Check(result)) return;
        await settings.UpdateAsync(s => s.SampleDataInstalled = true, ct);
        ShowSuccess($"Installed {result.Value} sample records (labeled \"Sample\").");
    });

    [RelayCommand]
    private async Task RemoveSampleDataAsync()
    {
        if (!await dialogs.ConfirmAsync("Remove sample data?", "Remove all sample vehicles, sessions, and customers? Your own data is not affected.", "Remove",
                "Cancel", destructive: true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            if (!Check(await system.RemoveSampleDataAsync(ct))) return;
            await settings.UpdateAsync(s => s.SampleDataInstalled = false, ct);
            ShowSuccess("Sample data removed.");
        });
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => launcher.OpenFolderAsync(paths.DataRoot);

    [RelayCommand]
    private Task OpenLogsFolderAsync() => launcher.OpenFolderAsync(paths.LogsDirectory);
}
