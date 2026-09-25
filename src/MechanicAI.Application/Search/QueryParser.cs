using System.Globalization;
using System.Text.RegularExpressions;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Search;

/// <summary>Structured interpretation of free text typed by the technician.</summary>
public sealed record ParsedQuery
{
    public required string Original { get; init; }

    public IReadOnlyList<string> Dtcs { get; init; } = [];

    public string? Vin { get; init; }

    public int? Year { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? Engine { get; init; }

    public decimal? DisplacementLiters { get; init; }

    public int? Mileage { get; init; }

    public IReadOnlyList<string> Symptoms { get; init; } = [];

    public IReadOnlyList<string> Conditions { get; init; } = [];

    public IReadOnlyList<string> Components { get; init; } = [];

    /// <summary>Intent scores (0..1), highest first.</summary>
    public IReadOnlyList<(SearchIntent Intent, double Score)> Intents { get; init; } = [];

    /// <summary>The query with vehicle/code tokens removed — what remains is the complaint or question.</summary>
    public string Remainder { get; init; } = string.Empty;

    public SearchIntent PrimaryIntent => Intents.Count > 0 ? Intents[0].Intent : SearchIntent.Web;

    public bool HasVehicle => Year is not null || Make is not null || Model is not null;

    public string VehicleDescription
    {
        get
        {
            var parts = new List<string>();
            if (Year is { } y) parts.Add(y.ToString(CultureInfo.InvariantCulture));
            if (Make is not null) parts.Add(Make);
            if (Model is not null) parts.Add(Model);
            if (Engine is not null) parts.Add(Engine);
            return string.Join(' ', parts);
        }
    }

    /// <summary>True when this looks like a request to diagnose a problem.</summary>
    public bool LooksLikeDiagnosis => Dtcs.Count > 0 || Symptoms.Count > 0;
}

/// <summary>Rule-based parser for vehicles, codes, symptoms, and intent in free text.</summary>
public static partial class QueryParser
{
    private static readonly (string Phrase, string Symptom)[] SymptomLexicon =
    [
        ("rough idle", "Rough idle"), ("idles rough", "Rough idle"), ("idle rough", "Rough idle"), ("shakes at idle", "Shakes at idle"),
        ("shake at idle", "Shakes at idle"), ("shaking", "Shaking / vibration"), ("shakes", "Shaking / vibration"), ("vibration", "Shaking / vibration"),
        ("misfire", "Misfire"), ("stumble", "Stumble / hesitation"), ("hesitat", "Stumble / hesitation"), ("bog", "Stumble / hesitation"),
        ("stall", "Stalling"), ("dies", "Stalling"), ("no start", "No start"), ("won't start", "No start"), ("wont start", "No start"),
        ("crank no start", "Cranks but no start"), ("cranks but", "Cranks but no start"), ("no crank", "No crank"), ("won't crank", "No crank"),
        ("hard start", "Hard start / long crank"), ("long crank", "Hard start / long crank"), ("overheat", "Overheating"), ("running hot", "Overheating"),
        ("check engine", "Check engine light"), ("cel", "Check engine light"), ("mil on", "Check engine light"), ("loss of power", "Loss of power"),
        ("lack of power", "Loss of power"), ("no power", "Loss of power"), ("limp mode", "Reduced power / limp mode"), ("reduced power", "Reduced power / limp mode"),
        ("poor fuel", "Poor fuel economy"), ("bad gas mileage", "Poor fuel economy"), ("knock", "Knocking noise"), ("ping", "Pinging / detonation"),
        ("tick", "Ticking noise"), ("squeal", "Squealing noise"), ("grind", "Grinding noise"), ("clunk", "Clunking noise"), ("whine", "Whining noise"),
        ("rattle", "Rattling noise"), ("hum", "Humming noise"), ("burning smell", "Burning smell"), ("fuel smell", "Fuel smell"), ("gas smell", "Fuel smell"),
        ("sweet smell", "Sweet (coolant) smell"), ("white smoke", "White smoke"), ("blue smoke", "Blue smoke"), ("black smoke", "Black smoke"),
        ("oil leak", "Oil leak"), ("coolant leak", "Coolant leak"), ("losing coolant", "Coolant loss"), ("transmission leak", "Transmission fluid leak"),
        ("abs light", "ABS warning light"), ("traction control light", "Traction control light"), ("airbag light", "Airbag / SRS light"), ("srs light", "Airbag / SRS light"),
        ("battery light", "Battery / charging light"), ("dead battery", "Dead battery"), ("battery drain", "Battery drain"), ("dim lights", "Dim lights"),
        ("a/c not cold", "A/C not cooling"), ("ac not cold", "A/C not cooling"), ("a/c not cooling", "A/C not cooling"), ("ac not cooling", "A/C not cooling"), ("blows warm", "A/C not cooling"),
        ("pulsat", "Brake pulsation"), ("soft pedal", "Soft brake pedal"), ("pulls", "Pulls to one side"), ("wander", "Wandering"),
        ("harsh shift", "Harsh shifting"), ("slip", "Transmission slipping"), ("delayed engagement", "Delayed engagement"), ("flare", "Shift flare"),
        ("no communication", "No communication with module"), ("sputter", "Sputtering"), ("surge", "Surging"), ("backfire", "Backfire"),
    ];

    private static readonly (string Phrase, string Condition)[] ConditionLexicon =
    [
        ("cold start", "Cold start"), ("when cold", "When cold"), ("cold", "When cold"), ("after warm", "After warm-up"), ("warmed up", "After warm-up"),
        ("warm", "When warm"), ("hot", "When hot"), ("at idle", "At idle"), ("idle", "At idle"), ("under load", "Under load"),
        ("accelerat", "During acceleration"), ("highway", "At highway speed"), ("cruis", "While cruising"), ("rain", "In rain / wet conditions"),
        ("wet", "In rain / wet conditions"), ("humid", "In humid conditions"), ("morning", "First start of the day"), ("after sitting", "After sitting"),
        ("intermittent", "Intermittent"), ("sometimes", "Intermittent"), ("braking", "While braking"), ("turning", "While turning"), ("bumps", "Over bumps"),
    ];

    private static readonly string[] ComponentLexicon =
    [
        "crankshaft position sensor", "camshaft position sensor", "crank sensor", "cam sensor", "abs module", "wheel speed sensor", "fuel pump",
        "fuel pump relay", "ignition coil", "spark plug", "fuel injector", "o2 sensor", "oxygen sensor", "air fuel ratio sensor", "a/f sensor",
        "maf sensor", "mass air flow", "map sensor", "throttle body", "pcv valve", "egr valve", "purge valve", "vent valve", "catalytic converter",
        "thermostat", "water pump", "radiator", "alternator", "starter", "battery", "bcm", "pcm", "ecm", "tcm", "knock sensor", "coolant temp sensor",
        "evap canister", "vvt solenoid", "cam phaser", "timing chain", "blower motor", "blend door", "a/c compressor", "compressor clutch",
        "brake caliper", "master cylinder", "tie rod", "ball joint", "control arm", "wheel bearing", "cv axle", "strut", "shock",
        "clockspring", "airbag module", "instrument cluster", "gateway module", "turbocharger", "wastegate", "intercooler", "high pressure fuel pump",
        "fuel rail pressure sensor", "transmission range sensor", "shift solenoid", "torque converter", "valve body", "ground strap", "fuse box",
    ];

    public static ParsedQuery Parse(string? input)
    {
        var original = (input ?? string.Empty).Trim();
        var lower = original.ToLowerInvariant();
        var remainder = original;

        // VIN
        string? vin = null;
        foreach (Match m in VinRegex().Matches(original.ToUpperInvariant()))
        {
            if (Vin.TryParse(m.Value, out var parsedVin))
            {
                vin = parsedVin.Value;
                remainder = RemoveToken(remainder, m.Value);
                break;
            }
        }

        // DTCs (with a bare 4-digit fallback when the whole query is just "0302")
        var dtcs = DtcCode.ExtractAll(original).Select(c => c.Value).ToList();
        foreach (var code in dtcs) remainder = RemoveToken(remainder, code);
        if (dtcs.Count == 0 && BareCodeRegex().Match(original) is { Success: true } bare &&
            DtcCode.TryParse(bare.Value, out var assumed, assumePowertrain: true))
        {
            dtcs.Add(assumed.Value);
            remainder = RemoveToken(remainder, bare.Value);
        }

        // Year
        int? year = null;
        var yearMatch = YearRegex().Match(original);
        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var y) && y >= 1981 && y <= DateTime.UtcNow.Year + 2)
        {
            year = y;
            remainder = RemoveToken(remainder, yearMatch.Value);
        }

        // Make and model (longest model names first so "grand cherokee" beats "cherokee")
        string? make = null;
        string? model = null;
        foreach (var (alias, canonical) in VehicleLexicon.MakeAliases.OrderByDescending(kv => kv.Key.Length))
        {
            if (WordMatch(lower, alias))
            {
                make = canonical;
                remainder = RemoveWord(remainder, alias);
                break;
            }
        }

        foreach (var (alias, value) in VehicleLexicon.Models.OrderByDescending(kv => kv.Key.Length))
        {
            if (alias.Length < 2) continue;
            if (WordMatch(lower, alias))
            {
                // Avoid treating a lone make word as a model ("ram" the brand vs "Ram 1500").
                if (make is not null && !string.Equals(make, value.Make, StringComparison.OrdinalIgnoreCase) && make != "Dodge") continue;
                model = value.Model;
                make ??= value.Make;
                remainder = RemoveWord(remainder, alias);
                break;
            }
        }

        if (make == "Ram" || (model?.StartsWith("Ram ", StringComparison.Ordinal) ?? false)) make = VehicleLexicon.ResolveRamMake(year);

        // Engine: displacement + family keywords
        decimal? displacement = null;
        string? engine = null;
        var dispMatch = DisplacementRegex().Match(lower);
        if (dispMatch.Success && decimal.TryParse(dispMatch.Groups["d"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var disp) && disp is >= 0.6m and <= 8.9m)
        {
            displacement = disp;
            engine = string.Create(CultureInfo.InvariantCulture, $"{disp:0.0}L");
            remainder = RemoveToken(remainder, dispMatch.Value.Trim());
        }

        foreach (var (key, family) in VehicleLexicon.EngineFamilies)
        {
            if (WordMatch(lower, key))
            {
                engine = engine is null ? family : $"{engine} {family}";
                remainder = RemoveWord(remainder, key);
                break;
            }
        }

        var cylMatch = CylinderRegex().Match(lower);
        if (cylMatch.Success)
        {
            var layout = cylMatch.Value.ToUpperInvariant();
            engine = engine is null ? layout : $"{engine} {layout}";
        }

        // Mileage
        int? mileage = null;
        var mileageMatch = MileageRegex().Match(lower);
        if (!mileageMatch.Success) mileageMatch = MileageKRegex().Match(lower);
        if (mileageMatch.Success)
        {
            var digits = mileageMatch.Groups["n"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                if (mileageMatch.Groups["k"].Success) n *= 1000;
                if (n is >= 10 and <= 2_000_000) mileage = (int)n;
            }
        }

        var symptoms = SymptomLexicon.Where(s => ContainsPhrase(lower, s.Phrase)).Select(s => s.Symptom).Distinct().ToList();
        var conditions = ConditionLexicon.Where(c => ContainsPhrase(lower, c.Phrase)).Select(c => c.Condition).Distinct().ToList();
        var components = ComponentLexicon.Where(c => lower.Contains(c, StringComparison.Ordinal)).ToList();

        var parsed = new ParsedQuery
        {
            Original = original,
            Dtcs = dtcs,
            Vin = vin,
            Year = year,
            Make = make,
            Model = model,
            Engine = engine,
            DisplacementLiters = displacement,
            Mileage = mileage,
            Symptoms = symptoms,
            Conditions = conditions,
            Components = components,
            Remainder = CleanRemainder(remainder),
        };

        return parsed with { Intents = IntentClassifier.Classify(parsed, lower) };
    }

    private static bool ContainsPhrase(string lower, string phrase) =>
        phrase.Length <= 4 ? WordMatch(lower, phrase) : lower.Contains(phrase, StringComparison.Ordinal);

    private static bool WordMatch(string lower, string word) =>
        Regex.IsMatch(lower, $@"(?<![a-z0-9]){Regex.Escape(word.ToLowerInvariant())}(?![a-z0-9])");

    private static string RemoveToken(string text, string token) =>
        Regex.Replace(text, Regex.Escape(token), " ", RegexOptions.IgnoreCase);

    private static string RemoveWord(string text, string word) =>
        Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(word)}(?![A-Za-z0-9])", " ", RegexOptions.IgnoreCase);

    private static string CleanRemainder(string text)
    {
        var s = Regex.Replace(text, @"\s+", " ").Trim();
        s = Regex.Replace(s, @"^(and|with|,|\.|-)+\s*", string.Empty, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(and|,)\s*(?=[\.\?!]|$)", string.Empty, RegexOptions.IgnoreCase);
        return s.Trim(' ', ',', '.', '-');
    }

    [GeneratedRegex(@"\b[A-HJ-NPR-Z0-9]{17}\b")]
    private static partial Regex VinRegex();

    [GeneratedRegex(@"^\s*0[0-9A-Fa-f]{3}\s*$")]
    private static partial Regex BareCodeRegex();

    [GeneratedRegex(@"(?<![0-9A-Za-z])(19[89][0-9]|20[0-4][0-9])(?![0-9A-Za-z])")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?<![0-9.])(?<d>[0-8]\.[0-9])\s*(?:l|liter|litre)?(?![0-9a-z])(?!\s*(?:v\b|volt|ohm|amp|a\b|psi|bar|kpa|%|ms\b|hz|g/s|mm|in\b|sec|second|hour|hr|min|gal|qt|quart))")]
    private static partial Regex DisplacementRegex();

    [GeneratedRegex(@"\b(v6|v8|v10|v12|i4|i6|i3|i5|l4|l6|v-6|v-8)\b")]
    private static partial Regex CylinderRegex();

    [GeneratedRegex(@"(?<n>\d{1,3}(?:,\d{3})+|\d+(?:\.\d+)?)\s*(?<k>k)?\s*(?:miles|mile|mi|km|kms|kilometers)\b")]
    private static partial Regex MileageRegex();

    [GeneratedRegex(@"(?<![\w.])(?<n>\d{2,3})(?<k>k)\b")]
    private static partial Regex MileageKRegex();
}
