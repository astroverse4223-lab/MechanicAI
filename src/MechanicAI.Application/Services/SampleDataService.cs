using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

/// <summary>
/// Installs or removes the built-in development sample data. Every sample record is flagged
/// <c>IsSample</c> and shown with a "Sample" badge; sample vehicles are never presented as
/// VIN-verified.
/// </summary>
public sealed class SampleDataService(
    IAppDbContextFactory dbFactory,
    IReferenceContentProvider content,
    DiagnosticSessionService sessions,
    ISettingsStore settings,
    ILogger<SampleDataService> logger)
{
    public async Task<Result<int>> InstallAsync(CancellationToken ct = default)
    {
        var data = await content.LoadSampleDataAsync(ct);
        if (data is null) return Error.NotFound("Sample data");

        await using var db = await dbFactory.CreateAsync(ct);
        if (await db.Vehicles.AnyAsync(v => v.IsSample, ct)) return Error.Validation("Sample data is already installed.");

        var customers = new Dictionary<string, Customer>(StringComparer.Ordinal);
        foreach (var c in data.Customers)
        {
            var customer = new Customer
            {
                FirstName = c.FirstName,
                LastName = c.LastName,
                Phone = c.Phone,
                Email = c.Email,
                Notes = "SAMPLE customer — fictional.",
                IsSample = true,
            };
            customers[c.Key] = customer;
            db.Customers.Add(customer);
        }

        var vehicles = new Dictionary<string, Vehicle>(StringComparer.Ordinal);
        foreach (var v in data.Vehicles)
        {
            var vehicle = new Vehicle
            {
                Year = v.Year,
                Make = v.Make,
                Model = v.Model,
                Trim = v.Trim,
                Engine = v.Engine,
                DisplacementLiters = v.DisplacementLiters,
                Cylinders = v.Cylinders,
                FuelType = v.FuelType,
                Drivetrain = v.Drivetrain,
                Transmission = v.Transmission,
                Mileage = v.Mileage,
                Notes = v.Notes,
                CustomerId = v.CustomerKey is not null && customers.TryGetValue(v.CustomerKey, out var owner) ? owner.Id : null,
                DataSource = VehicleDataSource.Sample,
                IsSample = true,
                LastAccessedUtc = DateTime.UtcNow.AddMinutes(-vehicles.Count * 30),
            };
            vehicles[v.Key] = vehicle;
            db.Vehicles.Add(vehicle);
        }

        await db.SaveChangesAsync(ct);

        var created = 0;
        foreach (var s in data.Sessions)
        {
            if (!vehicles.TryGetValue(s.VehicleKey, out var vehicle)) continue;
            var result = await sessions.StartAsync(new StartDiagnosticSessionCommand
            {
                VehicleId = vehicle.Id,
                Complaint = s.Complaint,
                Dtcs = s.Codes,
                Symptoms = s.Symptoms,
                Mileage = s.Mileage,
                TechnicianName = "Sample Technician",
            }, ct);
            if (result.IsFailure) continue;
            await using var db2 = await dbFactory.CreateAsync(ct);
            var session = await db2.DiagnosticSessions.FirstAsync(x => x.Id == result.Value, ct);
            session.IsSample = true;
            await db2.SaveChangesAsync(ct);
            created++;
        }

        await settings.UpdateAsync(x => x.SampleDataInstalled = true, ct);
        logger.LogInformation("Installed sample data: {Vehicles} vehicles, {Sessions} sessions", vehicles.Count, created);
        return vehicles.Count + created;
    }

    public async Task<Result> RemoveAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var sessions = await db.DiagnosticSessions.Where(s => s.IsSample).ToListAsync(ct);
        db.DiagnosticSessions.RemoveRange(sessions);
        var sampleVehicleIds = await db.Vehicles.Where(v => v.IsSample).Select(v => v.Id).ToListAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.DiagnosticSessions.Where(s => s.VehicleId != null && sampleVehicleIds.Contains(s.VehicleId.Value)).ExecuteDeleteAsync(ct);
        await db.Vehicles.Where(v => v.IsSample).ExecuteDeleteAsync(ct);
        await db.Customers.Where(c => c.IsSample).ExecuteDeleteAsync(ct);
        await settings.UpdateAsync(x => x.SampleDataInstalled = false, ct);
        return Result.Success();
    }
}
