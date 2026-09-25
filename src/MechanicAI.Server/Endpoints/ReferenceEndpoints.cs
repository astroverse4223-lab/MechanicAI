using System.Security.Claims;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Http;

namespace MechanicAI.Server.Endpoints;

/// <summary>DTC reference, training, and web research.</summary>
public static class ReferenceEndpoints
{
    public static IEndpointRouteBuilder MapDtcEndpoints(this IEndpointRouteBuilder app)
    {
        var dtcs = app.MapGroup("/api/dtcs").WithTags("DTC reference");

        dtcs.MapGet("/", async (string q, int? take, DtcService service, CancellationToken ct) =>
                string.IsNullOrWhiteSpace(q)
                    ? Error.Validation("Enter a code, partial code, or description to search for.").ToProblem()
                    : Results.Ok(await service.SearchAsync(q, Math.Clamp(take ?? 50, 1, 200), ct)))
            .WithSummary("Search by code (P0301), partial code (P03), text (\"lean\"), or make + code.");

        dtcs.MapGet("/{code}", async (string code, string? make, DtcService service, CancellationToken ct) =>
                await service.GetDetailAsync(code, make, ct) is { } detail
                    ? Results.Ok(DtcDetailDto.From(detail))
                    : Error.Validation($"'{code}' is not a valid trouble code.").ToProblem())
            .WithSummary("Definitions (generic and manufacturer), matching playbooks, related codes, and safety warnings.");

        dtcs.MapPost("/{code}/definitions", async (string code, DtcDefinitionRequest request, DtcService service, CancellationToken ct) =>
                (await service.SaveUserDefinitionAsync(code, request.Manufacturer, request.Description ?? string.Empty, request.Source ?? string.Empty,
                    request.Notes, ct)).ToHttp())
            .RequireAuthorization(Policies.Knowledge)
            .WithSummary("Add a technician-supplied definition (e.g. manufacturer-specific), labeled with its source.");

        return app;
    }

    public static IEndpointRouteBuilder MapTrainingEndpoints(this IEndpointRouteBuilder app)
    {
        var training = app.MapGroup("/api/training").WithTags("Training");
        var write = training.MapGroup(string.Empty).RequireAuthorization(Policies.Diagnostics);

        training.MapGet("/courses", async (TrainingService service, CancellationToken ct) => TypedResults.Ok(await service.ListCoursesAsync(ct)));

        training.MapGet("/courses/{id:guid}", async (Guid id, TrainingService service, CancellationToken ct) =>
            await service.GetCourseAsync(id, ct) is { } course ? Results.Ok(CourseDto.From(course)) : ResultMapping.NotFound("Course"));

        training.MapGet("/lessons/{id:guid}", async (Guid id, TrainingService service, CancellationToken ct) =>
            await service.GetLessonAsync(id, ct) is { } lesson ? Results.Ok(LessonDto.From(lesson)) : ResultMapping.NotFound("Lesson"));

        write.MapPost("/lessons/{id:guid}/complete", async (Guid id, LessonCompletionRequest? request, TrainingService service, CancellationToken ct) =>
        {
            if (await service.GetLessonAsync(id, ct) is null) return ResultMapping.NotFound("Lesson");
            await service.MarkLessonCompleteAsync(id, request?.Completed ?? true, ct);
            return TypedResults.NoContent();
        });

        write.MapPost("/quizzes/{id:guid}/submit", async (Guid id, SubmitQuizRequest request, ClaimsPrincipal user, TrainingService service, CancellationToken ct) =>
            (await service.SubmitQuizAsync(id, request.Answers ?? [], user.TechnicianName(), ct)).ToHttp());

        training.MapGet("/flashcards/due", async (Guid? courseId, int? take, TrainingService service, CancellationToken ct) =>
            TypedResults.Ok((await service.GetDueFlashcardsAsync(courseId, Math.Clamp(take ?? 20, 1, 100), ct)).Select(FlashcardDto.From).ToList()));

        write.MapPost("/flashcards/{id:guid}/review", async (Guid id, FlashcardReviewRequest request, TrainingService service, CancellationToken ct) =>
        {
            await service.ReviewFlashcardAsync(id, request.Correct, ct);
            return TypedResults.NoContent();
        });

        training.MapGet("/attempts", async (int? take, TrainingService service, CancellationToken ct) =>
            TypedResults.Ok((await service.RecentAttemptsAsync(Math.Clamp(take ?? 30, 1, 200), ct)).Select(TrainingAttemptDto.From).ToList()));

        return app;
    }

    public static IEndpointRouteBuilder MapResearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/research/search", async (string q, string? vehicle, int? maxResults, ResearchService service, CancellationToken ct) =>
                (await service.SearchAsync(q ?? string.Empty, vehicle, maxResults is null ? null : Math.Clamp(maxResults.Value, 1, 25), ct)).ToHttp())
            .WithTags("Research")
            .RequireAuthorization(Policies.Diagnostics)
            .WithSummary("Web research through the server's configured search provider (App:Search:Provider plus its API key). 503 when not configured.");

        return app;
    }
}
