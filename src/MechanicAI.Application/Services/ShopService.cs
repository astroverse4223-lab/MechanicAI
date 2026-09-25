using System.Globalization;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

public sealed record CustomerInput(Guid? Id, string FirstName, string LastName, string? CompanyName, string? Phone, string? Email,
    string? AddressLine1, string? City, string? Region, string? PostalCode, string? Notes);

public sealed record EstimateLineInput(EstimateLineKind Kind, string Description, string? PartNumber, decimal Quantity, decimal UnitPrice, bool Taxable);

/// <summary>
/// Optional shop features: customers, repairs, notes, estimates, and inspections.
/// Deliberately not an accounting system — no invoicing, payments, or inventory.
/// </summary>
public sealed class ShopService(IAppDbContextFactory dbFactory)
{
    public static readonly IReadOnlyList<(string Section, string[] Items)> MultiPointTemplate =
    [
        ("Under hood", ["Engine oil level & condition", "Coolant level & condition", "Brake fluid level", "Power steering fluid", "Washer fluid",
            "Drive belt(s)", "Radiator & heater hoses", "Air filter", "Cabin air filter", "Battery test & terminals"]),
        ("Under vehicle", ["Engine oil leaks", "Transmission leaks", "Exhaust system", "CV boots / driveshaft", "Steering linkage", "Suspension components",
            "Shocks / struts", "Differential / transfer case"]),
        ("Brakes", ["Front pads", "Rear pads / shoes", "Front rotors", "Rear rotors / drums", "Brake lines & hoses", "Parking brake"]),
        ("Tires & wheels", ["LF tread depth", "RF tread depth", "LR tread depth", "RR tread depth", "Tire pressures", "Tire wear pattern", "Spare tire"]),
        ("Exterior & interior", ["Lights (head/tail/brake/turn)", "Wiper blades", "Horn", "Warning lights on dash", "Seat belts", "Windshield"]),
    ];

    // ---------------------------------------------------------------- customers

    public async Task<IReadOnlyList<Customer>> ListCustomersAsync(string? search = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Customers.AsNoTracking().Include(c => c.Vehicles).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var p = "%" + search.Trim() + "%";
            query = query.Where(c => EF.Functions.Like(c.LastName, p) || EF.Functions.Like(c.FirstName, p) ||
                                     (c.CompanyName != null && EF.Functions.Like(c.CompanyName, p)) || (c.Phone != null && EF.Functions.Like(c.Phone, p)) ||
                                     (c.Email != null && EF.Functions.Like(c.Email, p)));
        }

        return await query.OrderBy(c => c.LastName).ThenBy(c => c.FirstName).Take(500).ToListAsync(ct);
    }

    public async Task<Customer?> GetCustomerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Customers.AsNoTracking().Include(c => c.Vehicles).FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Result<Guid>> SaveCustomerAsync(CustomerInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.LastName) && string.IsNullOrWhiteSpace(input.CompanyName) && string.IsNullOrWhiteSpace(input.FirstName))
        {
            return Error.Validation("Enter a name or company.");
        }

        if (!string.IsNullOrWhiteSpace(input.Email) && !input.Email.Contains('@', StringComparison.Ordinal)) return Error.Validation("Email address looks invalid.");

        await using var db = await dbFactory.CreateAsync(ct);
        Customer customer;
        if (input.Id is { } id)
        {
            var existing = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (existing is null) return Error.NotFound("Customer");
            customer = existing;
        }
        else
        {
            customer = new Customer();
            db.Customers.Add(customer);
        }

        customer.FirstName = input.FirstName.Trim();
        customer.LastName = input.LastName.Trim();
        customer.CompanyName = Blank(input.CompanyName);
        customer.Phone = Blank(input.Phone);
        customer.Email = Blank(input.Email);
        customer.AddressLine1 = Blank(input.AddressLine1);
        customer.City = Blank(input.City);
        customer.Region = Blank(input.Region);
        customer.PostalCode = Blank(input.PostalCode);
        customer.Notes = Blank(input.Notes);
        await db.SaveChangesAsync(ct);
        return customer.Id;
    }

    public async Task<Result> DeleteCustomerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return Error.NotFound("Customer");
        db.Customers.Remove(customer);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---------------------------------------------------------------- repairs & notes

    public async Task<IReadOnlyList<Repair>> ListRepairsAsync(Guid? vehicleId, int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Repairs.AsNoTracking().Include(r => r.Parts).Include(r => r.Vehicle).AsQueryable();
        if (vehicleId is { } v) query = query.Where(r => r.VehicleId == v);
        return await query.OrderByDescending(r => r.PerformedUtc).Take(take).ToListAsync(ct);
    }

    public async Task<Result<Guid>> AddRepairAsync(Guid? vehicleId, RepairKind kind, string title, string? description, int? mileage, decimal? laborHours,
        IReadOnlyList<Commands.PartInput> parts, string? technician, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return Error.Validation("Enter what was done.");
        await using var db = await dbFactory.CreateAsync(ct);
        var repair = new Repair
        {
            VehicleId = vehicleId,
            Kind = kind,
            Title = title.Trim(),
            Description = Blank(description),
            Mileage = mileage,
            LaborHours = laborHours,
            TechnicianName = technician,
            Parts = parts.Where(p => !string.IsNullOrWhiteSpace(p.Description)).Select(p => new Part
            {
                Description = p.Description.Trim(),
                PartNumber = Blank(p.PartNumber),
                Quantity = p.Quantity <= 0 ? 1 : p.Quantity,
                UnitPrice = p.UnitPrice,
                Brand = p.Brand,
            }).ToList(),
        };
        db.Repairs.Add(repair);
        await db.SaveChangesAsync(ct);
        return repair.Id;
    }

    public async Task<IReadOnlyList<Note>> ListNotesAsync(Guid? vehicleId, Guid? sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Notes.AsNoTracking();
        if (vehicleId is { } v) query = query.Where(n => n.VehicleId == v);
        if (sessionId is { } s) query = query.Where(n => n.DiagnosticSessionId == s);
        return await query.OrderByDescending(n => n.IsPinned).ThenByDescending(n => n.CreatedUtc).ToListAsync(ct);
    }

    public async Task<Result<Guid>> SaveNoteAsync(Guid? id, Guid? vehicleId, Guid? sessionId, NoteKind kind, string title, string body, bool pinned,
        string? author, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(title)) return Error.Validation("The note is empty.");
        await using var db = await dbFactory.CreateAsync(ct);
        Note note;
        if (id is { } existingId)
        {
            var existing = await db.Notes.FirstOrDefaultAsync(n => n.Id == existingId, ct);
            if (existing is null) return Error.NotFound("Note");
            note = existing;
        }
        else
        {
            note = new Note { VehicleId = vehicleId, DiagnosticSessionId = sessionId, AuthorName = author };
            db.Notes.Add(note);
        }

        note.Kind = kind;
        note.Title = string.IsNullOrWhiteSpace(title) ? Text.Truncate(body.Split('\n')[0], 80) : title.Trim();
        note.Body = body.Trim();
        note.IsPinned = pinned;
        await db.SaveChangesAsync(ct);
        return note.Id;
    }

    public async Task<Result> DeleteNoteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        await db.Notes.Where(n => n.Id == id).ExecuteDeleteAsync(ct);
        return Result.Success();
    }

    // ---------------------------------------------------------------- estimates

    public async Task<IReadOnlyList<Estimate>> ListEstimatesAsync(Guid? vehicleId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Estimates.AsNoTracking().Include(e => e.Lines).Include(e => e.Customer).Include(e => e.Vehicle).AsSplitQuery();
        if (vehicleId is { } v) query = query.Where(e => e.VehicleId == v);
        return await query.OrderByDescending(e => e.CreatedUtc).Take(300).ToListAsync(ct);
    }

    public async Task<Estimate?> GetEstimateAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var estimate = await db.Estimates.AsNoTracking().Include(e => e.Lines).Include(e => e.Customer).Include(e => e.Vehicle).AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (estimate is not null) estimate.Lines = estimate.Lines.OrderBy(l => l.SortOrder).ToList();
        return estimate;
    }

    public async Task<Result<Guid>> SaveEstimateAsync(Guid? id, Guid? customerId, Guid? vehicleId, Guid? sessionId, decimal taxRate, string? notes,
        IReadOnlyList<EstimateLineInput> lines, EstimateStatus status, CancellationToken ct = default)
    {
        if (taxRate is < 0 or > 0.5m) return Error.Validation("Tax rate must be between 0% and 50%.");
        if (lines.Any(l => l.Quantity < 0 || l.UnitPrice < 0)) return Error.Validation("Quantities and prices cannot be negative.");

        await using var db = await dbFactory.CreateAsync(ct);
        Estimate estimate;
        if (id is { } existingId)
        {
            var existing = await db.Estimates.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == existingId, ct);
            if (existing is null) return Error.NotFound("Estimate");
            estimate = existing;
            db.EstimateLines.RemoveRange(estimate.Lines);
            estimate.Lines.Clear();
        }
        else
        {
            var count = await db.Estimates.CountAsync(ct);
            estimate = new Estimate { Number = $"E{DateTime.UtcNow:yyMM}-{count + 1:0000}" };
            db.Estimates.Add(estimate);
        }

        estimate.CustomerId = customerId;
        estimate.VehicleId = vehicleId;
        estimate.DiagnosticSessionId = sessionId;
        estimate.TaxRate = taxRate;
        estimate.Notes = Blank(notes);
        estimate.Status = status;
        estimate.ValidUntilUtc ??= DateTime.UtcNow.AddDays(30);
        var order = 0;
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Description)))
        {
            estimate.Lines.Add(new EstimateLine
            {
                EstimateId = estimate.Id,
                Kind = line.Kind,
                Description = line.Description.Trim(),
                PartNumber = Blank(line.PartNumber),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Taxable = line.Taxable,
                SortOrder = order++,
            });
        }

        await db.SaveChangesAsync(ct);
        return estimate.Id;
    }

    public async Task<Result> DeleteEstimateAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (estimate is null) return Error.NotFound("Estimate");
        db.Estimates.Remove(estimate);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---------------------------------------------------------------- inspections

    public async Task<IReadOnlyList<Inspection>> ListInspectionsAsync(Guid? vehicleId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Inspections.AsNoTracking().Include(i => i.Items).Include(i => i.Vehicle).AsSplitQuery();
        if (vehicleId is { } v) query = query.Where(i => i.VehicleId == v);
        return await query.OrderByDescending(i => i.CreatedUtc).Take(200).ToListAsync(ct);
    }

    public async Task<Result<Guid>> StartInspectionAsync(Guid vehicleId, int? mileage, string? technician, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        if (!await db.Vehicles.AnyAsync(v => v.Id == vehicleId, ct)) return Error.NotFound("Vehicle");
        var inspection = new Inspection { VehicleId = vehicleId, TemplateName = "Multi-point inspection", Mileage = mileage, TechnicianName = technician };
        var order = 0;
        foreach (var (section, items) in MultiPointTemplate)
        {
            foreach (var item in items)
            {
                inspection.Items.Add(new InspectionItem { InspectionId = inspection.Id, Section = section, Name = item, SortOrder = order++ });
            }
        }

        db.Inspections.Add(inspection);
        await db.SaveChangesAsync(ct);
        return inspection.Id;
    }

    public async Task<Result> UpdateInspectionItemAsync(Guid itemId, InspectionRating rating, string? measurement, string? notes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var item = await db.InspectionItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Error.NotFound("Inspection item");
        item.Rating = rating;
        item.Measurement = Blank(measurement);
        item.Notes = Blank(notes);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> CompleteInspectionAsync(Guid inspectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var inspection = await db.Inspections.Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == inspectionId, ct);
        if (inspection is null) return Error.NotFound("Inspection");
        inspection.Status = InspectionStatus.Completed;
        inspection.CompletedUtc = DateTime.UtcNow;
        var urgent = inspection.Items.Count(i => i.Rating == InspectionRating.Urgent);
        var attention = inspection.Items.Count(i => i.Rating == InspectionRating.NeedsAttention);
        var good = inspection.Items.Count(i => i.Rating == InspectionRating.Good);
        inspection.Summary = string.Create(CultureInfo.InvariantCulture, $"{good} good, {attention} need attention, {urgent} urgent");
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
