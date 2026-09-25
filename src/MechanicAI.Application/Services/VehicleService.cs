using System.Globalization;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.DTOs;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

/// <summary>
/// Vehicle records, VIN decoding (NHTSA vPIC), recalls and owner complaints (NHTSA).
/// Vehicle identity is never invented: decoded values are stored with their source, and
/// anything not returned by the decoder is left blank for the technician to fill in.
/// </summary>
public sealed class VehicleService(
    IAppDbContextFactory dbFactory,
    IVinDecoder vinDecoder,
    IRecallProvider recallProvider,
    IComplaintProvider complaintProvider,
    ISettingsStore settings,
    ILogger<VehicleService> logger)
{
    public event EventHandler<Guid>? VehicleChanged;

    public static VinLookup AnalyzeLocally(string vin)
    {
        if (!Vin.TryParse(vin, out var parsed, out var error)) throw new ArgumentException(error);
        return new VinLookup(parsed.Value, parsed.HasValidCheckDigit, parsed.IsNorthAmerican, parsed.EstimatedModelYear,
            parsed.RegionOfManufacture, null, null);
    }

    public async Task<Result<VinLookup>> DecodeVinAsync(string vin, int? modelYear = null, CancellationToken ct = default)
    {
        if (!Vin.TryParse(vin, out var parsed, out var error)) return Error.Validation(error!);

        var local = new VinLookup(parsed.Value, parsed.HasValidCheckDigit, parsed.IsNorthAmerican, parsed.EstimatedModelYear,
            parsed.RegionOfManufacture, null, null);
        try
        {
            var decoded = await vinDecoder.DecodeAsync(parsed.Value, modelYear, ct);
            return local with { Decode = decoded };
        }
        catch (ExternalServiceException ex)
        {
            logger.LogWarning("VIN decode failed for {VinPrefix}: {Kind}", parsed.Wmi + parsed.Vds, ex.Kind);
            return local with { DecodeError = ex.UserMessage };
        }
    }

    /// <summary>Decodes a VIN and saves the vehicle with verified specifications.</summary>
    public async Task<Result<Guid>> CreateFromVinAsync(string vin, int? mileage, Guid? customerId, CancellationToken ct = default)
    {
        var lookup = await DecodeVinAsync(vin, null, ct);
        if (lookup.IsFailure) return Result<Guid>.Failure(lookup.Error!);
        var info = lookup.Value!;
        if (info.Decode is not { Value.IsUsable: true } decoded)
        {
            return Error.Validation(info.DecodeError ?? "NHTSA could not identify this VIN. Check it for typos, or enter the vehicle manually.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var existing = await db.Vehicles.Include(v => v.Specifications).FirstOrDefaultAsync(v => v.Vin == info.Vin, ct);
        var vehicle = existing ?? new Vehicle { Vin = info.Vin };
        ApplyDecode(vehicle, decoded);
        if (mileage is not null) vehicle.Mileage = mileage;
        if (customerId is not null) vehicle.CustomerId = customerId;
        vehicle.LastAccessedUtc = DateTime.UtcNow;
        if (existing is null) db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Vehicle {VehicleId} saved from VIN decode ({Source})", vehicle.Id, decoded.SourceName);
        VehicleChanged?.Invoke(this, vehicle.Id);

        if (settings.Current.VehicleData.AutoCheckRecalls)
        {
            _ = await RefreshRecallsAsync(vehicle.Id, ct);
        }

        return vehicle.Id;
    }

    /// <summary>Re-decodes an existing vehicle's VIN and refreshes its verified specifications.</summary>
    public async Task<Result> RedecodeAsync(Guid vehicleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.Include(v => v.Specifications).FirstOrDefaultAsync(v => v.Id == vehicleId, ct);
        if (vehicle is null) return Error.NotFound("Vehicle");
        if (string.IsNullOrWhiteSpace(vehicle.Vin)) return Error.Validation("This vehicle has no VIN to decode.");
        var lookup = await DecodeVinAsync(vehicle.Vin, vehicle.Year, ct);
        if (lookup.IsFailure) return lookup.Error!;
        if (lookup.Value!.Decode is not { Value.IsUsable: true } decoded) return Error.Validation(lookup.Value.DecodeError ?? "The VIN could not be decoded.");
        ApplyDecode(vehicle, decoded);
        await db.SaveChangesAsync(ct);
        VehicleChanged?.Invoke(this, vehicle.Id);
        return Result.Success();
    }

    private static void ApplyDecode(Vehicle vehicle, Sourced<VinDecodeResult> decoded)
    {
        var d = decoded.Value;
        vehicle.Year = d.ModelYear;
        vehicle.Make = Text.NormalizeMake(d.Make);
        vehicle.Model = d.Model ?? vehicle.Model;
        vehicle.Trim = d.Trim ?? vehicle.Trim;
        vehicle.Engine = d.EngineDescription ?? vehicle.Engine;
        vehicle.DisplacementLiters = d.DisplacementLiters ?? vehicle.DisplacementLiters;
        vehicle.Cylinders = d.Cylinders ?? vehicle.Cylinders;
        vehicle.FuelType = d.FuelType ?? vehicle.FuelType;
        vehicle.Drivetrain = d.Drivetrain ?? vehicle.Drivetrain;
        vehicle.Transmission = d.Transmission ?? vehicle.Transmission;
        vehicle.BodyClass = d.BodyClass ?? vehicle.BodyClass;
        vehicle.DataSource = VehicleDataSource.NhtsaVpic;
        vehicle.DecodedUtc = decoded.RetrievedUtc;

        vehicle.Specifications.RemoveAll(s => s.Verification == VerificationLevel.Verified);
        foreach (var field in d.Fields)
        {
            vehicle.Specifications.Add(new VehicleSpecification
            {
                VehicleId = vehicle.Id,
                Category = field.Category,
                Name = field.Name,
                Value = field.Value,
                Source = decoded.SourceName,
                SourceKey = field.SourceKey,
                RetrievedUtc = decoded.RetrievedUtc,
                Verification = VerificationLevel.Verified,
            });
        }
    }

    public async Task<Result<Guid>> SaveAsync(VehicleInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Make) || string.IsNullOrWhiteSpace(input.Model))
        {
            return Error.Validation("Make and model are required (or decode a VIN).");
        }

        if (input.Year is { } year && (year < 1900 || year > DateTime.UtcNow.Year + 2)) return Error.Validation("Model year is out of range.");
        if (input.Mileage is < 0) return Error.Validation("Mileage cannot be negative.");

        string? vin = null;
        if (!string.IsNullOrWhiteSpace(input.Vin))
        {
            if (!Vin.TryParse(input.Vin, out var parsedVin, out var vinError)) return Error.Validation(vinError!);
            vin = parsedVin.Value;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        if (vin is not null && await db.Vehicles.AnyAsync(v => v.Vin == vin && v.Id != input.Id, ct))
        {
            return Error.Validation("Another vehicle with this VIN already exists.");
        }

        Vehicle vehicle;
        if (input.Id is { } id)
        {
            vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct) ?? throw new InvalidOperationException("Vehicle not found.");
        }
        else
        {
            vehicle = new Vehicle { DataSource = VehicleDataSource.Manual };
            db.Vehicles.Add(vehicle);
        }

        vehicle.Vin = vin;
        vehicle.Year = input.Year;
        vehicle.Make = Text.NormalizeMake(input.Make);
        vehicle.Model = input.Model.Trim();
        vehicle.Trim = Blank(input.Trim);
        vehicle.Engine = Blank(input.Engine);
        vehicle.DisplacementLiters = input.DisplacementLiters;
        vehicle.Cylinders = input.Cylinders;
        vehicle.FuelType = Blank(input.FuelType);
        vehicle.Drivetrain = Blank(input.Drivetrain);
        vehicle.Transmission = Blank(input.Transmission);
        vehicle.BodyClass = Blank(input.BodyClass);
        vehicle.Mileage = input.Mileage;
        vehicle.MileageUnit = input.MileageUnit;
        vehicle.Color = Blank(input.Color);
        vehicle.LicensePlate = Blank(input.LicensePlate);
        vehicle.CustomerId = input.CustomerId;
        vehicle.Notes = Blank(input.Notes);
        vehicle.LastAccessedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        VehicleChanged?.Invoke(this, vehicle.Id);
        return vehicle.Id;
    }

    public async Task<Vehicle?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.AsNoTracking()
            .Include(v => v.Customer)
            .Include(v => v.Specifications)
            .Include(v => v.Recalls)
            .AsSplitQuery()
            .FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is not null)
        {
            vehicle.Specifications = vehicle.Specifications.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
            vehicle.Recalls = vehicle.Recalls.OrderByDescending(r => r.ReportReceivedDate).ToList();
        }

        return vehicle;
    }

    public async Task MarkAccessedAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return;
        vehicle.LastAccessedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await settings.UpdateAsync(s => s.ActiveVehicleId = id, ct);
    }

    public async Task<IReadOnlyList<VehicleSummary>> ListAsync(string? search = null, int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Vehicles.AsNoTracking().Include(v => v.Customer).Include(v => v.Recalls).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";
            int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out var yearTerm);
            query = query.Where(v =>
                (v.Vin != null && EF.Functions.Like(v.Vin, pattern, "\\")) ||
                EF.Functions.Like(v.Make, pattern, "\\") ||
                EF.Functions.Like(v.Model, pattern, "\\") ||
                (v.Trim != null && EF.Functions.Like(v.Trim, pattern, "\\")) ||
                (v.LicensePlate != null && EF.Functions.Like(v.LicensePlate, pattern, "\\")) ||
                (v.Customer != null && (EF.Functions.Like(v.Customer.LastName, pattern, "\\") || EF.Functions.Like(v.Customer.FirstName, pattern, "\\"))) ||
                v.Year == yearTerm);
        }

        var vehicles = await query.OrderByDescending(v => v.LastAccessedUtc).ThenByDescending(v => v.UpdatedUtc).Take(take).ToListAsync(ct);
        var ids = vehicles.Select(v => v.Id).ToList();
        var sessionCounts = await db.DiagnosticSessions.AsNoTracking()
            .Where(s => s.VehicleId != null && ids.Contains(s.VehicleId.Value))
            .GroupBy(s => s.VehicleId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return vehicles.Select(v => ToSummary(v, sessionCounts.GetValueOrDefault(v.Id))).ToList();
    }

    public async Task<IReadOnlyList<VehicleSummary>> RecentAsync(int take = 8, CancellationToken ct = default) =>
        (await ListAsync(null, take, ct)).Where(v => v.LastAccessedUtc is not null).ToList();

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return Error.NotFound("Vehicle");
        db.Vehicles.Remove(vehicle);
        await db.SaveChangesAsync(ct);
        if (settings.Current.ActiveVehicleId == id) await settings.UpdateAsync(s => s.ActiveVehicleId = null, ct);
        VehicleChanged?.Invoke(this, id);
        return Result.Success();
    }

    public async Task<Result> SetFavoriteAsync(Guid id, bool favorite, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vehicle is null) return Error.NotFound("Vehicle");
        vehicle.IsFavorite = favorite;
        await db.SaveChangesAsync(ct);
        VehicleChanged?.Invoke(this, id);
        return Result.Success();
    }

    /// <summary>Fetches NHTSA recalls for the vehicle's year/make/model and stores them.</summary>
    public async Task<Result<IReadOnlyList<Recall>>> RefreshRecallsAsync(Guid vehicleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var vehicle = await db.Vehicles.Include(v => v.Recalls).FirstOrDefaultAsync(v => v.Id == vehicleId, ct);
        if (vehicle is null) return Error.NotFound("Vehicle");
        if (vehicle.Year is not { } year || string.IsNullOrWhiteSpace(vehicle.Make) || string.IsNullOrWhiteSpace(vehicle.Model))
        {
            return Error.Validation("Year, make, and model are needed to look up recalls.");
        }

        Sourced<IReadOnlyList<RecallRecord>> records;
        try
        {
            records = await recallProvider.GetRecallsAsync(vehicle.Make, vehicle.Model, year, ct);
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }

        foreach (var record in records.Value)
        {
            var recall = vehicle.Recalls.FirstOrDefault(r => r.CampaignNumber == record.CampaignNumber);
            if (recall is null)
            {
                recall = new Recall { VehicleId = vehicle.Id, CampaignNumber = record.CampaignNumber, Status = RecallStatus.Unknown };
                vehicle.Recalls.Add(recall);
            }

            recall.Manufacturer = record.Manufacturer;
            recall.Component = record.Component;
            recall.Summary = record.Summary;
            recall.Consequence = record.Consequence;
            recall.Remedy = record.Remedy;
            recall.Notes = record.Notes;
            recall.ReportReceivedDate = record.ReportReceivedDate;
            recall.ParkIt = record.ParkIt;
            recall.ParkOutside = record.ParkOutside;
            recall.OverTheAirUpdate = record.OverTheAirUpdate;
            recall.RetrievedUtc = records.RetrievedUtc;
            recall.SourceUrl = record.SourceUrl;
        }

        vehicle.RecallsCheckedUtc = records.RetrievedUtc;
        await db.SaveChangesAsync(ct);
        VehicleChanged?.Invoke(this, vehicle.Id);
        return Result<IReadOnlyList<Recall>>.Success(vehicle.Recalls.OrderByDescending(r => r.ReportReceivedDate).ToList());
    }

    public async Task<Result> SetRecallStatusAsync(Guid recallId, RecallStatus status, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var recall = await db.Recalls.FirstOrDefaultAsync(r => r.Id == recallId, ct);
        if (recall is null) return Error.NotFound("Recall");
        recall.Status = status;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Owner complaints filed with NHTSA for this year/make/model (unverified reports).</summary>
    public async Task<Result<Sourced<IReadOnlyList<ComplaintRecord>>>> GetComplaintsAsync(Guid vehicleId, CancellationToken ct = default)
    {
        var vehicle = await GetAsync(vehicleId, ct);
        if (vehicle is null) return Error.NotFound("Vehicle");
        if (vehicle.Year is not { } year) return Error.Validation("Model year is needed to look up complaints.");
        try
        {
            return await complaintProvider.GetComplaintsAsync(vehicle.Make, vehicle.Model, year, ct);
        }
        catch (ExternalServiceException ex)
        {
            return new Error(ex.Kind, ex.UserMessage);
        }
    }

    private static VehicleSummary ToSummary(Vehicle v, int sessions) => new(
        v.Id,
        v.DisplayName,
        v.Engine ?? (v.DisplacementLiters is { } d ? string.Create(CultureInfo.InvariantCulture, $"{d:0.0}L") : null),
        v.Vin,
        v.Mileage,
        v.MileageUnit,
        v.Customer?.DisplayName,
        v.LastAccessedUtc,
        v.Recalls.Count(r => r.Status is RecallStatus.Open or RecallStatus.Unknown),
        sessions,
        v.IsSample,
        v.IsFavorite,
        v.DataSource);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
