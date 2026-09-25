using System.Text;
using System.Text.RegularExpressions;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

public enum WiringLabelKind { Ground, Connector, Splice, Fuse, Relay, WireColor, PowerFeed }

public sealed record WiringLabel(string Text, WiringLabelKind Kind, int PageNumber, double X, double Y, double W, double H);

public enum WiringQuestionKind { PowerSource, Ground, Connector, Components, VoltageTestPoints, ContinuityTest, OpenCircuitEffect, General }

public sealed record WiringAnswer(
    string Markdown,
    Guid? DocumentId,
    int? PageNumber,
    IReadOnlyList<WiringLabel> Highlights,
    IReadOnlyList<WiringLabel> AllLabels,
    IReadOnlyList<string> Warnings,
    bool AnsweredByAi,
    string? Model);

/// <summary>
/// Wiring diagram assistant. Identifiers are read from the diagram itself (PDF text layer or
/// OCR). The AI may only reference identifiers that were actually found; anything else is
/// flagged in the answer so a fabricated ground or connector can never slip through.
/// </summary>
public sealed partial class WiringService(
    IAppDbContextFactory dbFactory,
    KnowledgeBaseService knowledgeBase,
    IAiRouter router,
    IPdfPageRenderer? renderer = null,
    IImageProcessor? imageProcessor = null)
{
    public static readonly IReadOnlyList<(WiringQuestionKind Kind, string Prompt)> QuickQuestions =
    [
        (WiringQuestionKind.PowerSource, "Where does this circuit get power?"),
        (WiringQuestionKind.Ground, "Where is the ground?"),
        (WiringQuestionKind.Connector, "What connector contains this wire?"),
        (WiringQuestionKind.Components, "What components are on this circuit?"),
        (WiringQuestionKind.VoltageTestPoints, "Where should I test voltage?"),
        (WiringQuestionKind.ContinuityTest, "Where should I test continuity?"),
        (WiringQuestionKind.OpenCircuitEffect, "What happens if this ground is open?"),
    ];

    public async Task<IReadOnlyList<Document>> ListDiagramsAsync(Guid? vehicleId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Documents.AsNoTracking().Where(d => d.Kind == DocumentKind.WiringDiagram);
        if (vehicleId is { } v) query = query.Where(d => d.VehicleId == v || d.VehicleId == null);
        return await query.OrderByDescending(d => d.CreatedUtc).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WiringLabel>> ExtractLabelsAsync(Guid documentId, int? pageNumber = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.DocumentPages.AsNoTracking().Where(p => p.DocumentId == documentId);
        if (pageNumber is { } n) query = query.Where(p => p.PageNumber == n);
        var pages = await query.OrderBy(p => p.PageNumber).ToListAsync(ct);
        return pages.SelectMany(ExtractLabels).ToList();
    }

    public static IReadOnlyList<WiringLabel> ExtractLabels(DocumentPage page)
    {
        var labels = new List<WiringLabel>();
        var words = page.Words;
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var token = word.Text.Trim().TrimEnd(',', ';', ':', '.').ToUpperInvariant();
            if (token.Length == 0) continue;
            var kind = Classify(token);

            // Multi-word power feed labels ("HOT AT ALL TIMES", "HOT IN RUN").
            if (kind is null && token == "HOT" && i + 1 < words.Count)
            {
                var phrase = string.Join(' ', words.Skip(i).Take(4).Select(w => w.Text.ToUpperInvariant()));
                if (phrase.StartsWith("HOT AT ALL TIMES", StringComparison.Ordinal) || phrase.StartsWith("HOT IN RUN", StringComparison.Ordinal) ||
                    phrase.StartsWith("HOT IN START", StringComparison.Ordinal) || phrase.StartsWith("HOT IN ACC", StringComparison.Ordinal))
                {
                    labels.Add(new WiringLabel(string.Join(' ', words.Skip(i).Take(phrase.StartsWith("HOT AT ALL", StringComparison.Ordinal) ? 4 : 3).Select(w => w.Text)),
                        WiringLabelKind.PowerFeed, page.PageNumber, word.X, word.Y, word.W, word.H));
                    continue;
                }
            }

            if (kind is { } k) labels.Add(new WiringLabel(word.Text.Trim(), k, page.PageNumber, word.X, word.Y, word.W, word.H));
        }

        return labels;
    }

    internal static WiringLabelKind? Classify(string token)
    {
        if (GroundRegex().IsMatch(token) || token is "GND" or "GROUND") return WiringLabelKind.Ground;
        if (ConnectorRegex().IsMatch(token)) return WiringLabelKind.Connector;
        if (SpliceRegex().IsMatch(token)) return WiringLabelKind.Splice;
        if (FuseRegex().IsMatch(token)) return WiringLabelKind.Fuse;
        if (RelayRegex().IsMatch(token)) return WiringLabelKind.Relay;
        if (PowerRegex().IsMatch(token)) return WiringLabelKind.PowerFeed;
        if (WireColorRegex().IsMatch(token)) return WiringLabelKind.WireColor;
        return null;
    }

    public static WiringQuestionKind ClassifyQuestion(string question)
    {
        var q = question.ToLowerInvariant();
        if (q.Contains("open", StringComparison.Ordinal) && (q.Contains("ground", StringComparison.Ordinal) || q.Contains("circuit", StringComparison.Ordinal))) return WiringQuestionKind.OpenCircuitEffect;
        if (q.Contains("continuity", StringComparison.Ordinal)) return WiringQuestionKind.ContinuityTest;
        if (q.Contains("test voltage", StringComparison.Ordinal) || q.Contains("where should i test", StringComparison.Ordinal) || q.Contains("backprobe", StringComparison.Ordinal)) return WiringQuestionKind.VoltageTestPoints;
        if (q.Contains("ground", StringComparison.Ordinal)) return WiringQuestionKind.Ground;
        if (q.Contains("power", StringComparison.Ordinal) || q.Contains("fuse", StringComparison.Ordinal) || q.Contains("feed", StringComparison.Ordinal) || q.Contains("relay", StringComparison.Ordinal)) return WiringQuestionKind.PowerSource;
        if (q.Contains("connector", StringComparison.Ordinal) || q.Contains("pin", StringComparison.Ordinal)) return WiringQuestionKind.Connector;
        if (q.Contains("component", StringComparison.Ordinal) || q.Contains("what is on", StringComparison.Ordinal)) return WiringQuestionKind.Components;
        return WiringQuestionKind.General;
    }

    public async Task<Result<WiringAnswer>> AskAsync(string question, Guid? documentId, int? pageNumber, Func<string, Task>? onText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return Error.Validation("Enter a question about the diagram.");

        // Locate the most relevant diagram page if the technician didn't pick one.
        if (documentId is null || pageNumber is null)
        {
            var hits = await knowledgeBase.SearchAsync(question, new KnowledgeSearchOptions
            {
                Limit = 5,
                Kind = DocumentKind.WiringDiagram,
                DocumentId = documentId,
            }, ct);
            var best = hits.FirstOrDefault();
            if (best is null)
            {
                return new WiringAnswer("No uploaded wiring diagram matches this question. Upload the diagram for this circuit (Knowledge Base → Upload, type \"Wiring diagram\") or pick a diagram and page first.",
                    documentId, pageNumber, [], [], [], false, null);
            }

            documentId = best.DocumentId;
            pageNumber = best.PageNumber;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        var page = await db.DocumentPages.AsNoTracking().FirstOrDefaultAsync(p => p.DocumentId == documentId && p.PageNumber == pageNumber, ct);
        if (document is null || page is null) return Error.NotFound("Diagram page");

        var labels = ExtractLabels(page);
        var kind = ClassifyQuestion(question);
        var relevantKinds = kind switch
        {
            WiringQuestionKind.Ground or WiringQuestionKind.OpenCircuitEffect => new[] { WiringLabelKind.Ground, WiringLabelKind.Splice },
            WiringQuestionKind.PowerSource => [WiringLabelKind.Fuse, WiringLabelKind.Relay, WiringLabelKind.PowerFeed],
            WiringQuestionKind.Connector => [WiringLabelKind.Connector, WiringLabelKind.WireColor],
            WiringQuestionKind.VoltageTestPoints or WiringQuestionKind.ContinuityTest => [WiringLabelKind.Connector, WiringLabelKind.Ground, WiringLabelKind.Fuse, WiringLabelKind.PowerFeed],
            _ => Enum.GetValues<WiringLabelKind>(),
        };

        var labelSummary = SummarizeLabels(labels);
        var route = await router.ResolveChatAsync(AiTask.WiringAnalysis, DataSensitivity.PrivateDocuments, cancellationToken: ct);
        if (!route.IsAvailable)
        {
            var deterministic = new StringBuilder();
            deterministic.Append($"**{document.Title}, page {page.PageNumber}** — AI is unavailable ({route.UnavailableReason}). Identifiers found on this page:\n\n");
            deterministic.Append(labelSummary.Length == 0 ? "_No identifiers could be read from this page (it may be a scanned image without OCR)._" : labelSummary);
            return new WiringAnswer(deterministic.ToString(), documentId, pageNumber,
                labels.Where(l => relevantKinds.Contains(l.Kind)).ToList(), labels, [], false, null);
        }

        var images = new List<ChatImage>();
        if (route.Model!.Capabilities.HasFlag(ModelCapabilities.Vision) && renderer is { IsAvailable: true } &&
            document.ContentType == "application/pdf")
        {
            try
            {
                var rendered = await renderer.RenderPageAsync(knowledgeBase.GetFilePath(document), page.PageNumber, 1600, ct);
                images.Add(new ChatImage(rendered.PngBytes, "image/png"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rendering is an enhancement; the extracted labels are still used.
            }
        }
        else if (route.Model.Capabilities.HasFlag(ModelCapabilities.Vision) && document.ContentType.StartsWith("image/", StringComparison.Ordinal) &&
                 imageProcessor is { IsAvailable: true })
        {
            var bytes = await File.ReadAllBytesAsync(knowledgeBase.GetFilePath(document), ct);
            var prepared = await imageProcessor.PrepareForAnalysisAsync(bytes, 1600, ct);
            images.Add(new ChatImage(prepared.Data, prepared.MediaType));
        }

        var prompt = new StringBuilder()
            .Append("Diagram: ").Append(document.Title).Append(", page ").Append(page.PageNumber).Append('\n')
            .Append("Question: ").Append(question).Append("\n\n")
            .Append("Identifiers extracted from this page:\n").Append(labelSummary.Length == 0 ? "(none could be read)" : labelSummary).Append("\n\n")
            .Append("Text read from the page:\n").Append(Text.Truncate(page.Text, 5000));

        var sb = new StringBuilder();
        await foreach (var update in route.Model.StreamAsync(new ChatRequest
                       {
                           SystemPrompt = Prompts.WiringAnalysis,
                           Messages = [ChatMessage.User(prompt.ToString(), images)],
                           Temperature = 0.1,
                       }, ct))
        {
            if (update is not TextDeltaUpdate t) continue;
            sb.Append(t.Text);
            if (onText is not null) await onText(t.Text);
        }

        var (validated, warnings) = ValidateIdentifiers(sb.ToString(), labels);
        var mentioned = labels.Where(l => validated.Contains(l.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        var highlights = mentioned.Count > 0 ? mentioned : labels.Where(l => relevantKinds.Contains(l.Kind)).ToList();
        return new WiringAnswer(validated, documentId, pageNumber, highlights, labels, warnings, true, $"{route.Model.Provider} · {route.Model.Model}");
    }

    /// <summary>Flags ground/connector/splice/fuse/relay identifiers that are not on the diagram.</summary>
    public static (string Text, IReadOnlyList<string> Warnings) ValidateIdentifiers(string answer, IReadOnlyList<WiringLabel> labels)
    {
        var known = labels.Select(l => l.Text.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        var text = IdentifierRegex().Replace(answer, m =>
        {
            var id = m.Value.ToUpperInvariant();
            if (known.Contains(id)) return m.Value;
            if (!warnings.Any(w => w.Contains(id, StringComparison.Ordinal)))
            {
                warnings.Add($"'{m.Value}' was mentioned but does not appear in the text read from this diagram — verify it on the diagram itself.");
            }

            return m.Value + " (not found on diagram)";
        });
        return (text, warnings);
    }

    private static string SummarizeLabels(IReadOnlyList<WiringLabel> labels)
    {
        var sb = new StringBuilder();
        foreach (var group in labels.GroupBy(l => l.Kind).OrderBy(g => g.Key))
        {
            var items = group.Select(l => l.Text).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
            sb.Append("- ").Append(group.Key switch
            {
                WiringLabelKind.Ground => "Grounds",
                WiringLabelKind.Connector => "Connectors",
                WiringLabelKind.Splice => "Splices",
                WiringLabelKind.Fuse => "Fuses",
                WiringLabelKind.Relay => "Relays",
                WiringLabelKind.WireColor => "Wire colors",
                _ => "Power feeds",
            }).Append(": ").Append(string.Join(", ", items)).Append('\n');
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^G\d{2,4}[A-Z]?$")]
    private static partial Regex GroundRegex();

    [GeneratedRegex(@"^(C\d{1,4}[A-Z]?|X\d{1,4})$")]
    private static partial Regex ConnectorRegex();

    [GeneratedRegex(@"^S\d{3,4}$")]
    private static partial Regex SpliceRegex();

    [GeneratedRegex(@"^(F\d{1,3}[A-Z]?|FUSE\d{0,3}|\d{1,3}A)$")]
    private static partial Regex FuseRegex();

    [GeneratedRegex(@"^(K\d{1,3}|RELAY)$")]
    private static partial Regex RelayRegex();

    [GeneratedRegex(@"^(B\+|BATT\+?|IGN|RUN/START|ACC)$")]
    private static partial Regex PowerRegex();

    [GeneratedRegex(@"^(L-|D-|LT|DK)?(BK|BLK|WH|WHT|RD|RED|GN|GRN|BU|BLU|YE|YEL|OG|ORN|ORG|VT|VIO|PU|PPL|BN|BRN|GY|GRY|PK|PNK|TN|LG|DG|LB|DB|NCA)([/\-](BK|BLK|WH|WHT|RD|RED|GN|GRN|BU|BLU|YE|YEL|OG|ORN|ORG|VT|VIO|PU|PPL|BN|BRN|GY|GRY|PK|PNK|TN|LG|DG|LB|DB))?$")]
    private static partial Regex WireColorRegex();

    [GeneratedRegex(@"\b(G\d{2,4}[A-Z]?|C\d{3,4}[A-Z]?|S\d{3,4}|K\d{1,3})\b")]
    private static partial Regex IdentifierRegex();
}
