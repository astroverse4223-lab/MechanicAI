using System.Text.RegularExpressions;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Ai;

public sealed record CitationReport(
    string CleanedText,
    IReadOnlyList<SourceCitation> CitedSources,
    IReadOnlyList<string> Warnings)
{
    public bool HadProblems => Warnings.Count > 0;
}

/// <summary>
/// Post-processes model output so it can never present a fabricated source:
/// citation labels that were not produced by a tool are removed, URLs that did not come
/// from a retrieved source are removed, and uncited "source-derived" statements are flagged.
/// </summary>
public static partial class CitationValidator
{
    public static CitationReport Validate(string text, SourceRegistry registry)
    {
        if (string.IsNullOrEmpty(text)) return new CitationReport(string.Empty, [], []);
        var warnings = new List<string>();
        var cited = new List<SourceCitation>();

        // [S1], [S1, S3], [S1][S2], [s2]
        var cleaned = CitationGroupRegex().Replace(text, match =>
        {
            var labels = LabelRegex().Matches(match.Value).Select(m => m.Value.ToUpperInvariant()).ToList();
            var valid = new List<string>();
            foreach (var label in labels)
            {
                var source = registry.Find(label);
                if (source is null)
                {
                    warnings.Add($"Removed citation [{label}]: no such source was retrieved in this conversation.");
                    continue;
                }

                valid.Add(source.Label);
                if (!cited.Contains(source)) cited.Add(source);
            }

            return valid.Count == 0 ? string.Empty : "[" + string.Join(", ", valid) + "]";
        });

        cleaned = UrlRegex().Replace(cleaned, match =>
        {
            var url = match.Value.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
            var trailing = match.Value[url.Length..];
            if (registry.ContainsUrl(url)) return match.Value;
            warnings.Add($"Removed a link that did not come from a retrieved source ({Common.Text.Truncate(url, 80)}).");
            return "(link removed — not from a retrieved source)" + trailing;
        });

        // Markdown links whose target was removed leave "[text](...)" artifacts.
        cleaned = cleaned.Replace("((link removed — not from a retrieved source))", "(link removed — not from a retrieved source)", StringComparison.Ordinal);

        var sourceSection = SourceSectionRegex().Match(cleaned);
        if (sourceSection.Success)
        {
            var uncited = sourceSection.Groups["body"].Value
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith('-') || l.StartsWith('*') || (l.Length > 2 && char.IsDigit(l[0]) && l[1] == '.'))
                .Count(l => !LabelRegex().IsMatch(l));
            if (uncited > 0)
            {
                warnings.Add($"{uncited} statement(s) under 'Source-derived information' have no citation — treat them as unverified.");
            }
        }

        cleaned = MultiSpaceRegex().Replace(cleaned, " ");
        return new CitationReport(cleaned.Trim(), cited, warnings);
    }

    [GeneratedRegex(@"\[(?:\s*[Ss]\d{1,3}\s*[,;]?\s*)+\](?:\[(?:\s*[Ss]\d{1,3}\s*[,;]?\s*)+\])*")]
    private static partial Regex CitationGroupRegex();

    [GeneratedRegex(@"[Ss]\d{1,3}")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"https?://[^\s<>()\[\]""']+[^\s<>()\[\]""'.,;:]")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"#+\s*Source-derived information[^\n]*\n(?<body>(?:(?!\n#+\s).)*)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SourceSectionRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();
}
