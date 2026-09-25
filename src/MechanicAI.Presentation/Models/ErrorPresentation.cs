using System.Globalization;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Presentation.Models;

/// <summary>Severity of a page notice. Mirrors WinUI's InfoBarSeverity without referencing it.</summary>
public enum NoticeSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>How application errors are presented.</summary>
public static class ErrorPresentation
{
    public static NoticeSeverity Severity(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation or ErrorKind.NotFound or ErrorKind.Conflict => NoticeSeverity.Warning,
        ErrorKind.NotConfigured or ErrorKind.Offline or ErrorKind.RateLimited => NoticeSeverity.Warning,
        ErrorKind.Cancelled => NoticeSeverity.Informational,
        _ => NoticeSeverity.Error,
    };

    public static string Title(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "Check the details",
        ErrorKind.NotFound => "Not found",
        ErrorKind.Conflict => "Conflict",
        ErrorKind.Timeout => "Timed out",
        ErrorKind.Unavailable => "Service unavailable",
        ErrorKind.RateLimited => "Rate limited",
        ErrorKind.Unauthorized => "Not authorized",
        ErrorKind.InvalidResponse => "Unexpected response",
        ErrorKind.NotConfigured => "Setup needed",
        ErrorKind.Offline => "Offline",
        ErrorKind.Cancelled => "Cancelled",
        _ => "Something went wrong",
    };
}

/// <summary>Formatting helpers shared by view models (culture-aware for display).</summary>
public static class Format
{
    public static string Percent(double fraction) => fraction.ToString("P0", CultureInfo.CurrentCulture);

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Local(DateTime? utc) => utc is { } value ? Local(value) : "—";

    public static string Relative(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(30)) return $"{(int)span.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    public static string Money(decimal value) => value.ToString("C", CultureInfo.CurrentCulture);

    public static string Evidence(EvidenceClass evidence) => evidence switch
    {
        EvidenceClass.Verified => "Verified",
        EvidenceClass.SourceDerived => "From source",
        EvidenceClass.TechnicianObservation => "Technician observation",
        EvidenceClass.AiInference => "AI inference",
        _ => "Unconfirmed possibility",
    };

    public static string Actor(ActorKind actor) => actor switch
    {
        ActorKind.Ai => "AI",
        ActorKind.System => "Playbook",
        _ => "Technician",
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Join(IEnumerable<string> values, string empty = "—")
    {
        var text = string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return text.Length == 0 ? empty : text;
    }

    /// <summary>Splits a comma/semicolon/newline separated list typed by the user.</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static int? ParseInt(string? text) =>
        int.TryParse(text?.Replace(",", string.Empty, StringComparison.Ordinal).Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : null;

    public static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text?.Trim(), NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.CurrentCulture, out var value) ? value : null;
}
