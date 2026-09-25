using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Ai.Tools;

public sealed class SearchWebTool(ResearchService research, IWebPageFetcher fetcher, ISettingsStore settings) : IAiTool
{
    public string Name => "search_web";

    public string Description => "Search the web for automotive information (diagnostic procedures, known problems, TSB discussions, component locations). Returns classified sources (manufacturer, government, professional, publication, community, forum, social) with excerpts. Weigh authority accordingly.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search query; include year/make/model/engine when relevant" },
            max_results = new { type = "integer", description = "1-8, default 5" },
            read_pages = new { type = "boolean", description = "Fetch and read the top pages (slower, more accurate). Default true." },
        },
        required = new[] { "query" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = ToolArgs.String(arguments, "query") ?? string.Empty;
        var max = Math.Clamp(ToolArgs.Int(arguments, "max_results") ?? 5, 1, 8);
        var result = await research.SearchAsync(query, null, max, cancellationToken);
        if (result.IsFailure) return ToolExecutionResult.Fail(result.Error!.Message);
        var results = result.Value!;
        var readPages = (ToolArgs.Bool(arguments, "read_pages") ?? true) && settings.Current.Search.FetchPages && !results.FromCache;

        var items = new List<object>();
        foreach (var (source, index) in results.Sources.Take(max).Select((s, i) => (s, i)))
        {
            string? excerpt = source.Snippet;
            if (readPages && index < 3)
            {
                try
                {
                    var page = await fetcher.FetchAsync(source.Url, cancellationToken);
                    if (page.Text.Length > 200) excerpt = Text.Truncate(page.Text, 2500);
                }
                catch (Common.ExternalServiceException)
                {
                    // Keep the search snippet; the page could not be read.
                }
            }

            var citation = context.Sources.Register(SourceRegistry.Web(source.Title, source.Url, source.Type, source.Publisher, source.PublishedUtc,
                Text.Truncate(excerpt, 400), source.FromCache, source.RetrievedUtc));
            items.Add(new
            {
                source = citation.Label,
                title = source.Title,
                site = source.Domain,
                authority = source.TypeLabel,
                published = source.PublishedUtc?.ToString("yyyy-MM-dd") ?? source.AgeText,
                excerpt,
            });
        }

        return ToolExecutionResult.Ok(new
        {
            query = results.EffectiveQuery,
            notice = results.Notice,
            fromCache = results.FromCache,
            retrieved = results.RetrievedUtc.ToString("yyyy-MM-dd HH:mm") + " UTC",
            results = items,
            guidance = "Cite results by their source label. Forums and social media are anecdotal; manufacturer and government sources are authoritative.",
        }, $"{items.Count} web result(s){(results.FromCache ? " (cached)" : string.Empty)}");
    }
}

public sealed class SearchDocumentsTool(KnowledgeBaseService knowledgeBase, VehicleService vehicles) : IAiTool
{
    public string Name => "search_documents";

    public string Description => "Search the technician's uploaded service manuals, bulletins, wiring diagrams, and notes. Returns passages with document title and page. Specifications must be quoted exactly from these passages.";

    public bool ReturnsPrivateData => true;

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string" },
            document_type = new { type = "string", @enum = new[] { "any", "ServiceManual", "RepairManual", "WiringDiagram", "TechnicalBulletin", "Notes", "TrainingMaterial", "DiagnosticDocument" } },
            this_vehicle_only = new { type = "boolean", description = "Limit to documents applicable to the active vehicle" },
        },
        required = new[] { "query" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = ToolArgs.String(arguments, "query") ?? string.Empty;
        var type = ToolArgs.String(arguments, "document_type");
        DocumentKind? kind = Enum.TryParse<DocumentKind>(type, out var k) ? k : null;
        var options = new KnowledgeSearchOptions { Limit = 6, Kind = kind };
        if (ToolArgs.Bool(arguments, "this_vehicle_only") == true && context.VehicleId is { } vid && await vehicles.GetAsync(vid, cancellationToken) is { } vehicle)
        {
            options = options with { VehicleYear = vehicle.Year, VehicleMake = vehicle.Make, VehicleModel = vehicle.Model };
        }

        var hits = await knowledgeBase.SearchAsync(query, options, cancellationToken);
        if (hits.Count == 0)
        {
            return ToolExecutionResult.Ok(new { count = 0, message = "No matching passages in the technician's documents." }, "No document matches");
        }

        var passages = hits.Select(h =>
        {
            var citation = context.Sources.Register(new SourceCitation
            {
                Title = $"{h.DocumentTitle}, page {h.PageNumber}",
                Type = SourceType.PrivateDocument,
                DocumentId = h.DocumentId,
                PageNumber = h.PageNumber,
                ChunkId = h.ChunkId,
                Excerpt = Text.Truncate(h.Text, 300),
            });
            return new { source = citation.Label, document = h.DocumentTitle, page = h.PageNumber, heading = h.Heading, text = Text.Truncate(h.Text, 1800) };
        }).ToList();
        return ToolExecutionResult.Ok(new { count = passages.Count, passages }, $"{passages.Count} passage(s) from your documents");
    }
}

public sealed class SearchKnowledgeBaseTool(HistoryService history, KnowledgeBaseService knowledgeBase) : IAiTool
{
    public string Name => "search_knowledge_base";

    public string Description => "Search the shop's own knowledge: previously confirmed diagnoses on any vehicle (what fixed similar complaints before) plus technician notes and documents.";

    public bool ReturnsPrivateData => true;

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { query = new { type = "string" } },
        required = new[] { "query" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = ToolArgs.String(arguments, "query") ?? string.Empty;
        var diagnoses = await history.SearchConfirmedDiagnosesAsync(query, 6, cancellationToken);
        var notes = await knowledgeBase.SearchAsync(query, new KnowledgeSearchOptions { Limit = 4, Kind = DocumentKind.Notes }, cancellationToken);
        var confirmed = diagnoses.Select(d =>
        {
            var citation = context.Sources.Register(new SourceCitation
            {
                Title = $"Shop record: {d.Vehicle} — {d.Diagnosis}",
                Type = SourceType.PrivateDocument,
                Publisher = "This shop's diagnostic history",
                PublishedUtc = d.WhenUtc,
            });
            return new { source = citation.Label, vehicle = d.Vehicle, codes = d.Codes, complaint = Text.Truncate(d.Complaint, 200), confirmedDiagnosis = d.Diagnosis, date = d.WhenUtc.ToString("yyyy-MM-dd") };
        }).ToList();
        var notePassages = notes.Select(h =>
        {
            var citation = context.Sources.Register(new SourceCitation
            {
                Title = $"{h.DocumentTitle}, page {h.PageNumber}",
                Type = SourceType.PrivateDocument,
                DocumentId = h.DocumentId,
                PageNumber = h.PageNumber,
                ChunkId = h.ChunkId,
            });
            return new { source = citation.Label, document = h.DocumentTitle, text = Text.Truncate(h.Text, 1000) };
        }).ToList();
        return ToolExecutionResult.Ok(new
        {
            note = "Past diagnoses are evidence of what has happened in this shop, not proof of the current fault.",
            confirmedDiagnoses = confirmed,
            notes = notePassages,
        }, $"{confirmed.Count} past diagnosis(es), {notePassages.Count} note(s)");
    }
}

public sealed class SearchWiringTool(WiringService wiring) : IAiTool
{
    public string Name => "search_wiring";

    public string Description => "Answer a question from the technician's uploaded wiring diagrams (power feeds, grounds, connectors, splices, test points). Only identifiers read from the diagram are reported.";

    public bool ReturnsPrivateData => true;

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { question = new { type = "string" } },
        required = new[] { "question" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var question = ToolArgs.String(arguments, "question") ?? string.Empty;
        var result = await wiring.AskAsync(question, null, null, null, cancellationToken);
        if (result.IsFailure) return ToolExecutionResult.Fail(result.Error!.Message);
        var answer = result.Value!;
        if (answer.DocumentId is null) return ToolExecutionResult.Ok(new { found = false, message = answer.Markdown }, "No matching diagram");
        var citation = context.Sources.Register(new SourceCitation
        {
            Title = $"Wiring diagram page {answer.PageNumber}",
            Type = SourceType.PrivateDocument,
            DocumentId = answer.DocumentId,
            PageNumber = answer.PageNumber,
        });
        return ToolExecutionResult.Ok(new
        {
            source = citation.Label,
            page = answer.PageNumber,
            answer = answer.Markdown,
            identifiersOnPage = answer.AllLabels.GroupBy(l => l.Kind.ToString()).ToDictionary(g => g.Key, g => g.Select(l => l.Text).Distinct().Take(30)),
            warnings = answer.Warnings,
        }, $"Wiring answer from page {answer.PageNumber}");
    }
}

public sealed class AnalyzeImageTool(ImageAnalysisService images) : IAiTool
{
    public string Name => "analyze_image";

    public string Description => "Analyze a photo the technician attached to this message (component identification, visible condition). Returns a structured result with a confidence level.";

    public bool ReturnsPrivateData => true;

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new
        {
            image_index = new { type = "integer", description = "0-based index of the attached image" },
            question = new { type = "string" },
        },
        required = new[] { "image_index" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var index = ToolArgs.Int(arguments, "image_index") ?? 0;
        if (index < 0 || index >= context.Images.Count) return ToolExecutionResult.Fail("No image is attached at that index.");
        var image = context.Images[index];
        var result = await images.AnalyzeAsync(image.Data, image.MediaType, ToolArgs.String(arguments, "question"), null, cancellationToken);
        if (result.IsFailure) return ToolExecutionResult.Fail(result.Error!.Message);
        var r = result.Value!;
        return ToolExecutionResult.Ok(new
        {
            evidence = "AI INFERENCE from the photo — must be verified",
            r.LikelyComponent,
            r.Confidence,
            r.VisualEvidence,
            r.AlternativeIdentifications,
            r.ObservedConditions,
            r.RecommendedVerification,
            r.SafetyNotes,
            r.Limitations,
            r.RawText,
        }, $"{r.LikelyComponent} ({r.Confidence} confidence)");
    }
}

public sealed class AnalyzeLiveDataTool(LiveDataService liveData) : IAiTool
{
    public string Name => "analyze_live_data";

    public string Description => "Get statistics and computed observations for a recorded live-data capture (latest for the active vehicle if no id is given).";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { recording_id = new { type = "string" } },
        required = Array.Empty<string>(),
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var id = ToolArgs.Guid(arguments, "recording_id");
        if (id is null)
        {
            var recordings = await liveData.ListRecordingsAsync(context.VehicleId, cancellationToken);
            id = recordings.FirstOrDefault()?.Id;
        }

        if (id is null) return ToolExecutionResult.Fail("No live-data recordings exist for this vehicle.");
        var data = await liveData.LoadRecordingAsync(id.Value, cancellationToken);
        if (data is null) return ToolExecutionResult.Fail("Recording not found.");
        var stats = LiveDataService.ComputeStatistics(data);
        var citation = context.Sources.Register(new SourceCitation
        {
            Title = $"Live data recording: {data.Session.Title}{(data.Session.IsSimulated ? " (SIMULATOR)" : string.Empty)}",
            Type = SourceType.PrivateDocument,
            Publisher = data.Session.AdapterDescription,
            RetrievedUtc = data.Session.StartedUtc,
        });
        return ToolExecutionResult.Ok(new
        {
            source = citation.Label,
            simulated = data.Session.IsSimulated,
            durationSeconds = Math.Round(stats.Duration.TotalSeconds),
            pids = stats.Pids.Select(p => new { p.Key, p.Name, p.Unit, p.Min, p.Max, p.Mean, p.StdDev, p.Samples }),
            observations = stats.Observations,
            guidance = "Do not call a value abnormal without vehicle-specific context; label generic rules of thumb as general guidelines.",
        }, $"{stats.Pids.Count} PIDs, {stats.Duration.TotalSeconds:0} s");
    }
}
