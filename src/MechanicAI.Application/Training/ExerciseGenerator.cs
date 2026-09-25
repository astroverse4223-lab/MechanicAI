using System.Globalization;
using System.Text.RegularExpressions;

namespace MechanicAI.Application.Training;

public sealed partial record Exercise(
    string Key,
    string Type,
    string Prompt,
    double? NumericAnswer,
    string? Unit,
    double TolerancePercent,
    IReadOnlyList<string> Choices,
    int? CorrectChoice,
    string Explanation)
{
    public bool IsMultipleChoice => Choices.Count > 0;

    public bool Check(string response)
    {
        if (IsMultipleChoice) return int.TryParse(response, out var i) && i == CorrectChoice;
        // Take the first number in the response so units such as "lb-ft" or "N·m" don't break parsing.
        var cleaned = NumberRegex().Match(response).Value.Replace(",", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || NumericAnswer is not { } answer) return false;
        var tolerance = Math.Max(Math.Abs(answer) * TolerancePercent / 100, 0.051);
        return Math.Abs(value - answer) <= tolerance;
    }

    [GeneratedRegex(@"-?(?:\d[\d,]*(?:\.\d+)?|\.\d+)")]
    private static partial Regex NumberRegex();
}

/// <summary>
/// Generates practice problems with computed answers (Ohm's law, resistance networks,
/// power, voltage drop, fuel trim, unit conversions). Values are illustrative practice
/// numbers, not specifications for any vehicle.
/// </summary>
public static class ExerciseGenerator
{
    public static readonly IReadOnlyList<string> Types =
        ["ohms-law", "series-resistance", "parallel-resistance", "power-law", "voltage-drop", "fuel-trim", "unit-pressure", "unit-torque", "unit-temperature"];

    public static string Describe(string type) => type switch
    {
        "ohms-law" => "Ohm's law",
        "series-resistance" => "Series resistance",
        "parallel-resistance" => "Parallel resistance",
        "power-law" => "Electrical power",
        "voltage-drop" => "Voltage drop",
        "fuel-trim" => "Fuel trim interpretation",
        "unit-pressure" => "Pressure conversion",
        "unit-torque" => "Torque conversion",
        "unit-temperature" => "Temperature conversion",
        _ => type,
    };

    public static IReadOnlyList<Exercise> Generate(IEnumerable<string> types, int count, int seed)
    {
        var list = types.Where(Types.Contains).ToList();
        if (list.Count == 0) list = Types.ToList();
        var random = new Random(seed);
        var exercises = new List<Exercise>(count);
        for (var i = 0; i < count; i++)
        {
            var type = list[i % list.Count];
            exercises.Add(Create(type, random, $"{type}-{seed}-{i}"));
        }

        return exercises;
    }

    public static Exercise Create(string type, Random random, string key)
    {
        string F(double v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture);

        // Signed percentage; negative zero (from rounding) is shown as +0.0 rather than "-+0.0".
        string Signed(double v) => (v == 0 ? 0.0 : v).ToString("+0.0;-0.0", CultureInfo.InvariantCulture);

        switch (type)
        {
            case "ohms-law":
            {
                var volts = Math.Round(11.5 + random.NextDouble() * 3, 1);
                var ohms = Math.Round(0.5 + random.NextDouble() * 24, 1);
                var amps = volts / ohms;
                return random.Next(3) switch
                {
                    0 => new Exercise(key, type, $"A load with {F(ohms)} Ω of resistance has {F(volts)} V applied. How much current flows (amps)?",
                        Math.Round(amps, 2), "A", 3, [], null, $"I = V ÷ R = {F(volts)} ÷ {F(ohms)} = {F(amps, "0.00")} A."),
                    1 => new Exercise(key, type, $"{F(amps, "0.00")} A flows through a {F(ohms)} Ω load. What voltage is across the load?",
                        Math.Round(volts, 2), "V", 3, [], null, $"V = I × R = {F(amps, "0.00")} × {F(ohms)} = {F(volts)} V."),
                    _ => new Exercise(key, type, $"A load draws {F(amps, "0.00")} A with {F(volts)} V applied. What is its resistance (ohms)?",
                        Math.Round(ohms, 2), "Ω", 3, [], null, $"R = V ÷ I = {F(volts)} ÷ {F(amps, "0.00")} = {F(ohms)} Ω."),
                };
            }

            case "series-resistance":
            {
                var n = random.Next(2, 5);
                var values = Enumerable.Range(0, n).Select(_ => Math.Round(1 + random.NextDouble() * 99, 0)).ToList();
                var total = values.Sum();
                return new Exercise(key, type, $"Resistors of {string.Join(", ", values.Select(v => F(v) + " Ω"))} are connected in series. What is the total resistance?",
                    total, "Ω", 1, [], null, $"In series, resistances add: {string.Join(" + ", values.Select(v => F(v)))} = {F(total)} Ω.");
            }

            case "parallel-resistance":
            {
                var n = random.Next(2, 4);
                var values = Enumerable.Range(0, n).Select(_ => (double)(new[] { 60, 100, 120, 150, 220, 330, 470, 1000 })[random.Next(8)]).ToList();
                var total = 1 / values.Sum(v => 1 / v);
                return new Exercise(key, type, $"Resistors of {string.Join(", ", values.Select(v => F(v) + " Ω"))} are connected in parallel. What is the total resistance?",
                    Math.Round(total, 1), "Ω", 2, [], null,
                    $"1/Rt = {string.Join(" + ", values.Select(v => "1/" + F(v)))} → Rt = {F(total, "0.0")} Ω. (Two equal resistors in parallel give half the value — e.g. two 120 Ω CAN terminators measure about 60 Ω.)");
            }

            case "power-law":
            {
                var volts = Math.Round(12 + random.NextDouble() * 2.4, 1);
                var amps = Math.Round(1 + random.NextDouble() * 29, 1);
                var watts = volts * amps;
                return new Exercise(key, type, $"A blower motor draws {F(amps)} A at {F(volts)} V. How much power does it consume (watts)?",
                    Math.Round(watts, 0), "W", 2, [], null, $"P = V × I = {F(volts)} × {F(amps)} = {F(watts, "0")} W.");
            }

            case "voltage-drop":
            {
                var source = Math.Round(12.4 + random.NextDouble() * 0.4, 2);
                var drop = Math.Round(0.05 + random.NextDouble() * 1.2, 2);
                var atLoad = source - drop;
                var excessive = drop > 0.5;
                return new Exercise(key, type,
                    $"With the circuit operating, battery positive reads {F(source, "0.00")} V and the load's power input reads {F(atLoad, "0.00")} V (both to battery negative). " +
                    "Using the general guideline that a power-side drop above roughly 0.5 V under load warrants investigation (verify against OEM specification), what should you conclude?",
                    null, null, 0,
                    ["The drop is within the general guideline — look elsewhere", "The drop exceeds the general guideline — look for resistance in the power side", "Replace the load", "Voltage drop cannot be measured on a live circuit"],
                    excessive ? 1 : 0,
                    $"Voltage drop = {F(source, "0.00")} − {F(atLoad, "0.00")} = {F(drop, "0.00")} V. {(excessive ? "That exceeds the general guideline, so measure across connections, switches, and fuses on the power side to find the resistance." : "That is within the general guideline, so the power-side wiring is not the likely problem.")} Voltage drop must be measured with current flowing.");
            }

            case "fuel-trim":
            {
                var stft = Math.Round(-12 + random.NextDouble() * 30, 1);
                var ltft = Math.Round(-10 + random.NextDouble() * 28, 1);
                var total = stft + ltft;
                var choice = total switch
                {
                    > 10 => 0,
                    < -10 => 1,
                    _ => 2,
                };
                return new Exercise(key, type,
                    $"At a warm idle in closed loop: STFT {Signed(stft)}%, LTFT {Signed(ltft)}%. What is the PCM doing? (General guideline: total trim beyond roughly ±10% warrants investigation — verify against OEM specification.)",
                    null, null, 0,
                    ["Adding significant fuel — the engine was running lean", "Removing significant fuel — the engine was running rich", "Trims are within the general guideline — no strong correction", "Fuel trim cannot be interpreted at idle"],
                    choice,
                    $"Total trim = STFT + LTFT = {Signed(total)}%. Positive trim means the PCM is adding fuel to correct a lean condition; negative means it is removing fuel to correct a rich condition. Compare idle vs. 2,500 rpm to separate vacuum leaks (improve with RPM) from fuel delivery problems (worsen with load).");
            }

            case "unit-pressure":
            {
                var psi = Math.Round(5 + random.NextDouble() * 70, 1);
                var kpa = psi * 6.894757;
                return random.Next(2) == 0
                    ? new Exercise(key, type, $"Convert {F(psi)} psi to kPa.", Math.Round(kpa, 1), "kPa", 1, [], null, $"1 psi ≈ 6.895 kPa → {F(psi)} × 6.895 ≈ {F(kpa, "0.0")} kPa.")
                    : new Exercise(key, type, $"Convert {F(kpa, "0")} kPa to psi.", Math.Round(Math.Round(kpa) / 6.894757, 1), "psi", 1, [], null,
                        $"1 kPa ≈ 0.145 psi → {F(kpa, "0")} × 0.145 ≈ {F(Math.Round(kpa) / 6.894757, "0.0")} psi.");
            }

            case "unit-torque":
            {
                var nm = Math.Round(10 + random.NextDouble() * 190, 0);
                var lbft = nm * 0.737562;
                return random.Next(2) == 0
                    ? new Exercise(key, type, $"Convert {F(nm)} N·m to lb-ft.", Math.Round(lbft, 1), "lb-ft", 1, [], null, $"1 N·m ≈ 0.7376 lb-ft → {F(nm)} × 0.7376 ≈ {F(lbft, "0.0")} lb-ft.")
                    : new Exercise(key, type, $"Convert {F(Math.Round(lbft))} lb-ft to N·m.", Math.Round(Math.Round(lbft) / 0.737562, 1), "N·m", 1, [], null,
                        $"1 lb-ft ≈ 1.356 N·m → {F(Math.Round(lbft))} × 1.356 ≈ {F(Math.Round(lbft) / 0.737562, "0.0")} N·m.");
            }

            default:
            {
                var c = Math.Round(-20 + random.NextDouble() * 140, 0);
                var f = c * 9 / 5 + 32;
                return random.Next(2) == 0
                    ? new Exercise(key, "unit-temperature", $"Convert {F(c)} °C to °F.", Math.Round(f, 1), "°F", 1, [], null, $"°F = °C × 9/5 + 32 = {F(c)} × 1.8 + 32 = {F(f, "0.0")} °F.")
                    : new Exercise(key, "unit-temperature", $"Convert {F(Math.Round(f))} °F to °C.", Math.Round((Math.Round(f) - 32) * 5 / 9, 1), "°C", 1, [], null,
                        $"°C = (°F − 32) × 5/9 = ({F(Math.Round(f))} − 32) × 5/9 = {F((Math.Round(f) - 32) * 5 / 9, "0.0")} °C.");
            }
        }
    }
}
