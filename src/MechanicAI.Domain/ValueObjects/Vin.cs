using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MechanicAI.Domain.ValueObjects;

/// <summary>
/// A 17-character Vehicle Identification Number (ISO 3779 / 49 CFR 565).
/// Performs structural validation locally; authoritative decoding is done by NHTSA vPIC.
/// </summary>
public readonly record struct Vin
{
    private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    // Position-10 model year codes. Each code maps to two years 30 years apart.
    private const string YearCodes = "ABCDEFGHJKLMNPRSTVWXY123456789";

    public string Value { get; }

    private Vin(string value) => Value = value;

    public override string ToString() => Value;

    /// <summary>World Manufacturer Identifier (positions 1–3).</summary>
    public string Wmi => Value[..3];

    /// <summary>Vehicle Descriptor Section (positions 4–9).</summary>
    public string Vds => Value[3..9];

    /// <summary>Vehicle Identifier Section (positions 10–17).</summary>
    public string Vis => Value[9..];

    public char CheckDigit => Value[8];

    public char ModelYearCode => Value[9];

    /// <summary>
    /// True when position 9 matches the computed check digit. The check digit is mandatory
    /// for North American vehicles; many other markets do not use it, so a mismatch is a
    /// warning rather than proof the VIN is invalid.
    /// </summary>
    public bool HasValidCheckDigit => ComputeCheckDigit(Value) == CheckDigit;

    /// <summary>True when the VIN was assigned for the North American market (check digit mandatory).</summary>
    public bool IsNorthAmerican => Value[0] is >= '1' and <= '5';

    /// <summary>Both candidate model years for the position-10 code (e.g. 'J' → 1988 and 2018).</summary>
    public IReadOnlyList<int> CandidateModelYears
    {
        get
        {
            var index = YearCodes.IndexOf(ModelYearCode);
            if (index < 0) return [];
            return [1980 + index, 2010 + index];
        }
    }

    /// <summary>
    /// Best-effort model year. For North American passenger vehicles, a letter in position 7
    /// indicates the 2010–2039 cycle and a digit indicates 1980–2009. Returns null if the
    /// year code is invalid. This is a local estimate; the vPIC decode is authoritative.
    /// </summary>
    public int? EstimatedModelYear
    {
        get
        {
            var years = CandidateModelYears;
            if (years.Count == 0) return null;
            if (!IsNorthAmerican) return years[1] <= DateTime.UtcNow.Year + 1 ? years[1] : years[0];
            return char.IsLetter(Value[6]) ? years[1] : years[0];
        }
    }

    /// <summary>Region of manufacture derived from the first character (ISO 3780).</summary>
    public string RegionOfManufacture => Value[0] switch
    {
        >= '1' and <= '5' => "North America",
        >= '6' and <= '7' => "Oceania",
        >= '8' and <= '9' => "South America",
        >= 'A' and <= 'H' => "Africa",
        >= 'J' and <= 'R' => "Asia",
        >= 'S' and <= 'Z' => "Europe",
        _ => "Unknown",
    };

    public static Vin Parse(string input) =>
        TryParse(input, out var vin, out var error) ? vin : throw new FormatException(error);

    public static bool TryParse([NotNullWhen(true)] string? input, out Vin vin) => TryParse(input, out vin, out _);

    public static bool TryParse([NotNullWhen(true)] string? input, out Vin vin, out string? error)
    {
        vin = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "VIN is empty.";
            return false;
        }

        var normalized = Normalize(input);
        if (normalized.Length != 17)
        {
            error = $"A VIN must be 17 characters (got {normalized.Length}).";
            return false;
        }

        foreach (var c in normalized)
        {
            if (c is 'I' or 'O' or 'Q')
            {
                error = $"VINs never contain the letters I, O, or Q (found '{c}'). Check for 1/0 confusion.";
                return false;
            }

            if (!char.IsAsciiLetterOrDigit(c))
            {
                error = $"Invalid character '{c}' in VIN.";
                return false;
            }
        }

        vin = new Vin(normalized);
        error = null;
        return true;
    }

    /// <summary>Uppercases and strips whitespace, dashes, and other separators.</summary>
    public static string Normalize(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_' or '.' or '*') continue;
            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    public static char ComputeCheckDigit(string vin)
    {
        if (vin.Length != 17) throw new ArgumentException("VIN must be 17 characters.", nameof(vin));
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += Transliterate(vin[i]) * Weights[i];
        }

        var remainder = sum % 11;
        return remainder == 10 ? 'X' : (char)('0' + remainder);
    }

    private static int Transliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1,
        'B' or 'K' or 'S' => 2,
        'C' or 'L' or 'T' => 3,
        'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5,
        'F' or 'W' => 6,
        'G' or 'P' or 'X' => 7,
        'H' or 'Y' => 8,
        'R' or 'Z' => 9,
        _ => 0,
    };

    /// <summary>Finds VIN candidates (17 valid characters) in free text such as OCR output.</summary>
    public static IReadOnlyList<Vin> FindCandidates(string text)
    {
        var results = new List<Vin>();
        if (string.IsNullOrEmpty(text)) return results;

        // Normalize common OCR confusions inside alphanumeric runs: O→0, I→1, Q→0.
        var tokens = text.ToUpperInvariant()
            .Split([' ', '\n', '\r', '\t', ',', ';', ':', '|', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in tokens)
        {
            var token = new string(raw.Where(char.IsAsciiLetterOrDigit).ToArray());
            for (var start = 0; start + 17 <= token.Length; start++)
            {
                var slice = token.Substring(start, 17).Replace('O', '0').Replace('I', '1').Replace('Q', '0');
                if (TryParse(slice, out var vin) && (vin.HasValidCheckDigit || !vin.IsNorthAmerican) && !results.Contains(vin))
                {
                    results.Add(vin);
                }
            }
        }

        return results;
    }
}
