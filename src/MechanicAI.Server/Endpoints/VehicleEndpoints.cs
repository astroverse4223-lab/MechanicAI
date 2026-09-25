using MechanicAI.Application.Common;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Http;

namespace MechanicAI.Server.Endpoints;

public static class VehicleEndpoints
{
    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder app)
    {
        var vehicles = app.MapGroup("/api/vehicles").WithTags("Vehicles");

        vehicles.MapGet("/", async (string? search, int? take, VehicleService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(search, Math.Clamp(take ?? 200, 1, 500), ct)))
            .WithSummary("List or search vehicles (VIN, make, model, plate, customer, year).");

        vehicles.MapGet("/{id:guid}", async (Guid id, VehicleService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } vehicle ? Results.Ok(VehicleDto.From(vehicle)) : ResultMapping.NotFound("Vehicle"));

        vehicles.MapPost("/", async (VehicleInput input, VehicleService service, CancellationToken ct) =>
                (await service.SaveAsync(input with { Id = null }, ct)).ToHttp(id => TypedResults.Created($"/api/vehicles/{id}", new CreatedResponse(id))))
            .RequireAuthorization(Policies.Records);

        vehicles.MapPut("/{id:guid}", async (Guid id, VehicleInput input, VehicleService service, CancellationToken ct) =>
                (await service.SaveAsync(input with { Id = id }, ct)).ToHttp(_ => TypedResults.NoContent()))
            .RequireAuthorization(Policies.Records);

        vehicles.MapDelete("/{id:guid}", async (Guid id, VehicleService service, CancellationToken ct) =>
                (await service.DeleteAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Admin);

        vehicles.MapPut("/{id:guid}/favorite", async (Guid id, FavoriteRequest request, VehicleService service, CancellationToken ct) =>
                (await service.SetFavoriteAsync(id, request.Favorite, ct)).ToHttp())
            .RequireAuthorization(Policies.Records);

        vehicles.MapGet("/vin/{vin}", async (string vin, int? modelYear, VehicleService service, CancellationToken ct) =>
                (await service.DecodeVinAsync(vin, modelYear, ct)).ToHttp())
            .WithSummary("Decode a VIN (local check digit/region analysis plus NHTSA vPIC).");

        vehicles.MapPost("/from-vin", async (CreateVehicleFromVinRequest request, VehicleService service, CancellationToken ct) =>
                (await service.CreateFromVinAsync(request.Vin, request.Mileage, request.CustomerId, ct))
                .ToHttp(id => TypedResults.Created($"/api/vehicles/{id}", new CreatedResponse(id))))
            .RequireAuthorization(Policies.Records)
            .WithSummary("Decode a VIN and save (or update) the vehicle with verified specifications.");

        vehicles.MapPost("/{id:guid}/redecode", async (Guid id, VehicleService service, CancellationToken ct) =>
                (await service.RedecodeAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Records);

        vehicles.MapGet("/{id:guid}/recalls", async (Guid id, VehicleService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } vehicle
                ? Results.Ok(vehicle.Recalls.Select(RecallDto.From).ToList())
                : ResultMapping.NotFound("Vehicle"));

        vehicles.MapPost("/{id:guid}/recalls/refresh", async (Guid id, VehicleService service, CancellationToken ct) =>
                (await service.RefreshRecallsAsync(id, ct)).ToHttp(r => TypedResults.Ok(r.Select(RecallDto.From).ToList())))
            .RequireAuthorization(Policies.Records)
            .WithSummary("Fetch NHTSA recalls for the vehicle's year/make/model and store them.");

        vehicles.MapGet("/{id:guid}/complaints", async (Guid id, VehicleService service, CancellationToken ct) =>
            (await service.GetComplaintsAsync(id, ct)).ToHttp());

        vehicles.MapGet("/{id:guid}/history", async (Guid id, string? search, HistoryService history, CancellationToken ct) =>
                await history.GetVehicleHistoryAsync(id, search, ct) is { } h ? Results.Ok(VehicleHistoryDto.From(h)) : ResultMapping.NotFound("Vehicle"))
            .WithTags("History");

        app.MapPut("/api/recalls/{id:guid}/status", async (Guid id, SetRecallStatusRequest request, VehicleService service, CancellationToken ct) =>
                (await service.SetRecallStatusAsync(id, request.Status, ct)).ToHttp())
            .WithTags("Vehicles")
            .RequireAuthorization(Policies.Records);

        app.MapGet("/api/history/diagnoses", async (string q, int? take, HistoryService history, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(q)) return Error.Validation("Enter a search query.").ToProblem();
                var found = await history.SearchConfirmedDiagnosesAsync(q, Math.Clamp(take ?? 10, 1, 50), ct);
                return Results.Ok(found.Select(f => new ConfirmedDiagnosisDto(f.SessionId, f.Vehicle, f.Diagnosis, f.Complaint, f.Codes, f.WhenUtc)).ToList());
            })
            .WithTags("History")
            .WithSummary("Confirmed diagnoses across the shop that match a query (shop knowledge).");

        return app;
    }

    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var customers = app.MapGroup("/api/customers").WithTags("Customers");

        customers.MapGet("/", async (string? search, ShopService shop, CancellationToken ct) =>
            TypedResults.Ok((await shop.ListCustomersAsync(search, ct)).Select(CustomerDto.From).ToList()));

        customers.MapGet("/{id:guid}", async (Guid id, ShopService shop, CancellationToken ct) =>
            await shop.GetCustomerAsync(id, ct) is { } c ? Results.Ok(CustomerDto.From(c)) : ResultMapping.NotFound("Customer"));

        customers.MapPost("/", async (CustomerRequest request, ShopService shop, CancellationToken ct) =>
                (await shop.SaveCustomerAsync(request.ToInput(null), ct)).ToHttp(id => TypedResults.Created($"/api/customers/{id}", new CreatedResponse(id))))
            .RequireAuthorization(Policies.Records);

        customers.MapPut("/{id:guid}", async (Guid id, CustomerRequest request, ShopService shop, CancellationToken ct) =>
                (await shop.SaveCustomerAsync(request.ToInput(id), ct)).ToHttp(_ => TypedResults.NoContent()))
            .RequireAuthorization(Policies.Records);

        customers.MapDelete("/{id:guid}", async (Guid id, ShopService shop, CancellationToken ct) =>
                (await shop.DeleteCustomerAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Admin);

        return app;
    }
}
