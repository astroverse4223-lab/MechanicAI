using System.Globalization;
using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using MechanicAI.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Vehicles;

/// <summary>
/// NHTSA recalls and owner-complaints APIs. NHTSA's model naming ("SILVERADO 1500") differs
/// from everyday naming, so the model is resolved against NHTSA's own model list for the
/// year/make before querying. Results are cached for offline use.
/// </summary>
public sealed class NhtsaSafetyClient(
    IHttpClientFactory httpFactory,
    IResponseCache cache,
    ISettingsStore settings,
    ILogger<NhtsaSafetyClient> logger) : IRecallProvider, IComplaintProvider
{
    private const string Service = "NHTSA";

    public async Task<Sourced<IReadOnlyList<RecallRecord>>> GetRecallsAsync(string make, string model, int modelYear, CancellationToken ct)
    {
        var baseUrl = settings.Current.VehicleData.NhtsaApiBaseUrl.TrimEnd('/');
        var models = await ResolveModelsAsync(make, model, modelYear, "r", ct);
        var records = new Dictionary<string, RecallRecord>(StringComparer.OrdinalIgnoreCase);
        DateTime retrieved = DateTime.UtcNow;
        var fromCache = false;
        string? firstUrl = null;

        foreach (var nhtsaModel in models)
        {
            var url = $"{baseUrl}/recalls/recallsByVehicle?make={Uri.EscapeDataString(make)}&model={Uri.EscapeDataString(nhtsaModel)}&modelYear={modelYear}";
            firstUrl ??= url;
            var (json, when, cached) = await GetWithCacheAsync(url, $"nhtsa:recalls:{make}:{nhtsaModel}:{modelYear}".ToLowerInvariant(),
                TimeSpan.FromHours(24), ct);
            retrieved = when;
            fromCache |= cached;
            foreach (var record in ParseRecalls(json, url))
            {
                records.TryAdd(record.CampaignNumber, record);
            }
        }

        return new Sourced<IReadOnlyList<RecallRecord>>(records.Values.OrderByDescending(r => r.ReportReceivedDate).ToList(),
            "NHTSA Recalls API", firstUrl, retrieved, fromCache);
    }

    public async Task<Sourced<IReadOnlyList<ComplaintRecord>>> GetComplaintsAsync(string make, string model, int modelYear, CancellationToken ct)
    {
        var baseUrl = settings.Current.VehicleData.NhtsaApiBaseUrl.TrimEnd('/');
        var models = await ResolveModelsAsync(make, model, modelYear, "c", ct);
        var complaints = new Dictionary<long, ComplaintRecord>();
        DateTime retrieved = DateTime.UtcNow;
        var fromCache = false;
        string? firstUrl = null;
        foreach (var nhtsaModel in models)
        {
            var url = $"{baseUrl}/complaints/complaintsByVehicle?make={Uri.EscapeDataString(make)}&model={Uri.EscapeDataString(nhtsaModel)}&modelYear={modelYear}";
            firstUrl ??= url;
            var (json, when, cached) = await GetWithCacheAsync(url, $"nhtsa:complaints:{make}:{nhtsaModel}:{modelYear}".ToLowerInvariant(),
                TimeSpan.FromHours(24), ct);
            retrieved = when;
            fromCache |= cached;
            foreach (var c in ParseComplaints(json)) complaints.TryAdd(c.OdiNumber, c);
        }

        return new Sourced<IReadOnlyList<ComplaintRecord>>(complaints.Values.OrderByDescending(c => c.FiledDate).ToList(),
            "NHTSA Complaints API (owner-reported, unverified)", firstUrl, retrieved, fromCache);
    }

    /// <summary>Maps an everyday model name to NHTSA's names for that year/make ("Silverado" → "SILVERADO 1500", ...).</summary>
    private async Task<IReadOnlyList<string>> ResolveModelsAsync(string make, string model, int year, string issueType, CancellationToken ct)
    {
        var baseUrl = settings.Current.VehicleData.NhtsaApiBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/products/vehicle/models?modelYear={year}&make={Uri.EscapeDataString(make)}&issueType={issueType}";
        IReadOnlyList<string> available;
        try
        {
            var (json, _, _) = await GetWithCacheAsync(url, $"nhtsa:models:{make}:{year}:{issueType}".ToLowerInvariant(), TimeSpan.FromDays(7), ct);
            using var doc = HttpCall.ParseDocument(json, Service);
            available = doc.RootElement.TryGetProperty("results", out var results)
                ? results.EnumerateArray()
                    .Select(r => r.TryGetProperty("model", out var m) ? m.GetString() : null)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (ExternalServiceException ex) when (ex.Kind is not ErrorKind.Offline)
        {
            logger.LogDebug(ex, "NHTSA model list unavailable; querying with the model name as entered");
            return [model];
        }

        return MatchModels(model, available);
    }

    internal static IReadOnlyList<string> MatchModels(string model, IReadOnlyList<string> available)
    {
        static string Norm(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var target = Norm(model);
        if (target.Length == 0 || available.Count == 0) return [model];

        var exact = available.Where(a => Norm(a) == target).ToList();
        if (exact.Count > 0) return exact;

        var prefixed = available.Where(a => Norm(a).StartsWith(target, StringComparison.Ordinal)).Take(6).ToList();
        if (prefixed.Count > 0) return prefixed;

        var firstWord = model.Split(' ', '-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord is not null && firstWord.Length >= 3)
        {
            var byWord = available.Where(a => a.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            if (byWord.Count > 0) return byWord;
        }

        return [model];
    }

    private async Task<(string Json, DateTime RetrievedUtc, bool FromCache)> GetWithCacheAsync(string url, string key, TimeSpan ttl, CancellationToken ct)
    {
        var cached = await cache.GetAsync(key, ct);
        if (cached is not null && cached.IsFresh(DateTime.UtcNow)) return (cached.Payload, cached.RetrievedUtc, true);
        try
        {
            var json = await HttpCall.GetStringAsync(httpFactory.CreateClient(NhtsaVpicClient.HttpClientName), url, Service, ct);
            await cache.SetAsync(key, "nhtsa", json, ttl, ct);
            return (json, DateTime.UtcNow, false);
        }
        catch (ExternalServiceException) when (cached is not null)
        {
            return (cached.Payload, cached.RetrievedUtc, true);
        }
    }

    internal static IReadOnlyList<RecallRecord> ParseRecalls(string json, string sourceUrl)
    {
        using var doc = HttpCall.ParseDocument(json, Service);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var list = new List<RecallRecord>();
        foreach (var r in results.EnumerateArray())
        {
            var campaign = Str(r, "NHTSACampaignNumber");
            if (string.IsNullOrWhiteSpace(campaign)) continue;
            list.Add(new RecallRecord(
                campaign,
                Str(r, "Manufacturer"),
                Str(r, "Component"),
                Str(r, "Summary"),
                Str(r, "Consequence"),
                Str(r, "Remedy"),
                Str(r, "Notes"),
                ParseDate(Str(r, "ReportReceivedDate"), "dd/MM/yyyy"),
                Bool(r, "parkIt"),
                Bool(r, "parkOutSide"),
                Bool(r, "overTheAirUpdate"),
                sourceUrl));
        }

        return list;
    }

    internal static IReadOnlyList<ComplaintRecord> ParseComplaints(string json)
    {
        using var doc = HttpCall.ParseDocument(json, Service);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var list = new List<ComplaintRecord>();
        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("odiNumber", out var odi) || !odi.TryGetInt64(out var number)) continue;
            list.Add(new ComplaintRecord(
                number,
                Str(r, "components") ?? string.Empty,
                Str(r, "summary") ?? string.Empty,
                ParseDate(Str(r, "dateOfIncident"), "MM/dd/yyyy"),
                ParseDate(Str(r, "dateComplaintFiled"), "MM/dd/yyyy"),
                Bool(r, "crash"),
                Bool(r, "fire"),
                Int(r, "numberOfInjuries"),
                Int(r, "numberOfDeaths"),
                Str(r, "vin")));
        }

        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static DateTime? ParseDate(string? value, string format) =>
        DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
}
