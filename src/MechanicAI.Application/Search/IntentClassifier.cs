using MechanicAI.Domain.Enums;

namespace MechanicAI.Application.Search;

/// <summary>
/// Scores what the technician most likely wants from a universal-search query. The
/// technician never has to pick a category; every plausible intent is searched and the
/// strongest is shown first.
/// </summary>
public static class IntentClassifier
{
    private static readonly (SearchIntent Intent, string[] Cues, double Weight)[] Cues =
    [
        (SearchIntent.Wiring, ["wiring", "wire", "circuit", "pinout", "pin out", "connector", "ground", "splice", "schematic", "diagram", "harness", "fuse", "relay", "power feed", "voltage at"], 0.35),
        (SearchIntent.Repair, ["how to replace", "how do i replace", "replace", "remove", "removal", "install", "installation", "torque", "procedure", "r&r", "swap out", "change the"], 0.3),
        (SearchIntent.Component, ["where is", "location", "located", "find the", "which side", "bank 1", "bank 2"], 0.4),
        (SearchIntent.Training, ["how does", "what is", "what does", "explain", "learn", "lesson", "quiz", "theory", "why does", "difference between"], 0.3),
        (SearchIntent.Document, ["manual", "tsb", "bulletin", "spec", "specification", "capacity", "resistance", "my documents", "service info", "torque spec"], 0.3),
        (SearchIntent.Diagnostic, ["what should i check", "diagnose", "diagnosis", "check first", "why is", "problem", "issue", "keeps", "test"], 0.3),
        (SearchIntent.Web, ["forum", "recall", "known issue", "common problem", "news", "reddit", "youtube"], 0.3),
    ];

    public static IReadOnlyList<(SearchIntent Intent, double Score)> Classify(ParsedQuery q, string lower)
    {
        var scores = new Dictionary<SearchIntent, double>();
        void Add(SearchIntent intent, double value) => scores[intent] = Math.Min(1.0, scores.GetValueOrDefault(intent) + value);

        if (q.Vin is not null) Add(SearchIntent.Vin, 0.95);
        if (q.Dtcs.Count > 0)
        {
            var codeOnly = string.IsNullOrWhiteSpace(q.Remainder) && !q.HasVehicle;
            Add(SearchIntent.Dtc, codeOnly ? 0.95 : 0.6);
            if (!codeOnly) Add(SearchIntent.Diagnostic, 0.45);
        }

        if (q.Symptoms.Count > 0) Add(SearchIntent.Diagnostic, 0.35 + 0.1 * Math.Min(3, q.Symptoms.Count));
        if (q.HasVehicle) Add(SearchIntent.Vehicle, q.Dtcs.Count == 0 && q.Symptoms.Count == 0 && string.IsNullOrWhiteSpace(q.Remainder) ? 0.8 : 0.2);
        if (q.Components.Count > 0)
        {
            Add(SearchIntent.Component, 0.2);
            Add(SearchIntent.Repair, 0.1);
        }

        foreach (var (intent, cues, weight) in Cues)
        {
            var hits = cues.Count(c => lower.Contains(c, StringComparison.Ordinal));
            if (hits > 0) Add(intent, weight + 0.1 * (hits - 1));
        }

        if (lower.StartsWith("how do i test", StringComparison.Ordinal) || lower.StartsWith("how to test", StringComparison.Ordinal))
        {
            Add(SearchIntent.Diagnostic, 0.3);
            Add(SearchIntent.Training, 0.2);
        }

        // The web is always a fallback for anything not answered locally.
        Add(SearchIntent.Web, 0.15);

        return scores.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, Math.Round(kv.Value, 3))).ToList();
    }
}
