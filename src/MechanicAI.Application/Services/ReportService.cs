using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.Research;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Services;

public enum ReportFormat { Html, Markdown, Json }

/// <summary>
/// Exports a completed (or in-progress) diagnostic session as a report: vehicle, complaint,
/// DTCs, the evidence trail (every timestamped step), tests with actual results, final
/// diagnosis, repair, verification, and sources. HTML reports print cleanly to PDF.
/// </summary>
public sealed class ReportService(DiagnosticSessionService sessions, IAppPaths paths, ISettingsStore settings)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public async Task<Result<string>> ExportSessionAsync(Guid sessionId, ReportFormat format, CancellationToken ct = default)
    {
        var view = await sessions.GetViewAsync(sessionId, ct);
        if (view is null) return Error.NotFound("Diagnostic session");
        var markdown = BuildMarkdown(view.Session, view, settings.Current.Diagnostics.ShopName);
        var baseName = $"diagnostic-report-{DateTime.Now:yyyyMMdd-HHmm}-{Slug(view.Session.Title)}";
        string path;
        switch (format)
        {
            case ReportFormat.Markdown:
                path = Path.Combine(paths.ExportsDirectory, baseName + ".md");
                await File.WriteAllTextAsync(path, markdown, ct);
                break;
            case ReportFormat.Json:
                path = Path.Combine(paths.ExportsDirectory, baseName + ".json");
                await File.WriteAllTextAsync(path, Json.Serialize(new
                {
                    view.Session.Id,
                    view.Session.Title,
                    Vehicle = view.Session.Vehicle?.Description ?? view.Session.VehicleDescription,
                    view.Session.Complaint,
                    view.Session.Symptoms,
                    Dtcs = view.Session.Dtcs.Select(d => new { d.Code, d.Description, d.Status }),
                    Causes = view.Session.Causes.Select(c => new { c.Title, c.Probability, c.Status, c.Evidence }),
                    Tests = view.Session.Tests.Where(t => t.Status == TestStatus.Completed).Select(t => new { t.Title, t.Result, Outcome = t.SelectedOutcome?.Label, t.ActualResult, t.PerformedUtc }),
                    Steps = view.Session.Steps.Select(s => new { s.TimestampUtc, s.Kind, s.Actor, s.Title, s.Detail, s.IsReverted }),
                    view.Session.FinalDiagnosis,
                    view.Session.RepairPerformed,
                    view.Session.VerificationPassed,
                    view.Session.VerificationNotes,
                }, indented: true), ct);
                break;
            default:
                path = Path.Combine(paths.ExportsDirectory, baseName + ".html");
                await File.WriteAllTextAsync(path, WrapHtml(view.Session.Title, Markdown.ToHtml(markdown, Pipeline)), ct);
                break;
        }

        await sessions.AddObservationAsync(sessionId, $"Report exported ({format}): {Path.GetFileName(path)}", ct);
        return path;
    }

    public static string BuildMarkdown(DiagnosticSession s, DTOs.DiagnosticSessionView view, string? shopName)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"# Diagnostic Report — {s.Title}");
        if (!string.IsNullOrWhiteSpace(shopName)) sb.AppendLine($"**{shopName}**  ");
        sb.AppendLine(inv, $"Generated {DateTime.Now:f} · Session started {s.StartedUtc.ToLocalTime():f}{(s.CompletedUtc is { } c ? $" · Completed {c.ToLocalTime():f}" : string.Empty)}");
        if (s.IsSample) sb.AppendLine("\n> **SAMPLE DATA** — this session was created from built-in development samples.");
        sb.AppendLine();

        sb.AppendLine("## Vehicle");
        var v = s.Vehicle;
        if (v is not null)
        {
            sb.AppendLine($"- **Vehicle:** {v.Description}");
            if (v.Vin is not null) sb.AppendLine($"- **VIN:** {v.Vin}{(v.DataSource == VehicleDataSource.NhtsaVpic ? " (decoded by NHTSA vPIC)" : string.Empty)}");
            if (v.Transmission is not null) sb.AppendLine($"- **Transmission:** {v.Transmission}");
            if (v.Drivetrain is not null) sb.AppendLine($"- **Drivetrain:** {v.Drivetrain}");
        }
        else
        {
            sb.AppendLine($"- **Vehicle:** {(string.IsNullOrWhiteSpace(s.VehicleDescription) ? "Not specified" : s.VehicleDescription)}");
        }

        if (s.Mileage is { } miles) sb.AppendLine(inv, $"- **Mileage:** {miles:N0}");
        if (!string.IsNullOrWhiteSpace(s.TechnicianName)) sb.AppendLine($"- **Technician:** {s.TechnicianName}");
        sb.AppendLine();

        sb.AppendLine("## Customer complaint");
        sb.AppendLine(string.IsNullOrWhiteSpace(s.Complaint) ? "_Not recorded_" : s.Complaint);
        if (s.Symptoms.Count > 0) sb.AppendLine($"\n**Symptoms:** {string.Join(", ", s.Symptoms)}");
        if (s.Conditions.Count > 0) sb.AppendLine($"\n**Conditions:** {string.Join(", ", s.Conditions)}");
        sb.AppendLine();

        sb.AppendLine("## Trouble codes");
        if (s.Dtcs.Count == 0) sb.AppendLine("_None recorded_");
        foreach (var d in s.Dtcs) sb.AppendLine($"- **{d.Code}** ({d.Status}) — {d.Description ?? "definition not in built-in reference"} _({d.Source})_");
        sb.AppendLine();

        sb.AppendLine("## Tests performed");
        var tests = s.Tests.Where(t => t.Status == TestStatus.Completed).OrderBy(t => t.ExecutionOrder).ToList();
        if (tests.Count == 0) sb.AppendLine("_No tests recorded_");
        foreach (var t in tests)
        {
            sb.AppendLine(inv, $"### {t.ExecutionOrder}. {t.Title} — {t.Result?.ToString().ToUpperInvariant()}");
            sb.AppendLine($"- **Result:** {t.SelectedOutcome?.Label}");
            if (t.ActualResult is not null) sb.AppendLine($"- **Observed:** {t.ActualResult}");
            if (t.ExpectedResult is not null) sb.AppendLine($"- **Expected:** {t.ExpectedResult}");
            if (t.PerformedUtc is { } when) sb.AppendLine(inv, $"- **Performed:** {when.ToLocalTime():g}{(t.PerformedBy is null ? string.Empty : $" by {t.PerformedBy}")}");
            if (t.Origin == ActorKind.Ai) sb.AppendLine("- _Test suggested by AI (inference)._");
        }

        sb.AppendLine();
        sb.AppendLine("## Cause assessment");
        sb.AppendLine("| Cause | Status | Probability | Evidence | Origin |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var node in s.Causes.OrderByDescending(n => n.Status == CauseStatus.Confirmed).ThenByDescending(n => n.Probability))
        {
            sb.AppendLine(inv, $"| {Escape(node.Title)} | {node.Status} | {node.Probability:P0} | {EvidenceLabel(node.Evidence)} | {node.Origin} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Final diagnosis");
        sb.AppendLine(s.FinalDiagnosis ?? "_Not confirmed_");
        sb.AppendLine();
        sb.AppendLine("## Repair");
        sb.AppendLine(s.RepairPerformed ?? "_Not recorded_");
        sb.AppendLine();
        sb.AppendLine("## Verification");
        sb.AppendLine(s.VerificationPassed switch
        {
            true => $"**PASSED** — {s.VerificationNotes}",
            false => $"**FAILED** — {s.VerificationNotes}",
            _ => "_Not yet verified_",
        });
        if (s.VerificationPlan.Count > 0)
        {
            sb.AppendLine("\nVerification plan:");
            foreach (var step in s.VerificationPlan) sb.AppendLine($"- {step}");
        }

        if (!string.IsNullOrWhiteSpace(s.AiSummary))
        {
            sb.AppendLine();
            sb.AppendLine("## AI analysis (inference — not verified)");
            sb.AppendLine(s.AiSummary);
        }

        if (view.SafetyWarnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Safety notes");
            foreach (var w in view.SafetyWarnings) sb.AppendLine($"- **{w.Title}:** {w.Message}");
        }

        var sources = s.Nodes.SelectMany(n => n.Sources).Concat(s.Tests.SelectMany(t => t.Sources))
            .Where(src => src.Type != SourceType.BuiltInReference)
            .GroupBy(src => src.Url ?? src.Title).Select(g => g.First()).ToList();
        if (sources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Sources");
            foreach (var src in sources)
            {
                sb.AppendLine($"- {src.Title} — {SourceClassifier.Label(src.Type)}{(src.Url is null ? string.Empty : $" — {src.Url}")}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Timeline");
        foreach (var step in s.Steps.OrderBy(x => x.Sequence))
        {
            var reverted = step.IsReverted ? " ~~(reverted)~~" : string.Empty;
            sb.AppendLine(inv, $"- `{step.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}` **{step.Actor}** — {Escape(step.Title)}{reverted}");
            if (!string.IsNullOrWhiteSpace(step.Detail)) sb.AppendLine($"  - {Escape(step.Detail).Replace("\n", "\n    ", StringComparison.Ordinal)}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("_Evidence classes: Verified = authoritative record or technician-confirmed; Source-derived = cited source; Technician observation; AI inference; Unconfirmed possibility. Specifications must be confirmed in OEM service information._");
        return sb.ToString();
    }

    public static string EvidenceLabel(EvidenceClass evidence) => evidence switch
    {
        EvidenceClass.Verified => "Verified",
        EvidenceClass.SourceDerived => "Source-derived",
        EvidenceClass.TechnicianObservation => "Technician observation",
        EvidenceClass.AiInference => "AI inference",
        _ => "Unconfirmed possibility",
    };

    public static string WrapHtml(string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
        body { font-family: "Segoe UI", Arial, sans-serif; color: #1b1f24; max-width: 900px; margin: 32px auto; padding: 0 24px; line-height: 1.5; }
        h1 { font-size: 26px; border-bottom: 3px solid #3d8bfd; padding-bottom: 8px; }
        h2 { font-size: 19px; margin-top: 28px; color: #0b3d91; }
        h3 { font-size: 15px; margin-bottom: 4px; }
        table { border-collapse: collapse; width: 100%; font-size: 13px; }
        th, td { border: 1px solid #d0d7de; padding: 6px 8px; text-align: left; }
        th { background: #f3f6f9; }
        code { background: #f3f6f9; padding: 1px 4px; border-radius: 3px; font-size: 12px; }
        blockquote { border-left: 4px solid #f0ad4e; margin: 12px 0; padding: 4px 12px; background: #fff8e6; }
        @media print { body { margin: 0; } h2 { page-break-after: avoid; } }
        </style></head><body>
        {{body}}
        </body></html>
        """;

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string Slug(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return Text.Truncate(slug.Trim('-'), 40, string.Empty);
    }
}
