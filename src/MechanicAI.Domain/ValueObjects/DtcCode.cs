using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Domain.ValueObjects;

/// <summary>
/// A diagnostic trouble code in SAE J2012 / ISO 15031-6 format (e.g. P0302, U0100, P0A80).
/// </summary>
public readonly partial record struct DtcCode
{
    public string Value { get; }

    private DtcCode(string value) => Value = value;

    public override string ToString() => Value;

    public char SystemLetter => Value[0];

    public DtcSystem System => Value[0] switch
    {
        'P' => DtcSystem.Powertrain,
        'B' => DtcSystem.Body,
        'C' => DtcSystem.Chassis,
        _ => DtcSystem.Network,
    };

    /// <summary>
    /// True when the code is in an SAE-controlled (generic) range. Generic codes share a
    /// definition across manufacturers; manufacturer-controlled codes do not.
    /// </summary>
    public bool IsGeneric
    {
        get
        {
            var second = Value[1];
            return Value[0] switch
            {
                'P' => second == '0' || second == '2' || (second == '3' && IsP3Generic()),
                'B' or 'C' or 'U' => second == '0',
                _ => false,
            };
        }
    }

    public bool IsManufacturerSpecific => !IsGeneric;

    /// <summary>J2012 subsystem grouping for P0 codes; a system-level description otherwise.</summary>
    public string SubsystemDescription
    {
        get
        {
            if (Value[0] == 'P' && Value[1] == '0')
            {
                return Value[2] switch
                {
                    '0' => "Fuel and Air Metering and Auxiliary Emission Controls",
                    '1' => "Fuel and Air Metering",
                    '2' => "Fuel and Air Metering (Injector Circuit)",
                    '3' => "Ignition System or Misfire",
                    '4' => "Auxiliary Emission Controls",
                    '5' => "Vehicle Speed Controls and Idle Control System",
                    '6' => "Computer Output Circuit",
                    '7' or '8' or '9' => "Transmission",
                    'A' or 'B' or 'C' => "Hybrid Propulsion",
                    _ => "Powertrain",
                };
            }

            return System switch
            {
                DtcSystem.Powertrain => "Powertrain",
                DtcSystem.Body => "Body",
                DtcSystem.Chassis => "Chassis",
                _ => "Network Communication",
            };
        }
    }

    private bool IsP3Generic()
    {
        // P3000–P33FF are manufacturer controlled; P3400–P3FFF are SAE controlled.
        var third = Value[2];
        return third is >= '4' and <= '9' or >= 'A' and <= 'F';
    }

    public static DtcCode Parse(string input) =>
        TryParse(input, out var code) ? code : throw new FormatException($"'{input}' is not a valid DTC.");

    /// <summary>
    /// Parses a DTC. Accepts lowercase, embedded spaces ("P 0302"), and UDS failure-type
    /// suffixes ("U0100-87", "P0302:00"), which are stripped. A bare four-digit code
    /// ("0302") is interpreted as a P-code only when <paramref name="assumePowertrain"/> is set.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? input, out DtcCode code, bool assumePowertrain = false)
    {
        code = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var s = input.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        var suffix = s.IndexOfAny(['-', ':', '_']);
        if (suffix > 0) s = s[..suffix];

        if (assumePowertrain && s.Length == 4 && s.All(char.IsAsciiHexDigit) && s[0] is >= '0' and <= '3')
        {
            s = "P" + s;
        }

        if (!FullCodeRegex().IsMatch(s)) return false;
        code = new DtcCode(s);
        return true;
    }

    /// <summary>Extracts every distinct DTC mentioned in free text, preserving order.</summary>
    public static IReadOnlyList<DtcCode> ExtractAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var list = new List<DtcCode>();
        foreach (Match m in EmbeddedCodeRegex().Matches(text))
        {
            if (TryParse(m.Value, out var code) && !list.Contains(code)) list.Add(code);
        }

        return list;
    }

    [GeneratedRegex("^[PBCU][0-3][0-9A-F]{3}$")]
    private static partial Regex FullCodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[PBCUpbcu][0-3][0-9A-Fa-f]{3}(?![A-Za-z0-9])")]
    private static partial Regex EmbeddedCodeRegex();
}
