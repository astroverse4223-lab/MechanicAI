using System.Security.Claims;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Http;

namespace MechanicAI.Server.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapGroup("/api/sessions").WithTags("Diagnostic sessions");
        var write = sessions.MapGroup(string.Empty).RequireAuthorization(Policies.Diagnostics);

        sessions.MapGet("/", async (Guid? vehicleId, bool? includeClosed, int? take, DiagnosticSessionService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(Math.Clamp(take ?? 50, 1, 500), vehicleId, includeClosed ?? true, ct)));

        sessions.MapGet("/{id:guid}", GetSessionAsync)
            .WithName("GetDiagnosticSession")
            .WithSummary("The session with its diagnostic tree, test ranking, safety warnings, and step history.");

        sessions.MapGet("/{id:guid}/report", async (Guid id, DiagnosticSessionService service, ISettingsStore settings, CancellationToken ct) =>
                await service.GetViewAsync(id, ct) is { } view
                    ? Results.Text(ReportService.BuildMarkdown(view.Session, view, settings.Current.Diagnostics.ShopName), "text/markdown; charset=utf-8")
                    : ResultMapping.NotFound("Diagnostic session"))
            .WithSummary("Customer/technician report as Markdown.");

        write.MapPost("/", async (StartSessionRequest request, ClaimsPrincipal user, DiagnosticSessionService service, CancellationToken ct) =>
                Created(await service.StartAsync(request.ToCommand(user.TechnicianName()), ct)))
            .WithSummary("Start a session from a complaint, symptoms, and DTCs; the diagnostic tree is built immediately.");

        write.MapPost("/from-text", async (StartSessionFromTextRequest request, DiagnosticSessionService service, CancellationToken ct) =>
                Created(await service.StartFromTextAsync(request.Text ?? string.Empty, request.VehicleId, ct)))
            .WithSummary("Start a session from free text such as \"2017 Silverado 5.3 P0171 P0174\".");

        write.MapPost("/{id:guid}/tests/{testId:guid}/result",
                async (Guid id, Guid testId, RecordTestResultRequest request, ClaimsPrincipal user, DiagnosticSessionService service, CancellationToken ct) =>
                    await ThenViewAsync(await service.RecordTestResultAsync(
                        new RecordTestResultCommand(id, testId, request.OutcomeKey, request.ActualResult, user.TechnicianName()), ct), id, service, ct))
            .WithSummary("Record a test outcome; probabilities and the next recommended test are recomputed.");

        write.MapDelete("/{id:guid}/tests/{testId:guid}/result", async (Guid id, Guid testId, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.RevertTestResultAsync(id, testId, ct), id, service, ct));

        write.MapPost("/{id:guid}/tests/{testId:guid}/skip", async (Guid id, Guid testId, ReasonRequest? request, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.SkipTestAsync(id, testId, request?.Reason, ct), id, service, ct));

        write.MapPost("/{id:guid}/back", async (Guid id, DiagnosticSessionService service, CancellationToken ct) =>
                await ThenViewAsync(await service.GoBackAsync(id, ct), id, service, ct))
            .WithSummary("Revert the most recently recorded test result.");

        write.MapPost("/{id:guid}/observations", async (Guid id, ObservationRequest request, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.AddObservationAsync(id, request.Observation ?? string.Empty, ct), id, service, ct));

        write.MapPost("/{id:guid}/dtcs", async (Guid id, AddSessionDtcRequest request, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.AddDtcAsync(id, request.Code ?? string.Empty, request.Status, ct: ct), id, service, ct));

        write.MapDelete("/{id:guid}/dtcs/{code}", async (Guid id, string code, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.RemoveDtcAsync(id, code, ct), id, service, ct));

        write.MapPut("/{id:guid}/causes/{nodeId:guid}/status",
            async (Guid id, Guid nodeId, CauseStatusRequest request, DiagnosticSessionService service, CancellationToken ct) =>
                await ThenViewAsync(await service.SetCauseStatusAsync(id, nodeId, request.Status, request.Reason, ct), id, service, ct));

        write.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmDiagnosisRequest request, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.ConfirmDiagnosisAsync(id, request.NodeId, request.Notes, ct), id, service, ct));

        write.MapPost("/{id:guid}/repair",
            async (Guid id, RecordRepairRequest request, ClaimsPrincipal user, DiagnosticSessionService service, CancellationToken ct) =>
                await ThenViewAsync(await service.RecordRepairAsync(
                    new RecordRepairCommand(id, request.Description ?? string.Empty, request.Parts ?? [], request.LaborHours, user.TechnicianName()), ct), id, service, ct));

        write.MapPost("/{id:guid}/verification",
            async (Guid id, RecordVerificationRequest request, ClaimsPrincipal user, DiagnosticSessionService service, CancellationToken ct) =>
                await ThenViewAsync(await service.RecordVerificationAsync(
                    new RecordVerificationCommand(id, request.Passed, request.Notes ?? string.Empty, user.TechnicianName()), ct), id, service, ct));

        write.MapPost("/{id:guid}/reopen", async (Guid id, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.ReopenAsync(id, ct), id, service, ct));

        write.MapPost("/{id:guid}/abandon", async (Guid id, ReasonRequest? request, DiagnosticSessionService service, CancellationToken ct) =>
            await ThenViewAsync(await service.AbandonAsync(id, request?.Reason, ct), id, service, ct));

        sessions.MapDelete("/{id:guid}", async (Guid id, DiagnosticSessionService service, CancellationToken ct) =>
                (await service.DeleteAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Admin);

        return app;
    }

    private static async Task<IResult> GetSessionAsync(Guid id, DiagnosticSessionService service, CancellationToken ct) =>
        await service.GetViewAsync(id, ct) is { } view ? Results.Ok(DiagnosticSessionDto.From(view)) : ResultMapping.NotFound("Diagnostic session");

    private static IResult Created(Result<Guid> result) =>
        result.ToHttp(id => TypedResults.CreatedAtRoute(new CreatedResponse(id), "GetDiagnosticSession", new { id }));

    /// <summary>Mutations return the updated session so a workstation can re-render without a second round-trip.</summary>
    private static async Task<IResult> ThenViewAsync(Result result, Guid id, DiagnosticSessionService service, CancellationToken ct) =>
        result.IsFailure ? result.Error!.ToProblem() : await GetSessionAsync(id, service, ct);
}
