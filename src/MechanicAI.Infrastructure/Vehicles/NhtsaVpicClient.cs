using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Settings;
using MechanicAI.Infrastructure.Http;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Vehicles;

/// <summary>
/// NHTSA Product Information Catalog (vPIC) client: VIN decoding and model lists.
/// Decodes are cached; when offline, a cached decode is returned and flagged as such.
/// Missing values mean NHTSA has no data for that field — they are left blank, never guessed.
/// </summary>
public sealed partial class NhtsaVpicClient(
    IHttpClientFactory httpFactory,
    IResponseCache cache,
    ISettingsStore settings,
    ILogger<NhtsaVpicClient> logger) : IVinDecoder, IVehicleCatalog
{
    public const string HttpClientName = "nhtsa";
    private const string Service = "NHTSA vPIC";

    private static readonly (string Category, string Key, string Label)[] FieldMap =
    [
        ("Identity", "Make", "Make"), ("Identity", "Manufacturer", "Manufacturer"), ("Identity", "Model", "Model"),
        ("Identity", "ModelYear", "Model year"), ("Identity", "Series", "Series"), ("Identity", "Series2", "Series (2)"),
        ("Identity", "Trim", "Trim"), ("Identity", "Trim2", "Trim (2)"), ("Identity", "VehicleType", "Vehicle type"),
        ("Identity", "BodyClass", "Body class"), ("Identity", "BodyCabType", "Cab type"), ("Identity", "Doors", "Doors"),
        ("Identity", "PlantCity", "Plant city"), ("Identity", "PlantState", "Plant state"), ("Identity", "PlantCountry", "Plant country"),
        ("Engine", "DisplacementL", "Displacement (L)"), ("Engine", "DisplacementCC", "Displacement (cc)"),
        ("Engine", "EngineCylinders", "Cylinders"), ("Engine", "EngineConfiguration", "Configuration"),
        ("Engine", "EngineModel", "Engine model"), ("Engine", "EngineManufacturer", "Engine manufacturer"),
        ("Engine", "EngineHP", "Horsepower"), ("Engine", "EngineKW", "Power (kW)"), ("Engine", "FuelTypePrimary", "Primary fuel"),
        ("Engine", "FuelTypeSecondary", "Secondary fuel"), ("Engine", "FuelInjectionType", "Fuel injection"),
        ("Engine", "Turbo", "Turbocharged"), ("Engine", "ValveTrainDesign", "Valve train"), ("Engine", "EngineCycles", "Engine cycles"),
        ("Engine", "CoolingType", "Cooling"), ("Engine", "OtherEngineInfo", "Other engine info"),
        ("Electrification", "ElectrificationLevel", "Electrification"), ("Electrification", "BatteryType", "Battery type"),
        ("Electrification", "BatteryKWh", "Battery capacity (kWh)"), ("Electrification", "BatteryV", "Battery voltage"),
        ("Electrification", "ChargerLevel", "Charger level"), ("Electrification", "ChargerPowerKW", "Charger power (kW)"),
        ("Electrification", "EVDriveUnit", "EV drive unit"),
        ("Drivetrain", "DriveType", "Drive type"), ("Drivetrain", "TransmissionStyle", "Transmission"),
        ("Drivetrain", "TransmissionSpeeds", "Transmission speeds"), ("Drivetrain", "Axles", "Axles"),
        ("Chassis", "BrakeSystemType", "Brake system"), ("Chassis", "BrakeSystemDesc", "Brake description"), ("Chassis", "GVWR", "GVWR"),
        ("Chassis", "WheelBaseShort", "Wheelbase (in)"), ("Chassis", "TrackWidth", "Track width (in)"),
        ("Chassis", "WheelSizeFront", "Front wheel size (in)"), ("Chassis", "WheelSizeRear", "Rear wheel size (in)"),
        ("Chassis", "SteeringLocation", "Steering location"), ("Chassis", "BedLengthIN", "Bed length (in)"),
        ("Safety", "AirBagLocFront", "Front airbags"), ("Safety", "AirBagLocSide", "Side airbags"), ("Safety", "AirBagLocCurtain", "Curtain airbags"),
        ("Safety", "AirBagLocKnee", "Knee airbags"), ("Safety", "Pretensioner", "Pretensioners"), ("Safety", "SeatBeltsAll", "Seat belts"),
        ("Safety", "TPMS", "TPMS"), ("Safety", "ABS", "ABS"), ("Safety", "ESC", "Electronic stability control"),
        ("Safety", "TractionControl", "Traction control"), ("Safety", "AdaptiveCruiseControl", "Adaptive cruise control"),
        ("Safety", "ForwardCollisionWarning", "Forward collision warning"), ("Safety", "LaneDepartureWarning", "Lane departure warning"),
        ("Safety", "LaneKeepSystem", "Lane keeping assist"), ("Safety", "BlindSpotMon", "Blind spot monitoring"),
        ("Safety", "RearVisibilitySystem", "Backup camera"), ("Safety", "PedestrianAutomaticEmergencyBraking", "Pedestrian AEB"),
        ("Safety", "CIB", "Crash imminent braking"), ("Safety", "ParkAssist", "Park assist"),
    ];

    public async Task<Sourced<VinDecodeResult>> DecodeAsync(string vin, int? modelYear, CancellationToken cancellationToken)
    {
        var s = settings.Current.VehicleData;
        var url = $"{s.VpicBaseUrl.TrimEnd('/')}/vehicles/DecodeVinValuesExtended/{Uri.EscapeDataString(vin)}?format=json" +
                  (modelYear is { } y ? $"&modelyear={y}" : string.Empty);
        var cacheKey = $"vpic:decode:{vin}:{modelYear}";

        var cached = await cache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null && cached.IsFresh(DateTime.UtcNow))
        {
            return new Sourced<VinDecodeResult>(Parse(cached.Payload, vin), "NHTSA vPIC VIN decoder", url, cached.RetrievedUtc, FromCache: true);
        }

        string json;
        try
        {
            json = await HttpCall.GetStringAsync(httpFactory.CreateClient(HttpClientName), url, Service, cancellationToken);
        }
        catch (ExternalServiceException ex) when (cached is not null)
        {
            logger.LogInformation("vPIC unavailable ({Kind}); serving cached decode from {Retrieved}", ex.Kind, cached.RetrievedUtc);
            return new Sourced<VinDecodeResult>(Parse(cached.Payload, vin), "NHTSA vPIC VIN decoder", url, cached.RetrievedUtc, FromCache: true);
        }

        var result = Parse(json, vin);
        if (result.IsUsable)
        {
            await cache.SetAsync(cacheKey, "nhtsa-vpic", json, TimeSpan.FromDays(Math.Max(1, s.CacheDays)), cancellationToken);
        }

        return new Sourced<VinDecodeResult>(result, "NHTSA vPIC VIN decoder", url, DateTime.UtcNow, FromCache: false);
    }

    public async Task<Sourced<IReadOnlyList<string>>> GetModelsAsync(string make, int modelYear, CancellationToken cancellationToken)
    {
        var s = settings.Current.VehicleData;
        var url = $"{s.VpicBaseUrl.TrimEnd('/')}/vehicles/GetModelsForMakeYear/make/{Uri.EscapeDataString(make)}/modelyear/{modelYear}?format=json";
        var cacheKey = $"vpic:models:{make.ToLowerInvariant()}:{modelYear}";
        var cached = await cache.GetAsync(cacheKey, cancellationToken);
        if (cached is not null && cached.IsFresh(DateTime.UtcNow))
        {
            return new Sourced<IReadOnlyList<string>>(ParseModels(cached.Payload), "NHTSA vPIC", url, cached.RetrievedUtc, true);
        }

        var json = await HttpCall.GetStringAsync(httpFactory.CreateClient(HttpClientName), url, Service, cancellationToken);
        await cache.SetAsync(cacheKey, "nhtsa-vpic", json, TimeSpan.FromDays(30), cancellationToken);
        return new Sourced<IReadOnlyList<string>>(ParseModels(json), "NHTSA vPIC", url, DateTime.UtcNow, false);
    }

    internal static VinDecodeResult Parse(string json, string vin)
    {
        using var doc = HttpCall.ParseDocument(json, Service);
        if (!doc.RootElement.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            throw new ExternalServiceException(Service, ErrorKind.InvalidResponse, "NHTSA vPIC returned no decode results.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in results[0].EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null,
            };
            if (IsMeaningful(value)) values[property.Name] = value!.Trim();
        }

        string? Get(string key) => values.GetValueOrDefault(key);

        var fields = FieldMap
            .Where(f => values.ContainsKey(f.Key))
            .Select(f => new DecodedField(f.Category, f.Label, Clean(f.Key, values[f.Key]), f.Key))
            .ToList();

        var warnings = new List<string>();
        var errorCode = Get("ErrorCode");
        var errorText = Get("ErrorText");
        if (errorCode is not null && errorCode != "0" && errorText is not null)
        {
            warnings.AddRange(errorText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !t.StartsWith("0 ", StringComparison.Ordinal)));
        }

        if (Get("AdditionalErrorText") is { } extra) warnings.Add(extra);

        int? year = int.TryParse(Get("ModelYear"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
        decimal? displacement = decimal.TryParse(Get("DisplacementL"), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? Math.Round(d, 1)
            : null;
        int? cylinders = int.TryParse(Get("EngineCylinders"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : null;

        var model = Get("Model");
        var series = Get("Series");
        if (model is not null && series is not null && SeriesSuffixRegex().IsMatch(series) &&
            !model.Contains(series, StringComparison.OrdinalIgnoreCase))
        {
            // vPIC reports e.g. Model "Silverado" + Series "1500"; the conventional name is "Silverado 1500".
            model = $"{model} {series}";
        }

        return new VinDecodeResult
        {
            Vin = vin,
            ModelYear = year,
            Make = Get("Make"),
            Model = model,
            Trim = Get("Trim"),
            Series = series,
            EngineDescription = DescribeEngine(displacement, cylinders, Get("EngineConfiguration"), Get("EngineModel"), Get("FuelTypePrimary"), Get("Turbo")),
            DisplacementLiters = displacement,
            Cylinders = cylinders,
            FuelType = Get("FuelTypePrimary"),
            Drivetrain = NormalizeDrive(Get("DriveType")),
            Transmission = DescribeTransmission(Get("TransmissionStyle"), Get("TransmissionSpeeds")),
            BodyClass = Get("BodyClass"),
            VehicleType = Get("VehicleType"),
            Manufacturer = Get("Manufacturer"),
            PlantCountry = Get("PlantCountry"),
            ErrorCode = errorCode,
            ErrorText = errorText,
            Warnings = warnings,
            Fields = fields,
        };
    }

    private static IReadOnlyList<string> ParseModels(string json)
    {
        using var doc = HttpCall.ParseDocument(json, Service);
        if (!doc.RootElement.TryGetProperty("Results", out var results)) return [];
        return results.EnumerateArray()
            .Select(r => r.TryGetProperty("Model_Name", out var n) ? n.GetString() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMeaningful(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not ("Not Applicable" or "null" or "0" or "0.0");

    private static string Clean(string key, string value) => key switch
    {
        "DisplacementCC" or "DisplacementL" or "EngineHP" or "EngineKW" =>
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n.ToString("0.##", CultureInfo.InvariantCulture) : value,
        _ => value,
    };

    internal static string? DescribeEngine(decimal? liters, int? cylinders, string? configuration, string? engineModel, string? fuel, string? turbo)
    {
        var parts = new List<string>();
        if (liters is { } l) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{l:0.0}L"));
        if (cylinders is { } cyl)
        {
            var layout = configuration switch
            {
                not null when configuration.StartsWith("V", StringComparison.OrdinalIgnoreCase) => "V",
                not null when configuration.StartsWith("In-Line", StringComparison.OrdinalIgnoreCase) => "I",
                not null when configuration.StartsWith("Horizontally", StringComparison.OrdinalIgnoreCase) => "H",
                not null when configuration.StartsWith("W", StringComparison.OrdinalIgnoreCase) => "W",
                _ => null,
            };
            parts.Add(layout is null ? $"{cyl}-cyl" : $"{layout}{cyl}");
        }

        if (string.Equals(turbo, "Yes", StringComparison.OrdinalIgnoreCase)) parts.Add("Turbo");
        if (fuel is not null && !fuel.Equals("Gasoline", StringComparison.OrdinalIgnoreCase)) parts.Add(fuel);
        var head = string.Join(' ', parts);
        if (!string.IsNullOrWhiteSpace(engineModel)) head = head.Length == 0 ? engineModel : $"{head} ({engineModel})";
        return head.Length == 0 ? null : head;
    }

    private static string? NormalizeDrive(string? drive) => drive switch
    {
        null => null,
        _ when drive.StartsWith("4WD", StringComparison.OrdinalIgnoreCase) || drive.Contains("4x4", StringComparison.OrdinalIgnoreCase) => "4WD",
        _ when drive.StartsWith("AWD", StringComparison.OrdinalIgnoreCase) => "AWD",
        _ when drive.StartsWith("FWD", StringComparison.OrdinalIgnoreCase) => "FWD",
        _ when drive.StartsWith("RWD", StringComparison.OrdinalIgnoreCase) => "RWD",
        _ when drive.StartsWith("4x2", StringComparison.OrdinalIgnoreCase) => "2WD",
        _ => drive,
    };

    private static string? DescribeTransmission(string? style, string? speeds)
    {
        if (style is null && speeds is null) return null;
        if (speeds is null) return style;
        return style is null ? $"{speeds}-speed" : $"{style}, {speeds}-speed";
    }

    [GeneratedRegex(@"^\d{3,4}(HD|LD)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesSuffixRegex();
}
