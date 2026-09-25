using System.Text.RegularExpressions;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Diagnostics;

public enum SafetySeverity { Caution, Warning, Danger }

public sealed record SafetyWarning(string Tag, string Title, string Message, SafetySeverity Severity);

/// <summary>
/// Contextual safety guidance. Warnings are general safety practice; they direct the
/// technician to OEM procedures and never replace them.
/// </summary>
public static partial class SafetyAdvisor
{
    private static readonly Dictionary<string, SafetyWarning> Warnings = new(StringComparer.OrdinalIgnoreCase)
    {
        [SafetyTags.HighVoltage] = new(SafetyTags.HighVoltage, "High-voltage system",
            "Hybrid and EV high-voltage systems can cause fatal shock and arc-flash burns. Only technicians trained and certified for high-voltage work should service orange-cabled components. Follow the OEM high-voltage disable procedure, wear inspected class 0 insulating gloves with leather protectors, wait the OEM-specified time, and verify zero voltage with a CAT III/IV rated meter before touching any HV component. Do not improvise.",
            SafetySeverity.Danger),
        [SafetyTags.Airbag] = new(SafetyTags.Airbag, "Airbag / SRS",
            "Airbags and pretensioners can deploy with lethal force. Follow the OEM SRS disable procedure, disconnect the battery and wait the manufacturer-specified time before unplugging SRS components. Never probe squib circuits with a test light or ohmmeter — use OEM-approved load tools. Store live modules trim-side up, away from your body.",
            SafetySeverity.Danger),
        [SafetyTags.Fuel] = new(SafetyTags.Fuel, "Fuel system",
            "Fuel and vapors are highly flammable. Relieve fuel pressure using the OEM procedure before opening any fuel connection, eliminate ignition sources, keep a class B extinguisher within reach, and capture fuel in approved containers. Direct-injection high-pressure systems operate at pressures of thousands of psi — never loosen high-pressure fittings until pressure has been relieved per OEM instructions.",
            SafetySeverity.Warning),
        [SafetyTags.Lifting] = new(SafetyTags.Lifting, "Vehicle lifting",
            "Lift only at the manufacturer's specified lift points. Never work under a vehicle supported only by a jack — use rated jack stands on a solid, level surface and chock the wheels.",
            SafetySeverity.Warning),
        [SafetyTags.Rotating] = new(SafetyTags.Rotating, "Running engine / rotating parts",
            "Keep hands, tools, hair, and loose clothing clear of belts, pulleys, and fans. Electric cooling fans can start with the ignition off. For running tests set the parking brake, place the transmission in Park or Neutral, and chock the wheels.",
            SafetySeverity.Warning),
        [SafetyTags.Compressed] = new(SafetyTags.Compressed, "Stored energy",
            "Coil springs, struts, and pressurized components store significant energy. Use the correct spring compressor and OEM procedures; never unfasten a strut top nut with the spring loaded.",
            SafetySeverity.Warning),
        [SafetyTags.HighCurrent] = new(SafetyTags.HighCurrent, "High-current circuits",
            "Battery and starter circuits can deliver hundreds of amps: a shorted tool or ring can cause severe burns. Remove jewelry, wear eye protection, and disconnect the negative battery cable before working on starter or alternator output circuits. Battery gases are explosive.",
            SafetySeverity.Warning),
        [SafetyTags.Brakes] = new(SafetyTags.Brakes, "Brake system",
            "Brake work directly affects vehicle safety. Use OEM specifications for minimum thickness, runout, and torque; bleed using the OEM procedure (ABS units may require a scan tool); verify a firm pedal before moving the vehicle. Avoid breathing brake dust.",
            SafetySeverity.Warning),
        [SafetyTags.Refrigerant] = new(SafetyTags.Refrigerant, "A/C refrigerant",
            "In the US, refrigerant handling requires EPA Section 609 certification and approved recovery equipment — never vent refrigerant. Wear eye protection and gloves (frostbite risk). R-1234yf is mildly flammable: use equipment rated for it.",
            SafetySeverity.Warning),
        [SafetyTags.HotSurfaces] = new(SafetyTags.HotSurfaces, "Hot surfaces / pressurized coolant",
            "Never open a hot, pressurized cooling system — scalding coolant can erupt. Let the engine cool. Exhaust and turbocharger components can cause severe burns.",
            SafetySeverity.Caution),
        [SafetyTags.IgnitionVoltage] = new(SafetyTags.IgnitionVoltage, "Ignition high voltage",
            "Secondary ignition voltage can exceed tens of thousands of volts. Do not hold coils or plug wires while cranking; use insulated tools and a proper spark tester. People with implanted medical devices should avoid ignition testing.",
            SafetySeverity.Caution),
        [SafetyTags.ExhaustGas] = new(SafetyTags.ExhaustGas, "Exhaust gas",
            "Running engines produce carbon monoxide. Use exhaust extraction or work in a well-ventilated area.",
            SafetySeverity.Caution),
    };

    private static readonly (string Tag, Regex Pattern)[] Detectors =
    [
        (SafetyTags.HighVoltage, HighVoltageRegex()),
        (SafetyTags.Airbag, AirbagRegex()),
        (SafetyTags.Fuel, FuelRegex()),
        (SafetyTags.Lifting, LiftingRegex()),
        (SafetyTags.Rotating, RotatingRegex()),
        (SafetyTags.Compressed, CompressedRegex()),
        (SafetyTags.HighCurrent, HighCurrentRegex()),
        (SafetyTags.Brakes, BrakesRegex()),
        (SafetyTags.Refrigerant, RefrigerantRegex()),
        (SafetyTags.HotSurfaces, HotRegex()),
        (SafetyTags.IgnitionVoltage, IgnitionRegex()),
    ];

    public static SafetyWarning? Get(string tag) => Warnings.GetValueOrDefault(tag);

    public static IReadOnlyList<SafetyWarning> ForTags(IEnumerable<string> tags) =>
        tags.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Get)
            .OfType<SafetyWarning>()
            .OrderByDescending(w => w.Severity)
            .ToList();

    /// <summary>Detects safety-relevant topics in free text (procedures, AI answers, complaints).</summary>
    public static IReadOnlyList<string> DetectTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return Detectors.Where(d => d.Pattern.IsMatch(text)).Select(d => d.Tag).ToList();
    }

    public static IReadOnlyList<SafetyWarning> ForText(string? text) => ForTags(DetectTags(text));

    [GeneratedRegex(@"\b(high[- ]voltage|hv battery|hybrid battery|traction battery|orange cable|inverter|service disconnect|\bev\b|electric vehicle|p0a[0-9a-f]{2}|p0b[0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex HighVoltageRegex();

    [GeneratedRegex(@"\b(airbag|air bag|srs|clockspring|clock spring|pretensioner|squib|occupant classification|b00[0-9a-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex AirbagRegex();

    [GeneratedRegex(@"\b(fuel (line|rail|pressure|pump|filter|injector|tank|leak)|injector|evap|gasoline|relieve pressure)", RegexOptions.IgnoreCase)]
    private static partial Regex FuelRegex();

    [GeneratedRegex(@"\b(lift|jack stand|raise the vehicle|hoist|remove the wheel|under the vehicle)", RegexOptions.IgnoreCase)]
    private static partial Regex LiftingRegex();

    [GeneratedRegex(@"\b(engine running|running engine|idle|rev the engine|belt|pulley|cooling fan|drive shaft|driveshaft)", RegexOptions.IgnoreCase)]
    private static partial Regex RotatingRegex();

    [GeneratedRegex(@"\b(coil spring|strut|spring compressor|torsion bar|air suspension|accumulator)", RegexOptions.IgnoreCase)]
    private static partial Regex CompressedRegex();

    [GeneratedRegex(@"\b(battery (cable|terminal|test)|starter|alternator|b\+ cable|jump start|load test)", RegexOptions.IgnoreCase)]
    private static partial Regex HighCurrentRegex();

    [GeneratedRegex(@"\b(brake|caliper|rotor|master cylinder|abs modulator|bleed)", RegexOptions.IgnoreCase)]
    private static partial Regex BrakesRegex();

    [GeneratedRegex(@"\b(refrigerant|r-?134a|r-?1234yf|a/c recharge|ac recharge|recover the system|evacuate)", RegexOptions.IgnoreCase)]
    private static partial Regex RefrigerantRegex();

    [GeneratedRegex(@"\b(radiator cap|coolant reservoir|overheat|hot coolant|exhaust manifold|turbocharger)", RegexOptions.IgnoreCase)]
    private static partial Regex HotRegex();

    [GeneratedRegex(@"\b(spark plug wire|ignition coil|secondary ignition|spark test|coil pack)", RegexOptions.IgnoreCase)]
    private static partial Regex IgnitionRegex();
}
