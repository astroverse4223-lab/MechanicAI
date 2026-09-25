using MechanicAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Abstractions;

/// <summary>
/// Unit of work over the application database. Implemented by EF Core for SQLite
/// (desktop, offline-first) and PostgreSQL (shop server). Contexts are short-lived:
/// create one per operation through <see cref="IAppDbContextFactory"/>.
/// </summary>
public interface IAppDbContext : IDisposable, IAsyncDisposable
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Technician> Technicians { get; }
    DbSet<AuditLogEntry> AuditLog { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Vehicle> Vehicles { get; }
    DbSet<VehicleSpecification> VehicleSpecifications { get; }
    DbSet<Recall> Recalls { get; }
    DbSet<DiagnosticSession> DiagnosticSessions { get; }
    DbSet<SessionDtc> SessionDtcs { get; }
    DbSet<DiagnosticNode> DiagnosticNodes { get; }
    DbSet<DiagnosticTest> DiagnosticTests { get; }
    DbSet<DiagnosticStep> DiagnosticSteps { get; }
    DbSet<DtcDefinition> Dtcs { get; }
    DbSet<Repair> Repairs { get; }
    DbSet<Part> Parts { get; }
    DbSet<Note> Notes { get; }
    DbSet<MediaAttachment> MediaAttachments { get; }
    DbSet<Estimate> Estimates { get; }
    DbSet<EstimateLine> EstimateLines { get; }
    DbSet<Inspection> Inspections { get; }
    DbSet<InspectionItem> InspectionItems { get; }
    DbSet<Document> Documents { get; }
    DbSet<DocumentPage> DocumentPages { get; }
    DbSet<DocumentChunk> DocumentChunks { get; }
    DbSet<ChunkEmbedding> Embeddings { get; }
    DbSet<AiConversation> AiConversations { get; }
    DbSet<AiConversationMessage> AiConversationMessages { get; }
    DbSet<SearchHistoryEntry> SearchHistory { get; }
    DbSet<SavedSearch> SavedSearches { get; }
    DbSet<Bookmark> Bookmarks { get; }
    DbSet<WebSource> WebSources { get; }
    DbSet<CachedResponse> CachedResponses { get; }
    DbSet<TrainingCourse> TrainingCourses { get; }
    DbSet<TrainingLesson> TrainingLessons { get; }
    DbSet<TrainingQuiz> TrainingQuizzes { get; }
    DbSet<Flashcard> Flashcards { get; }
    DbSet<TrainingAttempt> TrainingAttempts { get; }
    DbSet<TrainingScenario> TrainingScenarios { get; }
    DbSet<LiveDataSession> LiveDataSessions { get; }
    DbSet<LiveDataSample> LiveDataSamples { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IAppDbContextFactory
{
    Task<IAppDbContext> CreateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Full-text keyword index over document chunks (SQLite FTS5 on desktop). Used alone when
/// no embedding model is available, and fused with vector search otherwise.
/// </summary>
public interface IKeywordIndex
{
    Task IndexChunksAsync(IReadOnlyList<DocumentChunk> chunks, string documentTitle, CancellationToken ct);

    Task RemoveDocumentAsync(Guid documentId, CancellationToken ct);

    Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, int limit, CancellationToken ct);

    Task IndexDtcsAsync(IReadOnlyList<DtcDefinition> definitions, CancellationToken ct);

    Task<IReadOnlyList<string>> SearchDtcCodesAsync(string query, int limit, CancellationToken ct);
}

public sealed record KeywordHit(Guid ChunkId, Guid DocumentId, double Score);

/// <summary>Nearest-neighbor search over chunk embeddings.</summary>
public interface IVectorIndex
{
    Task UpsertAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken ct);

    Task RemoveDocumentAsync(Guid documentId, CancellationToken ct);

    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] query, string model, int limit, CancellationToken ct);

    Task<int> CountAsync(string model, CancellationToken ct);

    void Invalidate();
}

public sealed record VectorHit(Guid ChunkId, Guid DocumentId, double Similarity);
