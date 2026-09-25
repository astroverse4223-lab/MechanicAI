using System.Text;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.KnowledgeBase;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Application.Services;

public sealed record DocumentUploadOptions
{
    public string? Title { get; init; }

    public DocumentKind Kind { get; init; } = DocumentKind.Other;

    public Guid? VehicleId { get; init; }

    public string? Make { get; init; }

    public string? Model { get; init; }

    public int? YearFrom { get; init; }

    public int? YearTo { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Description { get; init; }
}

public sealed record KnowledgeSearchOptions
{
    public int Limit { get; init; } = 10;

    public DocumentKind? Kind { get; init; }

    public Guid? DocumentId { get; init; }

    public int? VehicleYear { get; init; }

    public string? VehicleMake { get; init; }

    public string? VehicleModel { get; init; }
}

public sealed record KnowledgeHit(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    DocumentKind Kind,
    int PageNumber,
    string? Heading,
    string Text,
    double Score,
    string MatchType,
    int CharStart,
    int CharEnd);

public sealed record DocumentAnswer(
    string Markdown,
    IReadOnlyList<KnowledgeHit> Passages,
    IReadOnlyList<SourceCitation> Sources,
    IReadOnlyList<string> Warnings,
    string? Model,
    bool AnsweredByAi);

/// <summary>Quick-question templates with retrieval query expansion.</summary>
public enum DocumentQuestionTemplate
{
    Free, TorqueSpecification, ComponentLocation, ResistanceSpecification, WiringInformation,
    RemovalProcedure, InstallationProcedure, FluidCapacity, AdjustmentProcedure,
}

/// <summary>
/// The technician's private knowledge base: upload → validate → extract (text layer or OCR)
/// → chunk → keyword index (+ embeddings when a model is available) → hybrid retrieval →
/// grounded answers that cite document and page.
/// </summary>
public sealed class KnowledgeBaseService(
    IAppDbContextFactory dbFactory,
    IFileStore files,
    IDocumentTextExtractor extractor,
    IKeywordIndex keywordIndex,
    IVectorIndex vectorIndex,
    IAiRouter router,
    IBackgroundTaskQueue queue,
    ILogger<KnowledgeBaseService> logger,
    IOcrEngine? ocr = null)
{
    public const long MaxUploadBytes = 300L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".csv", ".log" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp" };

    public event EventHandler<Guid>? DocumentChanged;

    public static bool IsSupportedExtension(string extension) =>
        extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || TextExtensions.Contains(extension) || ImageExtensions.Contains(extension);

    public static string SupportedFileTypesDescription => "PDF, images (PNG, JPEG, TIFF, BMP, WebP), and text/Markdown files";

    public async Task<Result<Guid>> UploadAsync(Stream content, string fileName, DocumentUploadOptions options, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName);
        if (!IsSupportedExtension(extension)) return Error.Validation($"Unsupported file type '{extension}'. Supported: {SupportedFileTypesDescription}.");

        // Validate content by signature, not just by extension.
        var head = new byte[16];
        var buffered = new MemoryStream();
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        if (read == 0) return Error.Validation("The file is empty.");
        if (!SignatureMatches(extension, head.AsSpan(0, read)))
        {
            return Error.Validation("The file contents do not match its type. It may be damaged or mislabeled.");
        }

        await buffered.WriteAsync(head.AsMemory(0, read), ct);
        await content.CopyToAsync(buffered, ct);
        if (buffered.Length > MaxUploadBytes) return Error.Validation($"Files larger than {MaxUploadBytes / (1024 * 1024)} MB are not supported.");
        if (TextExtensions.Contains(extension) && ContainsNul(buffered)) return Error.Validation("This does not look like a text file.");
        buffered.Position = 0;

        StoredFile stored;
        try
        {
            stored = await files.SaveAsync(buffered, fileName, FileArea.Documents, ct);
        }
        catch (InvalidDataException ex)
        {
            return Error.Validation(ex.Message);
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var duplicate = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Sha256 == stored.Sha256, ct);
        if (duplicate is not null)
        {
            await files.DeleteAsync(stored.StoredFileName, FileArea.Documents, ct);
            return Error.Validation($"This file is already in your knowledge base as \"{duplicate.Title}\".");
        }

        var document = new Document
        {
            Title = string.IsNullOrWhiteSpace(options.Title) ? Path.GetFileNameWithoutExtension(fileName) : options.Title.Trim(),
            FileName = Path.GetFileName(fileName),
            StoredFileName = stored.StoredFileName,
            ContentType = ContentTypeFor(extension),
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            Kind = ImageExtensions.Contains(extension) && options.Kind == DocumentKind.Other ? DocumentKind.Photo : options.Kind,
            VehicleId = options.VehicleId,
            Make = options.Make,
            Model = options.Model,
            YearFrom = options.YearFrom,
            YearTo = options.YearTo,
            Tags = options.Tags.ToList(),
            Description = options.Description,
            Status = DocumentStatus.Pending,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Document {DocumentId} uploaded ({Bytes} bytes, {Type})", document.Id, stored.SizeBytes, document.ContentType);
        await QueueIndexingAsync(document.Id, document.Title, ct);
        DocumentChanged?.Invoke(this, document.Id);
        return document.Id;
    }

    public async Task QueueIndexingAsync(Guid documentId, string title, CancellationToken ct = default)
    {
        await queue.QueueAsync(new BackgroundWorkItem($"Indexing \"{title}\"", async (sp, token) =>
        {
            var service = (KnowledgeBaseService?)sp.GetService(typeof(KnowledgeBaseService)) ?? this;
            await service.IndexDocumentAsync(documentId, token);
        }, documentId), ct);
    }

    public async Task IndexDocumentAsync(Guid documentId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return;
        document.Status = DocumentStatus.Processing;
        document.StatusMessage = "Extracting text…";
        await db.SaveChangesAsync(ct);
        DocumentChanged?.Invoke(this, documentId);

        try
        {
            var path = files.GetFullPath(document.StoredFileName, FileArea.Documents);
            var extension = Path.GetExtension(document.StoredFileName);
            IReadOnlyList<ExtractedPage> pages;
            var usedOcr = false;

            if (ImageExtensions.Contains(extension))
            {
                if (ocr is not { IsAvailable: true })
                {
                    await FinishAsync(db, document, DocumentStatus.NeedsOcr, "Text recognition (OCR) is not available on this system.", 1, 0, false, null, ct);
                    return;
                }

                var result = await ocr.RecognizeImageAsync(await File.ReadAllBytesAsync(path, ct), ct);
                pages = [new ExtractedPage(1, result.Text, result.Width, result.Height, result.Words, true)];
                usedOcr = true;
            }
            else
            {
                var extracted = await extractor.ExtractAsync(path, null, ct);
                pages = extracted.Pages;
                if (string.IsNullOrWhiteSpace(document.Title) && extracted.Title is not null) document.Title = extracted.Title;

                var needOcr = extracted.PagesNeedingOcr.ToList();
                if (needOcr.Count > 0 && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && ocr is { IsAvailable: true })
                {
                    document.StatusMessage = $"Running OCR on {needOcr.Count} scanned page(s)…";
                    await db.SaveChangesAsync(ct);
                    var replaced = pages.ToList();
                    foreach (var number in needOcr)
                    {
                        ct.ThrowIfCancellationRequested();
                        var ocrPage = await ocr.RecognizePdfPageAsync(path, number, ct);
                        if (ocrPage is null || string.IsNullOrWhiteSpace(ocrPage.Text)) continue;
                        var index = replaced.FindIndex(p => p.PageNumber == number);
                        if (index >= 0) replaced[index] = new ExtractedPage(number, ocrPage.Text, ocrPage.Width, ocrPage.Height, ocrPage.Words, true);
                        usedOcr = true;
                    }

                    pages = replaced;
                }
            }

            // Replace any previous extraction.
            await db.DocumentChunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);
            await db.DocumentPages.Where(p => p.DocumentId == documentId).ExecuteDeleteAsync(ct);
            await keywordIndex.RemoveDocumentAsync(documentId, ct);

            foreach (var page in pages)
            {
                db.DocumentPages.Add(new DocumentPage
                {
                    DocumentId = documentId,
                    PageNumber = page.PageNumber,
                    Text = page.Text,
                    Width = page.Width,
                    Height = page.Height,
                    FromOcr = page.FromOcr,
                    Words = page.Words.ToList(),
                });
            }

            var chunks = DocumentChunker.Chunk(documentId, pages);
            db.DocumentChunks.AddRange(chunks);
            document.StatusMessage = $"Indexing {chunks.Count} passages…";
            await db.SaveChangesAsync(ct);
            await keywordIndex.IndexChunksAsync(chunks, document.Title, ct);

            if (chunks.Count == 0)
            {
                await FinishAsync(db, document, DocumentStatus.NeedsOcr,
                    ocr is { IsAvailable: true } ? "No readable text was found." : "No text layer found. OCR is not available on this system.",
                    pages.Count, 0, usedOcr, null, ct);
                return;
            }

            string? embeddingModel = null;
            try
            {
                embeddingModel = await EmbedChunksAsync(db, document, chunks, ct);
            }
            catch (ExternalServiceException ex)
            {
                logger.LogWarning("Embedding failed for document {DocumentId}: {Message}", documentId, ex.UserMessage);
            }

            await FinishAsync(db, document, embeddingModel is null ? DocumentStatus.ReadyKeywordOnly : DocumentStatus.Ready,
                embeddingModel is null ? "Keyword search only — no embedding model available. Re-index after installing one for semantic search." : null,
                pages.Count, chunks.Count, usedOcr, embeddingModel, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing failed for document {DocumentId}", documentId);
            var message = ex is ExternalServiceException ese ? ese.UserMessage : "Processing failed. The file may be damaged or unsupported.";
            await FinishAsync(db, document, DocumentStatus.Failed, message, document.PageCount, 0, false, null, CancellationToken.None);
        }
    }

    private async Task<string?> EmbedChunksAsync(IAppDbContext db, Document document, IReadOnlyList<DocumentChunk> chunks, CancellationToken ct)
    {
        var model = await router.ResolveEmbeddingAsync(ct);
        if (model is null) return null;
        document.StatusMessage = $"Generating embeddings ({model.Model})…";
        await db.SaveChangesAsync(ct);

        await db.Embeddings.Where(e => e.DocumentId == document.Id).ExecuteDeleteAsync(ct);
        var inputs = chunks.Select(c => $"{document.Title}{(c.Heading is null ? string.Empty : " — " + c.Heading)}\n{c.Text}").ToList();
        var vectors = await model.EmbedAsync(inputs, ct);
        var embeddings = chunks.Zip(vectors, (chunk, vector) => new ChunkEmbedding
        {
            ChunkId = chunk.Id,
            DocumentId = document.Id,
            Model = model.Model,
            Dimensions = vector.Length,
            Vector = vector,
        }).ToList();
        db.Embeddings.AddRange(embeddings);
        await db.SaveChangesAsync(ct);
        await vectorIndex.UpsertAsync(embeddings, ct);
        return model.Model;
    }

    private async Task FinishAsync(IAppDbContext db, Document document, DocumentStatus status, string? message, int pageCount, int chunkCount,
        bool usedOcr, string? embeddingModel, CancellationToken ct)
    {
        document.Status = status;
        document.StatusMessage = message;
        document.PageCount = pageCount;
        document.ChunkCount = chunkCount;
        document.UsedOcr = usedOcr;
        document.EmbeddingModel = embeddingModel;
        document.IndexedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        DocumentChanged?.Invoke(this, document.Id);
    }

    public async Task<Result> ReindexAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return Error.NotFound("Document");
        document.Status = DocumentStatus.Pending;
        document.StatusMessage = "Queued for re-indexing";
        await db.SaveChangesAsync(ct);
        await QueueIndexingAsync(documentId, document.Title, ct);
        DocumentChanged?.Invoke(this, documentId);
        return Result.Success();
    }

    public async Task<Result> UpdateMetadataAsync(Guid documentId, DocumentUploadOptions options, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return Error.NotFound("Document");
        if (!string.IsNullOrWhiteSpace(options.Title)) document.Title = options.Title.Trim();
        document.Kind = options.Kind;
        document.VehicleId = options.VehicleId;
        document.Make = options.Make;
        document.Model = options.Model;
        document.YearFrom = options.YearFrom;
        document.YearTo = options.YearTo;
        document.Tags = options.Tags.ToList();
        document.Description = options.Description;
        await db.SaveChangesAsync(ct);
        DocumentChanged?.Invoke(this, documentId);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return Error.NotFound("Document");
        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);
        await keywordIndex.RemoveDocumentAsync(documentId, ct);
        await vectorIndex.RemoveDocumentAsync(documentId, ct);
        await files.DeleteAsync(document.StoredFileName, FileArea.Documents, ct);
        DocumentChanged?.Invoke(this, documentId);
        return Result.Success();
    }

    public async Task<IReadOnlyList<Document>> ListAsync(DocumentKind? kind = null, Guid? vehicleId = null, string? search = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var query = db.Documents.AsNoTracking();
        if (kind is { } k) query = query.Where(d => d.Kind == k);
        if (vehicleId is { } v) query = query.Where(d => d.VehicleId == v);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim() + "%";
            query = query.Where(d => EF.Functions.Like(d.Title, pattern) || EF.Functions.Like(d.FileName, pattern));
        }

        return await query.OrderByDescending(d => d.CreatedUtc).ToListAsync(ct);
    }

    public async Task<Document?> GetAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
    }

    public async Task<DocumentPage?> GetPageAsync(Guid documentId, int pageNumber, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.DocumentPages.AsNoTracking().FirstOrDefaultAsync(p => p.DocumentId == documentId && p.PageNumber == pageNumber, ct);
    }

    public string GetFilePath(Document document) => files.GetFullPath(document.StoredFileName, FileArea.Documents);

    /// <summary>Hybrid retrieval: BM25 keyword hits fused with semantic (embedding) hits by reciprocal rank.</summary>
    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, KnowledgeSearchOptions? options = null, CancellationToken ct = default)
    {
        options ??= new KnowledgeSearchOptions();
        if (string.IsNullOrWhiteSpace(query)) return [];
        var candidateCount = Math.Max(40, options.Limit * 4);

        var keywordHits = await keywordIndex.SearchAsync(query, candidateCount, ct);
        IReadOnlyList<VectorHit> vectorHits = [];
        try
        {
            var embedder = await router.ResolveEmbeddingAsync(ct);
            if (embedder is not null && await vectorIndex.CountAsync(embedder.Model, ct) > 0)
            {
                var vectors = await embedder.EmbedAsync([query], ct);
                vectorHits = await vectorIndex.SearchAsync(vectors[0], embedder.Model, candidateCount, ct);
            }
        }
        catch (ExternalServiceException ex)
        {
            logger.LogInformation("Semantic search unavailable ({Message}); using keyword results", ex.UserMessage);
        }

        const double k = 60;
        var fused = new Dictionary<Guid, (double Score, bool Keyword, bool Semantic)>();
        for (var i = 0; i < keywordHits.Count; i++)
        {
            var hit = keywordHits[i];
            var current = fused.GetValueOrDefault(hit.ChunkId);
            fused[hit.ChunkId] = (current.Score + 1.0 / (k + i + 1), true, current.Semantic);
        }

        for (var i = 0; i < vectorHits.Count; i++)
        {
            var hit = vectorHits[i];
            if (hit.Similarity < 0.2) continue;
            var current = fused.GetValueOrDefault(hit.ChunkId);
            fused[hit.ChunkId] = (current.Score + 1.0 / (k + i + 1), current.Keyword, true);
        }

        if (fused.Count == 0) return [];
        var ids = fused.Keys.ToList();
        await using var db = await dbFactory.CreateAsync(ct);
        var chunks = await db.DocumentChunks.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        var documentIds = chunks.Select(c => c.DocumentId).Distinct().ToList();
        var documents = await db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);

        return chunks
            .Where(c => documents.ContainsKey(c.DocumentId))
            .Where(c => options.DocumentId is null || c.DocumentId == options.DocumentId)
            .Where(c => options.Kind is null || documents[c.DocumentId].Kind == options.Kind)
            .Where(c => documents[c.DocumentId].AppliesTo(options.VehicleYear, options.VehicleMake, options.VehicleModel))
            .Select(c =>
            {
                var f = fused[c.Id];
                var d = documents[c.DocumentId];
                return new KnowledgeHit(c.Id, c.DocumentId, d.Title, d.Kind, c.PageNumber, c.Heading, c.Text, Math.Round(f.Score * 1000, 3),
                    f.Keyword && f.Semantic ? "keyword + semantic" : f.Semantic ? "semantic" : "keyword", c.CharStart, c.CharEnd);
            })
            .OrderByDescending(h => h.Score)
            .Take(options.Limit)
            .ToList();
    }

    public static string ExpandQuery(DocumentQuestionTemplate template, string question) => template switch
    {
        DocumentQuestionTemplate.TorqueSpecification => $"{question} torque specification tighten N·m lb-ft in-lb",
        DocumentQuestionTemplate.ComponentLocation => $"{question} location located component locator view",
        DocumentQuestionTemplate.ResistanceSpecification => $"{question} resistance ohms specification terminals",
        DocumentQuestionTemplate.WiringInformation => $"{question} wiring circuit connector terminal pin wire color ground",
        DocumentQuestionTemplate.RemovalProcedure => $"{question} removal remove disconnect procedure",
        DocumentQuestionTemplate.InstallationProcedure => $"{question} installation install procedure tighten",
        DocumentQuestionTemplate.FluidCapacity => $"{question} capacity fluid oil coolant quarts liters",
        DocumentQuestionTemplate.AdjustmentProcedure => $"{question} adjustment adjust procedure clearance specification",
        _ => question,
    };

    public static string TemplateLabel(DocumentQuestionTemplate template) => template switch
    {
        DocumentQuestionTemplate.TorqueSpecification => "Find torque specification",
        DocumentQuestionTemplate.ComponentLocation => "Find component location",
        DocumentQuestionTemplate.ResistanceSpecification => "Find resistance specification",
        DocumentQuestionTemplate.WiringInformation => "Find wiring information",
        DocumentQuestionTemplate.RemovalProcedure => "Find removal procedure",
        DocumentQuestionTemplate.InstallationProcedure => "Find installation procedure",
        DocumentQuestionTemplate.FluidCapacity => "Find fluid capacity",
        DocumentQuestionTemplate.AdjustmentProcedure => "Find adjustment procedure",
        _ => "Ask a question",
    };

    /// <summary>
    /// Answers strictly from the technician's documents. If nothing relevant is retrieved the
    /// answer says so without calling the AI. Private documents are routed to a local model
    /// unless the Privacy settings allow cloud processing.
    /// </summary>
    public async Task<Result<DocumentAnswer>> AskAsync(string question, DocumentQuestionTemplate template, KnowledgeSearchOptions? options,
        Func<string, Task>? onText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return Error.Validation("Enter a question.");
        var hits = await SearchAsync(ExpandQuery(template, question), (options ?? new KnowledgeSearchOptions()) with { Limit = 8 }, ct);
        if (hits.Count == 0)
        {
            return new DocumentAnswer("**Not found in your documents.** No passage matched this question. Try different wording, check that the right manual is uploaded and indexed, or search service information.",
                [], [], [], null, false);
        }

        var registry = new SourceRegistry();
        var context = new StringBuilder();
        foreach (var hit in hits)
        {
            var citation = registry.Register(new SourceCitation
            {
                Title = $"{hit.DocumentTitle}, page {hit.PageNumber}",
                Type = SourceType.PrivateDocument,
                DocumentId = hit.DocumentId,
                PageNumber = hit.PageNumber,
                ChunkId = hit.ChunkId,
                Excerpt = Text.Truncate(hit.Text, 300),
            });
            context.Append('[').Append(citation.Label).Append("] ").Append(hit.DocumentTitle).Append(" — page ").Append(hit.PageNumber);
            if (hit.Heading is not null) context.Append(" — ").Append(hit.Heading);
            context.Append('\n').Append(hit.Text).Append("\n\n");
        }

        var route = await router.ResolveChatAsync(AiTask.DocumentQuestion, DataSensitivity.PrivateDocuments, cancellationToken: ct);
        if (!route.IsAvailable)
        {
            return new DocumentAnswer($"AI is unavailable ({route.UnavailableReason}). The most relevant passages from your documents are shown below.",
                hits, registry.Sources, [], null, false);
        }

        var sb = new StringBuilder();
        await foreach (var update in route.Model!.StreamAsync(new ChatRequest
                       {
                           SystemPrompt = Prompts.DocumentQuestion,
                           Messages = [ChatMessage.User($"Question ({TemplateLabel(template)}): {question}\n\nPassages:\n{context}")],
                           Temperature = 0.0,
                       }, ct))
        {
            if (update is not TextDeltaUpdate t) continue;
            sb.Append(t.Text);
            if (onText is not null) await onText(t.Text);
        }

        var report = CitationValidator.Validate(sb.ToString(), registry);
        return new DocumentAnswer(report.CleanedText, hits, registry.Sources, report.Warnings, $"{route.Model.Provider} · {route.Model.Model}", true);
    }

    public async Task<(int Documents, int Ready, int Chunks, int Embedded)> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var documents = await db.Documents.CountAsync(ct);
        var ready = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Ready || d.Status == DocumentStatus.ReadyKeywordOnly, ct);
        var chunks = await db.DocumentChunks.CountAsync(ct);
        var embedded = await db.Embeddings.CountAsync(ct);
        return (documents, ready, chunks, embedded);
    }

    private static bool SignatureMatches(string extension, ReadOnlySpan<byte> head)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8),
            ".png" => head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
            ".bmp" => head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M',
            ".tif" or ".tiff" => head.Length >= 4 && ((head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x2A && head[3] == 0x00) ||
                                                   (head[0] == 0x4D && head[1] == 0x4D && head[2] == 0x00 && head[3] == 0x2A)),
            ".webp" => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8),
            _ => true,
        };
    }

    private static bool ContainsNul(MemoryStream stream)
    {
        var buffer = stream.GetBuffer();
        var length = (int)Math.Min(stream.Length, 8192);
        return Array.IndexOf(buffer, (byte)0, 0, length) >= 0;
    }

    public static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        ".md" or ".markdown" => "text/markdown",
        ".csv" => "text/csv",
        _ => "text/plain",
    };
}
