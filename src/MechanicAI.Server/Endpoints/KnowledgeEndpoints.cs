using System.Globalization;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Server.Auth;
using MechanicAI.Server.Contracts;
using MechanicAI.Server.Hosting;
using MechanicAI.Server.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace MechanicAI.Server.Endpoints;

public static class KnowledgeEndpoints
{
    /// <summary>Allowance for multipart boundaries and form fields on top of the file itself.</summary>
    public const long MultipartOverheadBytes = 64 * 1024;

    public static long EffectiveUploadLimit(UploadOptions options) =>
        Math.Clamp(options.MaxDocumentBytes, 1, KnowledgeBaseService.MaxUploadBytes);

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var kb = app.MapGroup("/api/knowledge").WithTags("Knowledge base");

        kb.MapGet("/documents", async (DocumentKind? kind, Guid? vehicleId, string? search, KnowledgeBaseService service, CancellationToken ct) =>
            TypedResults.Ok((await service.ListAsync(kind, vehicleId, search, ct)).Select(DocumentDto.From).ToList()));

        kb.MapGet("/documents/{id:guid}", async (Guid id, KnowledgeBaseService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } d ? Results.Ok(DocumentDto.From(d)) : ResultMapping.NotFound("Document"));

        kb.MapGet("/documents/{id:guid}/file", async (Guid id, KnowledgeBaseService service, CancellationToken ct) =>
            {
                var document = await service.GetAsync(id, ct);
                if (document is null) return ResultMapping.NotFound("Document");
                var path = service.GetFilePath(document);
                return File.Exists(path)
                    ? Results.File(path, document.ContentType, document.FileName, enableRangeProcessing: true)
                    : ResultMapping.NotFound("Document file");
            })
            .WithSummary("Download the original file.");

        kb.MapPost("/documents", UploadAsync)
            .RequireAuthorization(Policies.Knowledge)
            .Accepts<IFormFile>("multipart/form-data")
            .WithSummary("Upload a document (multipart field \"file\" plus optional title, kind, vehicleId, make, model, yearFrom, yearTo, tags, description). " +
                         "Indexing runs in the background; poll the document for its status.");

        kb.MapPut("/documents/{id:guid}", async (Guid id, DocumentMetadataRequest request, KnowledgeBaseService service, CancellationToken ct) =>
                (await service.UpdateMetadataAsync(id, request.ToOptions(), ct)).ToHttp())
            .RequireAuthorization(Policies.Knowledge);

        kb.MapPost("/documents/{id:guid}/reindex", async (Guid id, KnowledgeBaseService service, CancellationToken ct) =>
                (await service.ReindexAsync(id, ct)).ToHttp(() => TypedResults.Accepted($"/api/knowledge/documents/{id}")))
            .RequireAuthorization(Policies.Knowledge);

        kb.MapDelete("/documents/{id:guid}", async (Guid id, KnowledgeBaseService service, CancellationToken ct) =>
                (await service.DeleteAsync(id, ct)).ToHttp())
            .RequireAuthorization(Policies.Knowledge);

        kb.MapGet("/search", async (string q, int? limit, DocumentKind? kind, Guid? documentId, int? year, string? make, string? model,
                KnowledgeBaseService service, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(q)) return Error.Validation("Enter a search query.").ToProblem();
                var options = new KnowledgeSearchOptions
                {
                    Limit = Math.Clamp(limit ?? 10, 1, 50),
                    Kind = kind,
                    DocumentId = documentId,
                    VehicleYear = year,
                    VehicleMake = make,
                    VehicleModel = model,
                };
                return Results.Ok(await service.SearchAsync(q, options, ct));
            })
            .WithSummary("Hybrid keyword + semantic search over document passages (with page citations).");

        kb.MapPost("/ask", async (AskDocumentsRequest request, KnowledgeBaseService service, CancellationToken ct) =>
            {
                var options = new KnowledgeSearchOptions
                {
                    Kind = request.Kind,
                    DocumentId = request.DocumentId,
                    VehicleYear = request.VehicleYear,
                    VehicleMake = request.VehicleMake,
                    VehicleModel = request.VehicleModel,
                };
                return (await service.AskAsync(request.Question ?? string.Empty, request.Template, options, null, ct)).ToHttp();
            })
            .WithSummary("Answer a question strictly from the shop's documents. Without an AI model the relevant passages are returned.");

        kb.MapGet("/stats", async (KnowledgeBaseService service, CancellationToken ct) =>
        {
            var (documents, ready, chunks, embedded) = await service.GetStatsAsync(ct);
            return TypedResults.Ok(new KnowledgeStatsDto(documents, ready, chunks, embedded));
        });

        return app;
    }

    private static async Task<IResult> UploadAsync(HttpContext http, KnowledgeBaseService service, IOptions<UploadOptions> uploads, CancellationToken ct)
    {
        var limit = EffectiveUploadLimit(uploads.Value);
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = limit + MultipartOverheadBytes;
        if (http.Request.ContentLength > limit + MultipartOverheadBytes) return TooLarge(limit);
        if (!http.Request.HasFormContentType) return Error.Validation("Send the document as multipart/form-data with a \"file\" field.").ToProblem();

        var form = await http.Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Error.Validation("No file was uploaded.").ToProblem();
        if (file.Length > limit) return TooLarge(limit);
        if (!KnowledgeBaseService.IsSupportedExtension(Path.GetExtension(file.FileName)))
        {
            return Error.Validation($"Unsupported file type. Supported: {KnowledgeBaseService.SupportedFileTypesDescription}.").ToProblem();
        }

        var kind = DocumentKind.Other;
        if (!string.IsNullOrWhiteSpace(form["kind"]) && (!Enum.TryParse(form["kind"], ignoreCase: true, out kind) || !Enum.IsDefined(kind)))
        {
            return Error.Validation("Unknown document kind.").ToProblem();
        }

        if (!TryParseOptional(form["vehicleId"], Guid.TryParse, out Guid? vehicleId) ||
            !TryParseOptional(form["yearFrom"], TryParseInt, out int? yearFrom) ||
            !TryParseOptional(form["yearTo"], TryParseInt, out int? yearTo))
        {
            return Error.Validation("vehicleId, yearFrom, and yearTo must be valid values when provided.").ToProblem();
        }

        var options = new DocumentUploadOptions
        {
            Title = Blank(form["title"]),
            Kind = kind,
            VehicleId = vehicleId,
            Make = Blank(form["make"]),
            Model = Blank(form["model"]),
            YearFrom = yearFrom,
            YearTo = yearTo,
            Tags = (Blank(form["tags"]) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Description = Blank(form["description"]),
        };

        await using var stream = file.OpenReadStream();
        var result = await service.UploadAsync(stream, file.FileName, options, ct);
        return result.ToHttp(id => TypedResults.Accepted($"/api/knowledge/documents/{id}", new CreatedResponse(id)));
    }

    private static IResult TooLarge(long limit) => TypedResults.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: ResultMapping.TitleFor(StatusCodes.Status413PayloadTooLarge),
        detail: $"Documents larger than {limit / (1024 * 1024)} MB are not accepted by this server.");

    private delegate bool TryParser<T>(string? value, out T result);

    private static bool TryParseInt(string? value, out int result) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryParseOptional<T>(string? value, TryParser<T> parse, out T? result) where T : struct
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!parse(value.Trim(), out var parsed)) return false;
        result = parsed;
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
