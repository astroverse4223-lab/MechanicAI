using System.Security.Claims;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Services;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Http;

namespace MechanicAI.Server.Endpoints;

/// <summary>Repairs, notes, estimates, and inspections (<see cref="ShopService"/>).</summary>
public static class ShopEndpoints
{
    public static IEndpointRouteBuilder MapShopEndpoints(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------ repairs
        var repairs = app.MapGroup("/api/repairs").WithTags("Shop");
        repairs.MapGet("/", async (Guid? vehicleId, int? take, ShopService shop, CancellationToken ct) =>
            TypedResults.Ok((await shop.ListRepairsAsync(vehicleId, Math.Clamp(take ?? 200, 1, 500), ct)).Select(RepairDto.From).ToList()));

        repairs.MapPost("/", async (AddRepairRequest request, ClaimsPrincipal user, ShopService shop, CancellationToken ct) =>
                (await shop.AddRepairAsync(request.VehicleId, request.Kind, request.Title ?? string.Empty, request.Description, request.Mileage,
                    request.LaborHours, (request.Parts ?? []).Select(p => new PartInput(p.Description, p.PartNumber, p.Quantity, p.UnitPrice, p.Brand)).ToList(),
                    user.TechnicianName(), ct)).ToHttp(id => TypedResults.Created($"/api/repairs?vehicleId={request.VehicleId}", new CreatedResponse(id))))
            .RequireAuthorization(Policies.Records);

        // ------------------------------------------------------------ notes
        var notes = app.MapGroup("/api/notes").WithTags("Shop");
        notes.MapGet("/", async (Guid? vehicleId, Guid? sessionId, ShopService shop, CancellationToken ct) =>
            TypedResults.Ok((await shop.ListNotesAsync(vehicleId, sessionId, ct)).Select(NoteDto.From).ToList()));

        notes.MapPost("/", async (NoteRequest request, ClaimsPrincipal user, ShopService shop, CancellationToken ct) =>
            (await shop.SaveNoteAsync(null, request.VehicleId, request.SessionId, request.Kind, request.Title ?? string.Empty, request.Body ?? string.Empty,
                request.Pinned, user.TechnicianName(), ct)).ToHttp(id => TypedResults.Created($"/api/notes/{id}", new CreatedResponse(id))));

        notes.MapPut("/{id:guid}", async (Guid id, NoteRequest request, ShopService shop, CancellationToken ct) =>
            (await shop.SaveNoteAsync(id, request.VehicleId, request.SessionId, request.Kind, request.Title ?? string.Empty, request.Body ?? string.Empty,
                request.Pinned, null, ct)).ToHttp(_ => TypedResults.NoContent()));

        notes.MapDelete("/{id:guid}", async (Guid id, ShopService shop, CancellationToken ct) => (await shop.DeleteNoteAsync(id, ct)).ToHttp());

        // ------------------------------------------------------------ estimates
        var estimates = app.MapGroup("/api/estimates").WithTags("Shop");
        estimates.MapGet("/", async (Guid? vehicleId, ShopService shop, CancellationToken ct) =>
            TypedResults.Ok((await shop.ListEstimatesAsync(vehicleId, ct)).Select(EstimateDto.From).ToList()));

        estimates.MapGet("/{id:guid}", async (Guid id, ShopService shop, CancellationToken ct) =>
            await shop.GetEstimateAsync(id, ct) is { } e ? Results.Ok(EstimateDto.From(e)) : ResultMapping.NotFound("Estimate"));

        estimates.MapPost("/", async (EstimateRequest request, ShopService shop, CancellationToken ct) =>
                (await shop.SaveEstimateAsync(null, request.CustomerId, request.VehicleId, request.SessionId, request.TaxRate, request.Notes,
                    request.Lines ?? [], request.Status, ct)).ToHttp(id => TypedResults.Created($"/api/estimates/{id}", new CreatedResponse(id))))
            .RequireAuthorization(Policies.Records);

        estimates.MapPut("/{id:guid}", async (Guid id, EstimateRequest request, ShopService shop, CancellationToken ct) =>
                (await shop.SaveEstimateAsync(id, request.CustomerId, request.VehicleId, request.SessionId, request.TaxRate, request.Notes,
                    request.Lines ?? [], request.Status, ct)).ToHttp(_ => TypedResults.NoContent()))
            .RequireAuthorization(Policies.Records);

        estimates.MapDelete("/{id:guid}", async (Guid id, ShopService shop, CancellationToken ct) => (await shop.DeleteEstimateAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Records);

        // ------------------------------------------------------------ inspections
        var inspections = app.MapGroup("/api/inspections").WithTags("Shop");
        inspections.MapGet("/", async (Guid? vehicleId, ShopService shop, CancellationToken ct) =>
            TypedResults.Ok((await shop.ListInspectionsAsync(vehicleId, ct)).Select(InspectionDto.From).ToList()));

        inspections.MapPost("/", async (StartInspectionRequest request, ClaimsPrincipal user, ShopService shop, CancellationToken ct) =>
                (await shop.StartInspectionAsync(request.VehicleId, request.Mileage, user.TechnicianName(), ct))
                .ToHttp(id => TypedResults.Created($"/api/inspections?vehicleId={request.VehicleId}", new CreatedResponse(id))))
            .RequireAuthorization(Policies.Records);

        inspections.MapPut("/items/{itemId:guid}", async (Guid itemId, UpdateInspectionItemRequest request, ShopService shop, CancellationToken ct) =>
                (await shop.UpdateInspectionItemAsync(itemId, request.Rating, request.Measurement, request.Notes, ct)).ToHttp())
            .RequireAuthorization(Policies.Records);

        inspections.MapPost("/{id:guid}/complete", async (Guid id, ShopService shop, CancellationToken ct) =>
                (await shop.CompleteInspectionAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Records);

        return app;
    }
}
