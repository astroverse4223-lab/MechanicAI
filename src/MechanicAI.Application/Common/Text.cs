using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MechanicAI.Application.Common;

public static partial class Text
{
    public static string Truncate(string? value, int maxLength, string ellipsis = "…")
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? string.Empty;
        return string.Concat(value.AsSpan(0, Math.Max(0, maxLength - ellipsis.Length)), ellipsis);
    }

    public static string CollapseWhitespace(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : WhitespaceRegex().Replace(value, " ").Trim();

    /// <summary>Rough token estimate (≈4 characters per token for English technical text).</summary>
    public static int EstimateTokens(string? value) => string.IsNullOrEmpty(value) ? 0 : (value.Length + 3) / 4;

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Sha256Hex(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    public static string TitleCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var lower = value.Trim().ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);
    }

    /// <summary>Normalizes a manufacturer name from upstream data ("FORD" → "Ford", "BMW" stays "BMW").</summary>
    public static string NormalizeMake(string? make)
    {
        if (string.IsNullOrWhiteSpace(make)) return string.Empty;
        var trimmed = make.Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "BMW" => "BMW",
            "GMC" => "GMC",
            "MINI" => "MINI",
            "FIAT" => "FIAT",
            "RAM" => "Ram",
            "KIA" => "Kia",
            "MERCEDES-BENZ" => "Mercedes-Benz",
            "ROLLS-ROYCE" => "Rolls-Royce",
            "LAND ROVER" => "Land Rover",
            "ALFA ROMEO" => "Alfa Romeo",
            "ASTON MARTIN" => "Aston Martin",
            "MCLAREN" => "McLaren",
            _ => TitleCase(trimmed),
        };
    }

    public static IEnumerable<string> Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (Match m in WordRegex().Matches(value.ToLowerInvariant()))
        {
            yield return m.Value;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[a-z0-9]+(?:[.\-/][a-z0-9]+)*")]
    private static partial Regex WordRegex();
}
