namespace MechanicAI.Application.LiveData;

public enum PidKind { Rpm, Speed, Temperature, Percent, FuelTrim, Pressure, Airflow, Voltage, Timing, Lambda, Current, Time, Rate }

/// <summary>A standard SAE J1979 Mode 01 parameter and its decoding formula.</summary>
public sealed record ObdPid(
    string Key,
    byte Pid,
    string Name,
    string Unit,
    int DataBytes,
    PidKind Kind,
    double Min,
    double Max,
    Func<byte[], double> Decode,
    string Group);

/// <summary>
/// SAE J1979 Mode 01 PID definitions. Formulas are the standard ones from the J1979 /
/// ISO 15031-5 specification; they are generic and do not depend on the vehicle.
/// Oxygen-sensor PIDs assume the common "bank/sensor" layout reported by PID 0x13.
/// </summary>
public static class ObdPids
{
    private static double A(byte[] d) => d.Length > 0 ? d[0] : 0;

    private static double B(byte[] d) => d.Length > 1 ? d[1] : 0;

    private static double C(byte[] d) => d.Length > 2 ? d[2] : 0;

    private static double D(byte[] d) => d.Length > 3 ? d[3] : 0;

    private static double Word(byte[] d) => 256 * A(d) + B(d);

    public static readonly IReadOnlyList<ObdPid> All =
    [
        new("LOAD", 0x04, "Calculated engine load", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Engine"),
        new("ECT", 0x05, "Coolant temperature", "°C", 1, PidKind.Temperature, -40, 215, d => A(d) - 40, "Engine"),
        new("STFT1", 0x06, "Short-term fuel trim — Bank 1", "%", 1, PidKind.FuelTrim, -100, 99.2, d => (A(d) - 128) * 100 / 128, "Fuel"),
        new("LTFT1", 0x07, "Long-term fuel trim — Bank 1", "%", 1, PidKind.FuelTrim, -100, 99.2, d => (A(d) - 128) * 100 / 128, "Fuel"),
        new("STFT2", 0x08, "Short-term fuel trim — Bank 2", "%", 1, PidKind.FuelTrim, -100, 99.2, d => (A(d) - 128) * 100 / 128, "Fuel"),
        new("LTFT2", 0x09, "Long-term fuel trim — Bank 2", "%", 1, PidKind.FuelTrim, -100, 99.2, d => (A(d) - 128) * 100 / 128, "Fuel"),
        new("FP", 0x0A, "Fuel pressure (gauge)", "kPa", 1, PidKind.Pressure, 0, 765, d => 3 * A(d), "Fuel"),
        new("MAP", 0x0B, "Intake manifold absolute pressure", "kPa", 1, PidKind.Pressure, 0, 255, d => A(d), "Air"),
        new("RPM", 0x0C, "Engine speed", "rpm", 2, PidKind.Rpm, 0, 8000, d => Word(d) / 4, "Engine"),
        new("SPEED", 0x0D, "Vehicle speed", "km/h", 1, PidKind.Speed, 0, 255, d => A(d), "Vehicle"),
        new("TIMING", 0x0E, "Ignition timing advance (cyl. 1)", "°", 1, PidKind.Timing, -64, 63.5, d => A(d) / 2 - 64, "Ignition"),
        new("IAT", 0x0F, "Intake air temperature", "°C", 1, PidKind.Temperature, -40, 215, d => A(d) - 40, "Air"),
        new("MAF", 0x10, "Mass air flow", "g/s", 2, PidKind.Airflow, 0, 655.35, d => Word(d) / 100, "Air"),
        new("TPS", 0x11, "Throttle position", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Air"),
        new("O2S1B1", 0x14, "O2 sensor B1S1 voltage", "V", 2, PidKind.Voltage, 0, 1.275, d => A(d) / 200, "Oxygen sensors"),
        new("O2S2B1", 0x15, "O2 sensor B1S2 voltage", "V", 2, PidKind.Voltage, 0, 1.275, d => A(d) / 200, "Oxygen sensors"),
        new("O2S1B2", 0x18, "O2 sensor B2S1 voltage", "V", 2, PidKind.Voltage, 0, 1.275, d => A(d) / 200, "Oxygen sensors"),
        new("O2S2B2", 0x19, "O2 sensor B2S2 voltage", "V", 2, PidKind.Voltage, 0, 1.275, d => A(d) / 200, "Oxygen sensors"),
        new("RUNTIME", 0x1F, "Run time since engine start", "s", 2, PidKind.Time, 0, 65535, Word, "Engine"),
        new("FRP_VAC", 0x22, "Fuel rail pressure (relative to vacuum)", "kPa", 2, PidKind.Pressure, 0, 5177, d => 0.079 * Word(d), "Fuel"),
        new("FRP", 0x23, "Fuel rail pressure (gauge, DI/diesel)", "kPa", 2, PidKind.Pressure, 0, 655350, d => 10 * Word(d), "Fuel"),
        new("AFR_B1S1", 0x34, "A/F sensor B1S1 equivalence ratio (λ)", "λ", 4, PidKind.Lambda, 0, 2, d => Word(d) / 32768, "Oxygen sensors"),
        new("AFR_B1S1_mA", 0x34, "A/F sensor B1S1 current", "mA", 4, PidKind.Current, -128, 128, d => (256 * C(d) + D(d)) / 256 - 128, "Oxygen sensors"),
        new("AFR_B2S1", 0x38, "A/F sensor B2S1 equivalence ratio (λ)", "λ", 4, PidKind.Lambda, 0, 2, d => Word(d) / 32768, "Oxygen sensors"),
        new("EGR_CMD", 0x2C, "Commanded EGR", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Emissions"),
        new("EVAP_PURGE", 0x2E, "Commanded evaporative purge", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Emissions"),
        new("FUEL_LVL", 0x2F, "Fuel tank level", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Fuel"),
        new("BARO", 0x33, "Barometric pressure", "kPa", 1, PidKind.Pressure, 0, 255, d => A(d), "Air"),
        new("CAT_B1S1", 0x3C, "Catalyst temperature B1S1", "°C", 2, PidKind.Temperature, -40, 6514, d => Word(d) / 10 - 40, "Emissions"),
        new("VPWR", 0x42, "Control module voltage", "V", 2, PidKind.Voltage, 0, 65.535, d => Word(d) / 1000, "Electrical"),
        new("ABS_LOAD", 0x43, "Absolute load", "%", 2, PidKind.Percent, 0, 25700, d => Word(d) * 100 / 255, "Engine"),
        new("LAMBDA_CMD", 0x44, "Commanded equivalence ratio (λ)", "λ", 2, PidKind.Lambda, 0, 2, d => Word(d) / 32768, "Fuel"),
        new("TPS_REL", 0x45, "Relative throttle position", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Air"),
        new("AAT", 0x46, "Ambient air temperature", "°C", 1, PidKind.Temperature, -40, 215, d => A(d) - 40, "Air"),
        new("APP_D", 0x49, "Accelerator pedal position D", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Air"),
        new("TAC_CMD", 0x4C, "Commanded throttle actuator", "%", 1, PidKind.Percent, 0, 100, d => A(d) * 100 / 255, "Air"),
        new("EOT", 0x5C, "Engine oil temperature", "°C", 1, PidKind.Temperature, -40, 210, d => A(d) - 40, "Engine"),
        new("FUEL_RATE", 0x5E, "Engine fuel rate", "L/h", 2, PidKind.Rate, 0, 3276.75, d => Word(d) / 20, "Fuel"),
    ];

    private static readonly Dictionary<string, ObdPid> ByKey = All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static ObdPid? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IEnumerable<ObdPid> ForPid(byte pid) => All.Where(p => p.Pid == pid);

    /// <summary>Decodes a "supported PIDs" bitmap response (PIDs 0x00, 0x20, 0x40…).</summary>
    public static IEnumerable<byte> DecodeSupportBitmap(byte basePid, byte[] data)
    {
        for (var i = 0; i < 32 && i / 8 < data.Length; i++)
        {
            if ((data[i / 8] & (0x80 >> (i % 8))) != 0) yield return (byte)(basePid + i + 1);
        }
    }

    /// <summary>Decodes a two-byte DTC per SAE J2012: first two bits select P/C/B/U.</summary>
    public static string DecodeDtc(byte high, byte low)
    {
        var letter = (high >> 6) switch
        {
            0 => 'P',
            1 => 'C',
            2 => 'B',
            _ => 'U',
        };
        var digit2 = (high >> 4) & 0x03;
        return $"{letter}{digit2}{high & 0x0F:X1}{low:X2}";
    }
}
