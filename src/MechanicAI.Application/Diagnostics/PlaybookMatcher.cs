using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MechanicAI.Application.Content;

namespace MechanicAI.Application.Diagnostics;

public sealed record PlaybookMatch(PlaybookDefinition Playbook, double Score, IReadOnlyList<string> Reasons, bool MatchedByDtc);

/// <summary>Selects the playbooks relevant to a session's DTCs and complaint text.</summary>
public static class PlaybookMatcher
{
    private static readonly ConcurrentDictionary<string, Regex?> RegexCache = new(StringComparer.Ordinal);

    public static IReadOnlyList<PlaybookMatch> Match(
        IReadOnlyList<PlaybookDefinition> playbooks,
        IReadOnlyList<string> dtcs,
        string symptomText)
    {
        var text = symptomText ?? string.Empty;
        var matches = new List<PlaybookMatch>();

        foreach (var playbook in playbooks)
        {
            var reasons = new List<string>();
            var score = 0.0;
            var byDtc = false;

            foreach (var code in dtcs)
            {
                if (playbook.Triggers.DtcPatterns.Any(p => IsMatch(p, code)))
                {
                    score += 1.0;
                    byDtc = true;
                    reasons.Add($"DTC {code}");
                }
            }

            var keywordHits = playbook.Triggers.SymptomKeywords.Where(k => IsSymptomMatch(k, text)).ToList();
            if (keywordHits.Count > 0)
            {
                score += 0.35 * keywordHits.Count;
                reasons.Add("complaint mentions " + string.Join(", ", keywordHits.Take(3).Select(k => $"\"{Humanize(k)}\"")));
            }

            if (score > 0) matches.Add(new PlaybookMatch(playbook, score, reasons, byDtc));
        }

        // When codes identified specific playbooks, symptom-only playbooks (designed for
        // no-code complaints) would add noise — keep them only if nothing matched by code.
        var anyByDtc = matches.Any(m => m.MatchedByDtc);
        var selected = anyByDtc
            ? matches.Where(m => m.MatchedByDtc || (m.Playbook.Triggers.DtcPatterns.Count > 0 && m.Score >= 0.7))
            : matches;

        return selected.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>
    /// Matches a symptom keyword/regex against free text on word boundaries, so "stall" does not
    /// match "install" and "dies" does not match "diesel", while stems ("hesitat") still match
    /// "hesitation" and short words accept common suffixes ("stalls", "stalling").
    /// </summary>
    public static bool IsSymptomMatch(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(input)) return false;
        return IsMatch(ToWordBoundedPattern(pattern), input);
    }

    internal static string ToWordBoundedPattern(string pattern)
    {
        if (pattern.Contains("\\b", StringComparison.Ordinal) || pattern.Contains("(?<", StringComparison.Ordinal) || pattern.StartsWith('^'))
        {
            return pattern;
        }

        var isShortWord = pattern.Length <= 5 && pattern.All(char.IsLetter);
        return isShortWord
            ? $"(?<![A-Za-z0-9])(?:{pattern})(?:s|es|ed|ing)?(?![A-Za-z])"
            : $"(?<![A-Za-z0-9])(?:{pattern})";
    }

    public static bool IsMatch(string pattern, string input)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(input)) return false;
        var regex = RegexCache.GetOrAdd(pattern, static p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                return null;
            }
        });

        if (regex is null) return input.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string Humanize(string pattern) =>
        Regex.Replace(pattern, @"[\\\^\$\(\)\[\]\?\*\+\|]", " ").Replace("  ", " ", StringComparison.Ordinal).Trim();
}
