using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.Search;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

public sealed record DtcSearchResult(
    string Code,
    string Description,
    string Subsystem,
    DtcSystem System,
    bool IsGeneric,
    string? Manufacturer,
    string MatchReason,
    bool HasPlaybook);

public sealed record PlaybookCauseSummary(string Key, string Title, DiagnosticCategory Category, double RelativeLikelihood, string? Description);

public sealed record PlaybookTestSummary(string Key, string Title, string? Purpose, int Minutes, int Difficulty, IReadOnlyList<string> Tools, IReadOnlyList<string> Safety);

public sealed record PlaybookSummary(
    string Key,
    string Title,
    string Summary,
    IReadOnlyList<PlaybookCauseSummary> Causes,
    IReadOnlyList<PlaybookTestSummary> Tests,
    IReadOnlyList<string> Verification);

public sealed record DtcDetail(
    string Code,
    DtcSystem System,
    bool IsGeneric,
    string SubsystemFromCode,
    IReadOnlyList<DtcDefinition> Definitions,
    IReadOnlyList<PlaybookSummary> Playbooks,
    IReadOnlyList<DtcDefinition> Related,
    IReadOnlyList<SafetyWarning> Safety,
    int TimesSeenInSessions)
{
    public DtcDefinition? Primary => Definitions.FirstOrDefault();

    public bool IsKnown => Definitions.Count > 0;
}

/// <summary>DTC reference database: search by code, partial code, text, or "Make code".</summary>
public sealed class DtcService(IAppDbContextFactory dbFactory, IReferenceContentProvider content, IKeywordIndex? keywordIndex = null)
{
    public async Task<IReadOnlyList<DtcSearchResult>> SearchAsync(string query, int take = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var parsed = QueryParser.Parse(query);
        var playbooks = await content.LoadPlaybooksAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var results = new List<DtcSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddRange(IEnumerable<DtcDefinition> definitions, string reason)
        {
            foreach (var d in definitions)
            {
                if (!seen.Add(d.Code + "|" + d.Manufacturer)) continue;
                results.Add(new DtcSearchResult(d.Code, d.Description, d.Subsystem, d.System, d.IsGeneric, d.Manufacturer, reason,
                    playbooks.Any(p => p.Triggers.DtcPatterns.Any(pattern => PlaybookMatcher.IsMatch(pattern, d.Code)))));
            }
        }

        // 1. Exact codes (manufacturer-specific definitions for the named make first).
        if (parsed.Dtcs.Count > 0)
        {
            var codes = parsed.Dtcs.ToList();
            var exact = await db.Dtcs.AsNoTracking().Where(d => codes.Contains(d.Code)).ToListAsync(ct);
            var ordered = exact
                .OrderByDescending(d => parsed.Make is not null && string.Equals(d.Manufacturer, parsed.Make, StringComparison.OrdinalIgnoreCase))
                .ThenBy(d => codes.IndexOf(d.Code));
            AddRange(ordered, "Exact code");

            foreach (var code in codes.Where(c => exact.All(e => e.Code != c)))
            {
                if (!DtcCode.TryParse(code, out var dtc)) continue;
                results.Add(new DtcSearchResult(code,
                    dtc.IsManufacturerSpecific ? "Manufacturer-specific code — meaning varies by manufacturer" : "Not in the built-in reference",
                    dtc.SubsystemDescription, dtc.System, dtc.IsGeneric, null, "Code format recognized",
                    playbooks.Any(p => p.Triggers.DtcPatterns.Any(pattern => PlaybookMatcher.IsMatch(pattern, code)))));
            }
        }

        // 2. Partial code prefix ("P03", "030").
        var compact = query.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (parsed.Dtcs.Count == 0 && compact.Length is >= 2 and <= 4 && compact.All(char.IsAsciiLetterOrDigit))
        {
            var prefix = char.IsAsciiDigit(compact[0]) ? "P" + compact : compact;
            var byPrefix = await db.Dtcs.AsNoTracking().Where(d => d.Code.StartsWith(prefix)).OrderBy(d => d.Code).Take(take).ToListAsync(ct);
            AddRange(byPrefix, "Code prefix");
        }

        // 3. Full text ("misfire cylinder 2", "lean bank 1").
        var text = parsed.Remainder;
        if (!string.IsNullOrWhiteSpace(text) && results.Count < take)
        {
            IReadOnlyList<string> codes = [];
            if (keywordIndex is not null) codes = await keywordIndex.SearchDtcCodesAsync(text, take, ct);
            if (codes.Count > 0)
            {
                var defs = await db.Dtcs.AsNoTracking().Where(d => codes.Contains(d.Code)).ToListAsync(ct);
                AddRange(codes.SelectMany(c => defs.Where(d => d.Code == c)), "Description match");
            }
            else
            {
                var terms = Text.Tokenize(text).Where(t => t.Length > 2).Take(4).ToList();
                if (terms.Count > 0)
                {
                    var candidates = db.Dtcs.AsNoTracking();
                    foreach (var term in terms)
                    {
                        var pattern = "%" + term + "%";
                        candidates = candidates.Where(d => EF.Functions.Like(d.Description, pattern));
                    }

                    AddRange(await candidates.OrderBy(d => d.Code).Take(take).ToListAsync(ct), "Description match");
                }
            }
        }

        return results.Take(take).ToList();
    }

    public async Task<DtcDetail?> GetDetailAsync(string code, string? make = null, CancellationToken ct = default)
    {
        if (!DtcCode.TryParse(code, out var dtc, assumePowertrain: true)) return null;
        await using var db = await dbFactory.CreateAsync(ct);
        var definitions = await db.Dtcs.AsNoTracking().Where(d => d.Code == dtc.Value).ToListAsync(ct);
        definitions = definitions
            .OrderByDescending(d => make is not null && string.Equals(d.Manufacturer, make, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.Manufacturer is null)
            .ToList();

        var relatedCodes = definitions.SelectMany(d => d.RelatedCodes).Distinct().ToList();
        var related = relatedCodes.Count == 0
            ? []
            : await db.Dtcs.AsNoTracking().Where(d => relatedCodes.Contains(d.Code) && d.Manufacturer == null).OrderBy(d => d.Code).ToListAsync(ct);

        var seen = await db.SessionDtcs.AsNoTracking().CountAsync(d => d.Code == dtc.Value, ct);

        var playbooks = await content.LoadPlaybooksAsync(ct);
        var matching = PlaybookMatcher.Match(playbooks, [dtc.Value], string.Empty).Where(m => m.MatchedByDtc).ToList();
        var summaries = matching.Select(m => Summarize(m.Playbook, dtc.Value)).ToList();

        var tags = definitions.SelectMany(d => d.SafetyTags)
            .Concat(matching.SelectMany(m => m.Playbook.Causes.SelectMany(c => c.Safety)))
            .Concat(SafetyAdvisor.DetectTags(dtc.Value));
        return new DtcDetail(dtc.Value, dtc.System, dtc.IsGeneric, dtc.SubsystemDescription, definitions, summaries, related,
            SafetyAdvisor.ForTags(tags), seen);
    }

    /// <summary>Adds a technician-supplied (e.g. manufacturer-specific) definition, labeled with its source.</summary>
    public async Task<Result> SaveUserDefinitionAsync(string code, string? manufacturer, string description, string source, string? notes,
        CancellationToken ct = default)
    {
        if (!DtcCode.TryParse(code, out var dtc)) return Error.Validation("Enter a valid DTC.");
        if (string.IsNullOrWhiteSpace(description)) return Error.Validation("Enter the code description.");
        if (string.IsNullOrWhiteSpace(source)) return Error.Validation("Record where this definition came from (e.g. OEM service information).");

        await using var db = await dbFactory.CreateAsync(ct);
        var existing = await db.Dtcs.FirstOrDefaultAsync(d => d.Code == dtc.Value && d.Manufacturer == manufacturer && d.IsUserDefined, ct);
        if (existing is null)
        {
            existing = new DtcDefinition { Code = dtc.Value, Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer.Trim(), IsUserDefined = true };
            db.Dtcs.Add(existing);
        }

        existing.System = dtc.System;
        existing.IsGeneric = dtc.IsGeneric;
        existing.Subsystem = dtc.SubsystemDescription;
        existing.Description = description.Trim();
        existing.Source = "Technician entry: " + source.Trim();
        existing.Notes = notes;
        await db.SaveChangesAsync(ct);
        if (keywordIndex is not null)
        {
            var all = await db.Dtcs.AsNoTracking().ToListAsync(ct);
            await keywordIndex.IndexDtcsAsync(all, ct);
        }

        return Result.Success();
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Dtcs.CountAsync(ct);
    }

    private static PlaybookSummary Summarize(Content.PlaybookDefinition playbook, string code)
    {
        var context = new TreeContext([code], string.Empty, null);
        var priors = playbook.Causes.Select(c => (Cause: c, Prior: DiagnosticTreeBuilder.ApplyModifiers(c, context).Prior)).ToList();
        var total = priors.Sum(p => p.Prior);
        return new PlaybookSummary(
            playbook.Key,
            playbook.Title,
            playbook.Summary,
            priors.OrderByDescending(p => p.Prior)
                .Select(p => new PlaybookCauseSummary(p.Cause.Key, p.Cause.Title, DiagnosticTreeBuilder.ParseCategory(p.Cause.Category),
                    total > 0 ? p.Prior / total : 0, p.Cause.Description))
                .ToList(),
            playbook.Tests.Select(t => new PlaybookTestSummary(t.Key, t.Title, t.Purpose, t.Minutes, t.Difficulty, t.Tools, t.Safety)).ToList(),
            playbook.Verification);
    }
}
