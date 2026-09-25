using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Settings;
using MechanicAI.Infrastructure.Http;

namespace MechanicAI.Infrastructure.Web;

/// <summary>Brave Search API (https://api.search.brave.com). Requires a subscription token.</summary>
public sealed class BraveSearchProvider(HttpClient http, string apiKey) : IWebSearchProvider
{
    public WebSearchProviderKind Kind => WebSearchProviderKind.Brave;

    public string DisplayName => "Brave Search";

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(count, 1, 20)}&safesearch=moderate";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Subscription-Token", apiKey);
        using var response = await HttpCall.SendAsync(http, request, DisplayName, cancellationToken);
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static IReadOnlyList<WebSearchHit> Parse(string json)
    {
        using var doc = HttpCall.ParseDocument(json, "Brave Search");
        if (!doc.RootElement.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var results)) return [];
        var hits = new List<WebSearchHit>();
        var rank = 0;
        foreach (var r in results.EnumerateArray())
        {
            var title = Str(r, "title");
            var url = Str(r, "url");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            rank++;
            hits.Add(new WebSearchHit(
                StripTags(title),
                url,
                StripTags(Str(r, "description")),
                ParseDate(Str(r, "page_age")),
                Str(r, "age"),
                "Brave Search",
                1.0 / rank));
        }

        return hits;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;

    internal static string StripTags(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value, "<.*?>", string.Empty));
}

/// <summary>Tavily Search API (https://api.tavily.com). Requires an API key.</summary>
public sealed class TavilySearchProvider(HttpClient http, string apiKey) : IWebSearchProvider
{
    public WebSearchProviderKind Kind => WebSearchProviderKind.Tavily;

    public string DisplayName => "Tavily";

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(new { query, max_results = Math.Clamp(count, 1, 20), search_depth = "basic", include_answer = false }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await HttpCall.SendAsync(http, request, DisplayName, cancellationToken);
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    internal static IReadOnlyList<WebSearchHit> Parse(string json)
    {
        using var doc = HttpCall.ParseDocument(json, "Tavily");
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        var hits = new List<WebSearchHit>();
        foreach (var r in results.EnumerateArray())
        {
            var title = BraveSearchProvider.Str(r, "title");
            var url = BraveSearchProvider.Str(r, "url");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            double? score = r.TryGetProperty("score", out var s) && s.TryGetDouble(out var d) ? d : null;
            hits.Add(new WebSearchHit(title, url, BraveSearchProvider.Str(r, "content"),
                BraveSearchProvider.ParseDate(BraveSearchProvider.Str(r, "published_date")), null, "Tavily", score));
        }

        return hits;
    }
}

/// <summary>Self-hosted SearXNG metasearch (JSON output must be enabled on the instance).</summary>
public sealed class SearxngSearchProvider(HttpClient http, string baseUrl, string? token) : IWebSearchProvider
{
    public WebSearchProviderKind Kind => WebSearchProviderKind.SearXng;

    public string DisplayName => "SearXNG";

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json&categories=general&safesearch=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await HttpCall.SendAsync(http, request, DisplayName, cancellationToken);
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken)).Take(count).ToList();
    }

    internal static IReadOnlyList<WebSearchHit> Parse(string json)
    {
        using var doc = HttpCall.ParseDocument(json, "SearXNG");
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        var hits = new List<WebSearchHit>();
        var rank = 0;
        foreach (var r in results.EnumerateArray())
        {
            var title = BraveSearchProvider.Str(r, "title");
            var url = BraveSearchProvider.Str(r, "url");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
            rank++;
            double? score = r.TryGetProperty("score", out var s) && s.TryGetDouble(out var d) ? d : 1.0 / rank;
            hits.Add(new WebSearchHit(title, url, BraveSearchProvider.Str(r, "content"),
                BraveSearchProvider.ParseDate(BraveSearchProvider.Str(r, "publishedDate")), null, "SearXNG", score));
        }

        return hits;
    }
}

public sealed class WebSearchProviderFactory(IHttpClientFactory httpFactory, ISettingsStore settings, ISecretStore secrets) : IWebSearchProviderFactory
{
    public const string HttpClientName = "web-search";

    public async Task<(IWebSearchProvider? Provider, string? UnavailableReason)> GetActiveAsync(CancellationToken cancellationToken)
    {
        var s = settings.Current.Search;
        var http = httpFactory.CreateClient(HttpClientName);
        switch (s.Provider)
        {
            case WebSearchProviderKind.Brave:
            {
                var key = await secrets.GetAsync(SecretNames.BraveApiKey, cancellationToken);
                return string.IsNullOrWhiteSpace(key)
                    ? (null, "Brave Search is selected but no API key is saved (Settings → Security → Credentials).")
                    : (new BraveSearchProvider(http, key), null);
            }

            case WebSearchProviderKind.Tavily:
            {
                var key = await secrets.GetAsync(SecretNames.TavilyApiKey, cancellationToken);
                return string.IsNullOrWhiteSpace(key)
                    ? (null, "Tavily is selected but no API key is saved (Settings → Security → Credentials).")
                    : (new TavilySearchProvider(http, key), null);
            }

            case WebSearchProviderKind.SearXng:
            {
                if (!Uri.TryCreate(s.SearxngBaseUrl, UriKind.Absolute, out _)) return (null, "The SearXNG address in Settings → Search is not a valid URL.");
                var token = await secrets.GetAsync(SecretNames.SearxngApiKey, cancellationToken);
                return (new SearxngSearchProvider(http, s.SearxngBaseUrl, token), null);
            }

            default:
                return (null, "Web search is not configured. Choose a provider (Brave, Tavily, or a SearXNG server) in Settings → Search.");
        }
    }
}
