using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public sealed record ImageAnalysisResult
{
    public string LikelyComponent { get; init; } = "Unable to identify";

    /// <summary>low, moderate, or high.</summary>
    public string Confidence { get; init; } = "low";

    public IReadOnlyList<string> VisualEvidence { get; init; } = [];

    public IReadOnlyList<string> AlternativeIdentifications { get; init; } = [];

    public IReadOnlyList<string> ObservedConditions { get; init; } = [];

    public IReadOnlyList<string> RecommendedVerification { get; init; } = [];

    public IReadOnlyList<string> SafetyNotes { get; init; } = [];

    public string? Limitations { get; init; }

    public string Model { get; init; } = string.Empty;

    public DateTime AnalyzedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Unstructured model output, when the model did not return valid JSON.</summary>
    public string? RawText { get; init; }

    public string? Question { get; init; }
}

public sealed record VinScanResult(IReadOnlyList<string> Candidates, string Method, string? RecognizedText);

/// <summary>
/// Photo analysis with a vision model. Results are always AI inference with an explicit
/// confidence level and a verification step; ambiguous images yield "low" confidence.
/// </summary>
public sealed class ImageAnalysisService(
    IAiRouter router,
    IAppDbContextFactory dbFactory,
    IFileStore files,
    ILogger<ImageAnalysisService> logger,
    IImageProcessor? imageProcessor = null,
    IOcrEngine? ocr = null,
    IBarcodeReader? barcodeReader = null)
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            likelyComponent = new { type = "string" },
            confidence = new { type = "string", @enum = new[] { "low", "moderate", "high" } },
            visualEvidence = new { type = "array", items = new { type = "string" } },
            alternativeIdentifications = new { type = "array", items = new { type = "string" } },
            observedConditions = new { type = "array", items = new { type = "string" } },
            recommendedVerification = new { type = "array", items = new { type = "string" } },
            safetyNotes = new { type = "array", items = new { type = "string" } },
            limitations = new { type = "string" },
        },
        required = new[] { "likelyComponent", "confidence", "visualEvidence", "recommendedVerification" },
    });

    public async Task<Result<ImageAnalysisResult>> AnalyzeAsync(byte[] image, string mediaType, string? question, string? vehicleContext,
        CancellationToken ct = default)
    {
        if (image.Length == 0) return Error.Validation("The image is empty.");
        var route = await router.ResolveChatAsync(AiTask.ImageAnalysis, DataSensitivity.Images, requireVision: true, cancellationToken: ct);
        if (!route.IsAvailable) return Error.NotConfigured(route.UnavailableReason!);

        var data = image;
        var type = mediaType;
        if (imageProcessor is { IsAvailable: true })
        {
            var prepared = await imageProcessor.PrepareForAnalysisAsync(image, 1568, ct);
            data = prepared.Data;
            type = prepared.MediaType;
        }

        var prompt = "Analyze this photo from the shop floor." +
                     (string.IsNullOrWhiteSpace(vehicleContext) ? string.Empty : $" Vehicle: {vehicleContext}.") +
                     (string.IsNullOrWhiteSpace(question) ? string.Empty : $" The technician asks: {question}");

        var completion = await route.Model!.CompleteAsync(new ChatRequest
        {
            SystemPrompt = Prompts.ImageAnalysis,
            Messages = [ChatMessage.User(prompt, [new ChatImage(data, type)])],
            Format = ResponseFormat.Json,
            JsonSchema = Schema,
            Temperature = 0.1,
        }, ct);

        var model = $"{route.Model!.Provider} · {route.Model.Model}";
        var json = Json.ExtractJson(completion.Text);
        if (json is not null && Json.TryDeserialize<ImageAnalysisResult>(json, out var parsed) && parsed is not null)
        {
            var rawConfidence = (parsed.Confidence ?? "low").Trim().ToLowerInvariant();
            var confidence = rawConfidence is "high" or "moderate" or "low" ? rawConfidence : "low";
            var safety = (parsed.SafetyNotes ?? []).ToList();
            foreach (var warning in SafetyAdvisor.ForText(string.Join(' ', (parsed.ObservedConditions ?? []).Append(parsed.LikelyComponent ?? string.Empty))))
            {
                if (!safety.Any(s => s.Contains(warning.Title, StringComparison.OrdinalIgnoreCase))) safety.Add($"{warning.Title}: {warning.Message}");
            }

            return parsed with { Confidence = confidence, Model = model, AnalyzedUtc = DateTime.UtcNow, SafetyNotes = safety, Question = question };
        }

        logger.LogInformation("Vision model returned non-JSON output; presenting raw text");
        return new ImageAnalysisResult
        {
            LikelyComponent = "See description",
            Confidence = "low",
            RawText = completion.Text,
            RecommendedVerification = ["Verify any identification by part number and routing before acting on it."],
            Model = model,
            Question = question,
        };
    }

    public async Task<Result<Guid>> AttachPhotoAsync(Stream content, string fileName, Guid? vehicleId, Guid? sessionId, string? caption,
        CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".heic" or ".tif" or ".tiff"))
        {
            return Error.Validation("Photos must be JPEG, PNG, BMP, WebP, HEIC, or TIFF.");
        }

        StoredFile stored;
        try
        {
            stored = await files.SaveAsync(content, fileName, FileArea.Media, ct);
        }
        catch (InvalidDataException ex)
        {
            return Error.Validation(ex.Message);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var attachment = new MediaAttachment
        {
            VehicleId = vehicleId,
            DiagnosticSessionId = sessionId,
            Kind = AttachmentKind.Photo,
            FileName = Path.GetFileName(fileName),
            StoredFileName = stored.StoredFileName,
            ContentType = KnowledgeBaseService.ContentTypeFor(extension),
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            Caption = caption,
            TakenUtc = DateTime.UtcNow,
        };
        db.MediaAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return attachment.Id;
    }

    public async Task<Result<ImageAnalysisResult>> AnalyzeAttachmentAsync(Guid attachmentId, string? question, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var attachment = await db.MediaAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return Error.NotFound("Photo");
        var vehicle = attachment.VehicleId is { } vid ? await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct) : null;
        var bytes = await File.ReadAllBytesAsync(files.GetFullPath(attachment.StoredFileName, FileArea.Media), ct);
        var result = await AnalyzeAsync(bytes, attachment.ContentType, question, vehicle?.Description, ct);
        if (result.IsFailure) return result;
        attachment.AnalysisJson = Json.Serialize(result.Value);
        attachment.AnalyzedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<MediaAttachment>> ListPhotosAsync(Guid? vehicleId, Guid? sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.MediaAttachments.AsNoTracking().Where(a => a.Kind == AttachmentKind.Photo);
        if (vehicleId is { } v) query = query.Where(a => a.VehicleId == v);
        if (sessionId is { } s) query = query.Where(a => a.DiagnosticSessionId == s);
        return await query.OrderByDescending(a => a.CreatedUtc).Take(200).ToListAsync(ct);
    }

    public string GetPhotoPath(MediaAttachment attachment) => files.GetFullPath(attachment.StoredFileName, FileArea.Media);

    public async Task<Result> DeletePhotoAsync(Guid attachmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var attachment = await db.MediaAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return Error.NotFound("Photo");
        db.MediaAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await files.DeleteAsync(attachment.StoredFileName, FileArea.Media, ct);
        return Result.Success();
    }

    /// <summary>
    /// Reads a VIN from a photo of a VIN plate, door-jamb label, or registration: barcodes
    /// first (exact), then OCR. Only candidates that pass structural validation (and the
    /// check digit, for North American VINs) are returned.
    /// </summary>
    public async Task<Result<VinScanResult>> ScanVinAsync(byte[] image, CancellationToken ct = default)
    {
        if (imageProcessor is not { IsAvailable: true }) return Error.NotConfigured("Image decoding is not available on this system.");

        if (barcodeReader is not null)
        {
            var pixels = await imageProcessor.DecodePixelsAsync(image, 2400, ct);
            var decoded = barcodeReader.Decode(pixels);
            var fromBarcode = decoded.SelectMany(Vin.FindCandidates).Select(v => v.Value).Distinct().ToList();
            if (fromBarcode.Count > 0) return new VinScanResult(fromBarcode, "Barcode", string.Join(" | ", decoded));
        }

        if (ocr is { IsAvailable: true })
        {
            var text = await ocr.RecognizeImageAsync(image, ct);
            var fromOcr = Vin.FindCandidates(text.Text).Select(v => v.Value).Distinct().ToList();
            return new VinScanResult(fromOcr, "Text recognition (OCR)", text.Text);
        }

        return new VinScanResult([], "None", null);
    }
}
