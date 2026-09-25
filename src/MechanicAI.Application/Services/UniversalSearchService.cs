using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Search;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public enum SearchActionKind
{
    StartDiagnosis, OpenDtc, DecodeVin, OpenVehicle, OpenSession, OpenDocument, OpenLesson, SearchWeb, AskAssistant, OpenWiring, CreateVehicle,
}

public sealed record SearchAction(SearchActionKind Kind, string Label, string? Argument);

public sealed record UniversalSearchItem(
    SearchIntent Group,
    string Title,
    string? Subtitle,
    string? Detail,
    SearchAction PrimaryAction,
    double Score,
    string? Badge = null);

public sealed record UniversalSearchResults(
    ParsedQuery Parsed,
    IReadOnlyList<SearchAction> SuggestedActions,
    IReadOnlyList<UniversalSearchItem> Items,
    ResearchResults? Web,
    string? WebNotice);

/// <summary>
/// One search box for everything. The query is parsed for intent, then every relevant local
/// source is searched in parallel; the web is searched when the intent calls for it.
/// </summary>
public sealed class UniversalSearchService(
    DtcService dtcs,
    VehicleService vehicles,
    KnowledgeBaseService knowledgeBase,
    ResearchService research,
    SearchHistoryService history,
    IAppDbContextFactory dbFactory,
    ILogger<UniversalSearchService> logger,
    IConnectivityMonitor? connectivity = null)
{
    public async Task<UniversalSearchResults> SearchAsync(string query, bool includeWeb, CancellationToken ct = default)
    {
        var parsed = QueryParser.Parse(query);
        var actions = SuggestActions(parsed);
        var items = new List<UniversalSearchItem>();
        var intentScore = parsed.Intents.ToDictionary(i => i.Intent, i => i.Score);
        double Weight(SearchIntent intent) => 1 + intentScore.GetValueOrDefault(intent);

        var dtcTask = Safe(async () =>
        {
            if (parsed.Dtcs.Count == 0 && intentScore.GetValueOrDefault(SearchIntent.Dtc) < 0.1 && parsed.Symptoms.Count == 0) return [];
            var results = await dtcs.SearchAsync(query, 8, ct);
            return results.Select((r, i) => new UniversalSearchItem(SearchIntent.Dtc, $"{r.Code} — {r.Description}", r.Subsystem,
                r.IsGeneric ? "Generic (SAE)" : "Manufacturer-specific", new SearchAction(SearchActionKind.OpenDtc, "Open code", r.Code),
                Weight(SearchIntent.Dtc) * (1 - i * 0.05), r.HasPlaybook ? "Playbook" : null)).ToList();
        });

        var vehicleTask = Safe(async () =>
        {
            if (!parsed.HasVehicle && parsed.Vin is null && intentScore.GetValueOrDefault(SearchIntent.Vehicle) < 0.1) return [];
            var term = parsed.Vin ?? parsed.Model ?? parsed.Make ?? query;
            var list = await vehicles.ListAsync(term, 8, ct);
            if (parsed.Year is { } y) list = list.Where(v => v.DisplayName.StartsWith(y.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList();
            return list.Select((v, i) => new UniversalSearchItem(SearchIntent.Vehicle, v.DisplayName, v.Engine, v.CustomerName ?? v.Vin,
                new SearchAction(SearchActionKind.OpenVehicle, "Open vehicle", v.Id.ToString()), Weight(SearchIntent.Vehicle) * (1 - i * 0.05),
                v.IsSample ? "Sample" : null)).ToList();
        });

        var sessionTask = Safe(async () =>
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var codes = parsed.Dtcs.ToList();
            var pattern = "%" + (string.IsNullOrWhiteSpace(parsed.Remainder) ? query : parsed.Remainder).Trim() + "%";
            var sessions = await db.DiagnosticSessions.AsNoTracking()
                .Include(s => s.Dtcs)
                .Where(s => s.Dtcs.Any(d => codes.Contains(d.Code)) || EF.Functions.Like(s.Title, pattern) || EF.Functions.Like(s.Complaint, pattern) ||
                            (s.FinalDiagnosis != null && EF.Functions.Like(s.FinalDiagnosis, pattern)))
                .OrderByDescending(s => s.UpdatedUtc)
                .Take(6)
                .ToListAsync(ct);
            return sessions.Select((s, i) => new UniversalSearchItem(SearchIntent.Diagnostic, s.Title, s.VehicleDescription,
                s.FinalDiagnosis is null ? s.Status.ToString() : $"Diagnosed: {s.FinalDiagnosis}", new SearchAction(SearchActionKind.OpenSession, "Open session", s.Id.ToString()),
                Weight(SearchIntent.Diagnostic) * (0.9 - i * 0.05), s.IsSample ? "Sample" : null)).ToList();
        });

        var documentTask = Safe(async () =>
        {
            var kind = intentScore.GetValueOrDefault(SearchIntent.Wiring) >= 0.3 ? DocumentKind.WiringDiagram : (DocumentKind?)null;
            var hits = await knowledgeBase.SearchAsync(query, new KnowledgeSearchOptions { Limit = 6, Kind = kind }, ct);
            return hits.Select((h, i) => new UniversalSearchItem(h.Kind == DocumentKind.WiringDiagram ? SearchIntent.Wiring : SearchIntent.Document,
                $"{h.DocumentTitle} — page {h.PageNumber}", h.Heading, Common.Text.Truncate(h.Text, 220),
                new SearchAction(h.Kind == DocumentKind.WiringDiagram ? SearchActionKind.OpenWiring : SearchActionKind.OpenDocument, "Open page",
                    $"{h.DocumentId}|{h.PageNumber}|{h.ChunkId}"),
                Weight(h.Kind == DocumentKind.WiringDiagram ? SearchIntent.Wiring : SearchIntent.Document) * (0.95 - i * 0.05), h.MatchType)).ToList();
        });

        var trainingTask = Safe(async () =>
        {
            if (intentScore.GetValueOrDefault(SearchIntent.Training) < 0.2 && parsed.Components.Count == 0) return [];
            var terms = Common.Text.Tokenize(parsed.Remainder).Where(t => t.Length > 3).Take(3).ToList();
            if (terms.Count == 0) return [];
            await using var db = await dbFactory.CreateAsync(ct);
            var lessons = db.TrainingLessons.AsNoTracking();
            foreach (var term in terms)
            {
                var p = "%" + term + "%";
                lessons = lessons.Where(l => EF.Functions.Like(l.Title, p) || EF.Functions.Like(l.BodyMarkdown, p));
            }

            var found = await lessons.Take(5).ToListAsync(ct);
            return found.Select((l, i) => new UniversalSearchItem(SearchIntent.Training, l.Title, $"{l.EstimatedMinutes} min lesson", null,
                new SearchAction(SearchActionKind.OpenLesson, "Open lesson", l.Id.ToString()), Weight(SearchIntent.Training) * (0.8 - i * 0.05))).ToList();
        });

        await Task.WhenAll(dtcTask, vehicleTask, sessionTask, documentTask, trainingTask);
        items.AddRange(dtcTask.Result);
        items.AddRange(vehicleTask.Result);
        items.AddRange(sessionTask.Result);
        items.AddRange(documentTask.Result);
        items.AddRange(trainingTask.Result);

        ResearchResults? web = null;
        string? webNotice = null;
        var webWanted = includeWeb || intentScore.GetValueOrDefault(SearchIntent.Web) >= 0.3 || intentScore.GetValueOrDefault(SearchIntent.Repair) >= 0.3 ||
                        intentScore.GetValueOrDefault(SearchIntent.Component) >= 0.3;
        if (webWanted)
        {
            if (connectivity is { IsOnline: false })
            {
                webNotice = "Offline — web results unavailable.";
            }
            else
            {
                var result = await research.SearchAsync(query, null, 8, ct);
                if (result.IsSuccess) web = result.Value;
                else webNotice = result.Error!.Message;
            }
        }

        var primary = parsed.PrimaryIntent;
        await history.RecordAsync(query, primary, items.Count + (web?.Sources.Count ?? 0), ct);
        return new UniversalSearchResults(parsed, actions, items.OrderByDescending(i => i.Score).ToList(), web, webNotice);
    }

    public static IReadOnlyList<SearchAction> SuggestActions(ParsedQuery parsed)
    {
        var actions = new List<SearchAction>();
        if (parsed.Vin is not null) actions.Add(new SearchAction(SearchActionKind.DecodeVin, $"Decode VIN {parsed.Vin}", parsed.Vin));
        if (parsed.LooksLikeDiagnosis)
        {
            var label = parsed.HasVehicle ? $"Start diagnosis: {parsed.VehicleDescription}" : "Start a diagnostic session";
            if (parsed.Dtcs.Count > 0) label += $" ({string.Join(", ", parsed.Dtcs)})";
            actions.Add(new SearchAction(SearchActionKind.StartDiagnosis, label, parsed.Original));
        }

        foreach (var code in parsed.Dtcs.Take(3)) actions.Add(new SearchAction(SearchActionKind.OpenDtc, $"Open {code}", code));
        if (parsed.HasVehicle && parsed.Vin is null && !parsed.LooksLikeDiagnosis)
        {
            actions.Add(new SearchAction(SearchActionKind.CreateVehicle, $"Add vehicle: {parsed.VehicleDescription}", parsed.Original));
        }

        actions.Add(new SearchAction(SearchActionKind.AskAssistant, "Ask the AI assistant", parsed.Original));
        actions.Add(new SearchAction(SearchActionKind.SearchWeb, "Search the web", parsed.Original));
        return actions;
    }

    private async Task<List<UniversalSearchItem>> Safe(Func<Task<List<UniversalSearchItem>>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A universal search source failed");
            return [];
        }
    }
}
