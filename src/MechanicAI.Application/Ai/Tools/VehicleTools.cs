using System.Text.Json;
using MechanicAI.Application.Common;
using MechanicAI.Application.Search;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Application.Ai.Tools;

public sealed class DecodeVinTool(VehicleService vehicles) : IAiTool
{
    public string Name => "decode_vin";

    public string Description => "Decode a 17-character VIN with NHTSA vPIC. Returns verified year/make/model/engine/etc. Use this instead of guessing vehicle details from a VIN.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { vin = new { type = "string", description = "17-character VIN" } },
        required = new[] { "vin" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var vin = ToolArgs.String(arguments, "vin") ?? string.Empty;
        var result = await vehicles.DecodeVinAsync(vin, null, cancellationToken);
        if (result.IsFailure) return ToolExecutionResult.Fail(result.Error!.Message);
        var lookup = result.Value!;
        if (lookup.Decode is null) return ToolExecutionResult.Fail(lookup.DecodeError ?? "The VIN could not be decoded.");

        var decode = lookup.Decode;
        var source = context.Sources.Register(new SourceCitation
        {
            Title = $"NHTSA vPIC decode of VIN {lookup.Vin}",
            Url = decode.SourceUrl,
            Type = SourceType.Government,
            Publisher = "NHTSA",
            RetrievedUtc = decode.RetrievedUtc,
            FromCache = decode.FromCache,
        });
        var d = decode.Value;
        return ToolExecutionResult.Ok(new
        {
            source = source.Label,
            evidence = "VERIFIED (NHTSA vPIC)",
            vin = lookup.Vin,
            checkDigitValid = lookup.CheckDigitValid,
            modelYear = d.ModelYear,
            make = d.Make,
            model = d.Model,
            trim = d.Trim,
            engine = d.EngineDescription,
            displacementLiters = d.DisplacementLiters,
            cylinders = d.Cylinders,
            fuel = d.FuelType,
            drivetrain = d.Drivetrain,
            transmission = d.Transmission,
            bodyClass = d.BodyClass,
            warnings = d.Warnings,
            fromCache = decode.FromCache,
            note = "Fields not listed were not provided by NHTSA; do not infer them.",
        }, d.IsUsable ? $"{d.ModelYear} {d.Make} {d.Model} {d.EngineDescription}".Trim() : "VIN decoded with limited data");
    }
}

public sealed class SearchVehicleTool(VehicleService vehicles) : IAiTool
{
    public string Name => "search_vehicle";

    public string Description => "Search the shop's saved vehicles by VIN, year, make, model, plate, or customer name. Returns technician-entered vehicle records.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { query = new { type = "string", description = "e.g. '2017 Silverado', a VIN, or a customer's last name" } },
        required = new[] { "query" },
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var query = ToolArgs.String(arguments, "query") ?? string.Empty;
        var parsed = QueryParser.Parse(query);
        var list = await vehicles.ListAsync(parsed.Model ?? parsed.Make ?? query, 10, cancellationToken);
        if (parsed.Year is { } year) list = list.Where(v => v.DisplayName.StartsWith(year.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList();
        return ToolExecutionResult.Ok(new
        {
            evidence = "TECHNICIAN RECORDS",
            count = list.Count,
            vehicles = list.Select(v => new { id = v.Id, name = v.DisplayName, engine = v.Engine, vin = v.Vin, mileage = v.Mileage, customer = v.CustomerName, isSample = v.IsSample }),
        }, $"{list.Count} saved vehicle(s)");
    }
}

public sealed class SearchRecallsTool(VehicleService vehicles) : IAiTool
{
    public string Name => "search_recalls";

    public string Description => "Get NHTSA safety recalls (and optionally owner complaint counts by component) for a saved vehicle or a year/make/model.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new
        {
            vehicle_id = new { type = "string", description = "Saved vehicle id (preferred)" },
            include_complaints = new { type = "boolean", description = "Also summarize NHTSA owner complaints by component" },
        },
        required = Array.Empty<string>(),
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var vehicleId = ToolArgs.Guid(arguments, "vehicle_id") ?? context.VehicleId;
        if (vehicleId is null) return ToolExecutionResult.Fail("No vehicle specified. Ask the technician to select or save the vehicle first.");
        var recalls = await vehicles.RefreshRecallsAsync(vehicleId.Value, cancellationToken);
        if (recalls.IsFailure) return ToolExecutionResult.Fail(recalls.Error!.Message);

        var vehicle = await vehicles.GetAsync(vehicleId.Value, cancellationToken);
        var list = recalls.Value!;
        var recallSource = list.FirstOrDefault()?.SourceUrl;
        var sources = list.Select(r => context.Sources.Register(new SourceCitation
        {
            Title = $"NHTSA recall {r.CampaignNumber}: {r.Component}",
            Url = r.SourceUrl,
            Type = SourceType.Government,
            Publisher = "NHTSA",
            PublishedUtc = r.ReportReceivedDate,
            Excerpt = Text.Truncate(r.Summary, 300),
            RetrievedUtc = r.RetrievedUtc,
        })).ToList();

        object? complaints = null;
        if (ToolArgs.Bool(arguments, "include_complaints") == true)
        {
            var result = await vehicles.GetComplaintsAsync(vehicleId.Value, cancellationToken);
            if (result.IsSuccess)
            {
                var src = context.Sources.Register(new SourceCitation
                {
                    Title = $"NHTSA owner complaints — {vehicle?.DisplayName}",
                    Url = result.Value!.SourceUrl,
                    Type = SourceType.Government,
                    Publisher = "NHTSA (owner-reported, unverified)",
                    RetrievedUtc = result.Value.RetrievedUtc,
                });
                complaints = new
                {
                    source = src.Label,
                    note = "Owner complaints are unverified reports filed with NHTSA; they indicate patterns, not confirmed defects.",
                    total = result.Value.Value.Count,
                    byComponent = result.Value.Value
                        .SelectMany(c => c.Components.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        .GroupBy(c => c).OrderByDescending(g => g.Count()).Take(10).Select(g => new { component = g.Key, count = g.Count() }),
                };
            }
        }

        return ToolExecutionResult.Ok(new
        {
            vehicle = vehicle?.DisplayName,
            evidence = "GOVERNMENT SOURCE (NHTSA). Recall data is by year/make/model; VIN-level completion status is not included.",
            recallCount = list.Count,
            recalls = list.Zip(sources, (r, s) => new
            {
                source = s.Label,
                campaign = r.CampaignNumber,
                component = r.Component,
                summary = Text.Truncate(r.Summary, 600),
                remedy = Text.Truncate(r.Remedy, 300),
                reportDate = r.ReportReceivedDate?.ToString("yyyy-MM-dd"),
                parkIt = r.ParkIt,
                technicianStatus = r.Status.ToString(),
            }),
            complaints,
            apiSource = recallSource,
        }, $"{list.Count} recall(s)");
    }
}

public sealed class GetVehicleHistoryTool(HistoryService history) : IAiTool
{
    public string Name => "get_vehicle_history";

    public string Description => "Get a saved vehicle's service history: previous diagnostic sessions and diagnoses, DTC history, repairs and parts, notes, recalls.";

    public JsonElement ParametersSchema { get; } = ToolArgs.Schema(new
    {
        type = "object",
        properties = new { vehicle_id = new { type = "string" } },
        required = Array.Empty<string>(),
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var vehicleId = ToolArgs.Guid(arguments, "vehicle_id") ?? context.VehicleId;
        if (vehicleId is null) return ToolExecutionResult.Fail("No vehicle selected.");
        var h = await history.GetVehicleHistoryAsync(vehicleId.Value, null, cancellationToken);
        if (h is null) return ToolExecutionResult.Fail("Vehicle not found.");
        var source = context.Sources.Register(new SourceCitation
        {
            Title = $"Shop records — {h.VehicleName}",
            Type = SourceType.PrivateDocument,
            Publisher = "This workstation",
        });
        return ToolExecutionResult.Ok(new
        {
            source = source.Label,
            evidence = "TECHNICIAN RECORDS",
            vehicle = h.VehicleName,
            sessions = h.SessionCount,
            repairs = h.RepairCount,
            openRecalls = h.OpenRecallCount,
            dtcHistory = h.DtcHistory.Select(d => new { code = d.Code, occurrences = d.Occurrences, lastSeen = d.LastSeenUtc.ToString("yyyy-MM-dd") }),
            recent = h.Items.Take(25).Select(i => new { date = i.WhenUtc.ToString("yyyy-MM-dd"), kind = i.Kind, title = i.Title, detail = Text.Truncate(i.Detail, 300) }),
        }, $"{h.Items.Count} history item(s)");
    }
}
