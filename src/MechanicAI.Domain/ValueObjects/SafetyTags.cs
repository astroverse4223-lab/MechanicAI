namespace MechanicAI.Domain.ValueObjects;

/// <summary>
/// Canonical safety tag vocabulary used across content, diagnostic tests, and AI output.
/// Tags are strings so content files can reference them directly.
/// </summary>
public static class SafetyTags
{
    public const string Airbag = "airbag";
    public const string Fuel = "fuel";
    public const string HighVoltage = "high-voltage";
    public const string Lifting = "lifting";
    public const string Rotating = "rotating";
    public const string Compressed = "compressed";
    public const string HighCurrent = "high-current";
    public const string Brakes = "brakes";
    public const string Refrigerant = "refrigerant";
    public const string HotSurfaces = "hot-surfaces";
    public const string IgnitionVoltage = "ignition-voltage";
    public const string ExhaustGas = "exhaust-gas";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Airbag, Fuel, HighVoltage, Lifting, Rotating, Compressed, HighCurrent, Brakes,
        Refrigerant, HotSurfaces, IgnitionVoltage, ExhaustGas,
    };

    public static bool IsValid(string tag) => All.Contains(tag);
}
