using MechanicAI.Application.Abstractions;
using MechanicAI.Domain.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MechanicAI.Infrastructure.Persistence;

/// <summary>
/// Shared EF Core model. Two provider-specific subclasses exist so each provider gets its
/// own migrations: <see cref="SqliteAppDbContext"/> (desktop, offline-first) and
/// <see cref="PostgresAppDbContext"/> (shop server, pgvector).
/// </summary>
public abstract class AppDbContext(DbContextOptions options) : DbContext(options), IAppDbContext
{
    protected abstract bool IsPostgres { get; }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<VehicleSpecification> VehicleSpecifications => Set<VehicleSpecification>();
    public DbSet<Recall> Recalls => Set<Recall>();
    public DbSet<DiagnosticSession> DiagnosticSessions => Set<DiagnosticSession>();
    public DbSet<SessionDtc> SessionDtcs => Set<SessionDtc>();
    public DbSet<DiagnosticNode> DiagnosticNodes => Set<DiagnosticNode>();
    public DbSet<DiagnosticTest> DiagnosticTests => Set<DiagnosticTest>();
    public DbSet<DiagnosticStep> DiagnosticSteps => Set<DiagnosticStep>();
    public DbSet<DtcDefinition> Dtcs => Set<DtcDefinition>();
    public DbSet<Repair> Repairs => Set<Repair>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<MediaAttachment> MediaAttachments => Set<MediaAttachment>();
    public DbSet<Estimate> Estimates => Set<Estimate>();
    public DbSet<EstimateLine> EstimateLines => Set<EstimateLine>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<InspectionItem> InspectionItems => Set<InspectionItem>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentPage> DocumentPages => Set<DocumentPage>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<ChunkEmbedding> Embeddings => Set<ChunkEmbedding>();
    public DbSet<AiConversation> AiConversations => Set<AiConversation>();
    public DbSet<AiConversationMessage> AiConversationMessages => Set<AiConversationMessage>();
    public DbSet<SearchHistoryEntry> SearchHistory => Set<SearchHistoryEntry>();
    public DbSet<SavedSearch> SavedSearches => Set<SavedSearch>();
    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();
    public DbSet<WebSource> WebSources => Set<WebSource>();
    public DbSet<CachedResponse> CachedResponses => Set<CachedResponse>();
    public DbSet<TrainingCourse> TrainingCourses => Set<TrainingCourse>();
    public DbSet<TrainingLesson> TrainingLessons => Set<TrainingLesson>();
    public DbSet<TrainingQuiz> TrainingQuizzes => Set<TrainingQuiz>();
    public DbSet<Flashcard> Flashcards => Set<Flashcard>();
    public DbSet<TrainingAttempt> TrainingAttempts => Set<TrainingAttempt>();
    public DbSet<TrainingScenario> TrainingScenarios => Set<TrainingScenario>();
    public DbSet<LiveDataSession> LiveDataSessions => Set<LiveDataSession>();
    public DbSet<LiveDataSample> LiveDataSamples => Set<LiveDataSample>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedUtc == default) entry.Entity.CreatedUtc = now;
                entry.Entity.UpdatedUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedUtc = now;
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as strings so the database stays readable and reordering an enum is safe.
        foreach (var enumType in typeof(Entity).Assembly.GetTypes().Where(t => t.IsEnum))
        {
            configurationBuilder.Properties(enumType).HaveConversion<string>().HaveMaxLength(48);
        }

        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        var jsonb = IsPostgres;

        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.HasOne(x => x.Technician).WithMany().HasForeignKey(x => x.TechnicianId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.RefreshTokens).WithOne(x => x.User).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Technician>(e =>
        {
            e.ToTable("Technicians");
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.Name);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.Property(x => x.FirstName).HasMaxLength(100);
            e.Property(x => x.LastName).HasMaxLength(100);
            e.Property(x => x.CompanyName).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Ignore(x => x.DisplayName);
            e.HasIndex(x => new { x.LastName, x.FirstName });
            e.HasIndex(x => x.Phone);
        });

        b.Entity<Vehicle>(e =>
        {
            e.ToTable("Vehicles");
            e.Property(x => x.Vin).HasMaxLength(17);
            e.Property(x => x.Make).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(96);
            e.Property(x => x.Trim).HasMaxLength(96);
            e.Property(x => x.Engine).HasMaxLength(160);
            e.Property(x => x.LicensePlate).HasMaxLength(20);
            e.Ignore(x => x.DisplayName);
            e.Ignore(x => x.Description);
            e.HasIndex(x => x.Vin);
            e.HasIndex(x => new { x.Year, x.Make, x.Model });
            e.HasIndex(x => x.LastAccessedUtc);
            e.HasOne(x => x.Customer).WithMany(c => c.Vehicles).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Specifications).WithOne().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Recalls).WithOne().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VehicleSpecification>(e =>
        {
            e.ToTable("VehicleSpecifications");
            e.Property(x => x.Category).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Source).HasMaxLength(128);
            e.Property(x => x.SourceKey).HasMaxLength(128);
            e.HasIndex(x => x.VehicleId);
        });

        b.Entity<Recall>(e =>
        {
            e.ToTable("Recalls");
            e.Property(x => x.CampaignNumber).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.VehicleId, x.CampaignNumber }).IsUnique();
        });

        b.Entity<DiagnosticSession>(e =>
        {
            e.ToTable("DiagnosticSessions");
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.VehicleDescription).HasMaxLength(256);
            e.Property(x => x.TechnicianName).HasMaxLength(128);
            e.Property(x => x.ClarifyingQuestions).HasJsonConversion(jsonb);
            e.Ignore(x => x.IsClosed);
            e.Ignore(x => x.Causes);
            e.Ignore(x => x.CompletedTests);
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.UpdatedUtc);
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Dtcs).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Nodes).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tests).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SessionDtc>(e =>
        {
            e.ToTable("SessionDtcs");
            e.Property(x => x.Code).HasMaxLength(8).IsRequired();
            e.Property(x => x.Source).HasMaxLength(64);
            e.Property(x => x.FreezeFrame).HasJsonConversion(jsonb);
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.Code);
        });

        b.Entity<DiagnosticNode>(e =>
        {
            e.ToTable("DiagnosticNodes");
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Sources).HasJsonConversion(jsonb);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<DiagnosticTest>(e =>
        {
            e.ToTable("DiagnosticTests");
            e.Property(x => x.Key).HasMaxLength(160);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Outcomes).HasJsonConversion(jsonb);
            e.Property(x => x.Sources).HasJsonConversion(jsonb);
            e.Ignore(x => x.SelectedOutcome);
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<DiagnosticStep>(e =>
        {
            e.ToTable("DiagnosticSteps");
            e.Property(x => x.Title).HasMaxLength(512);
            e.HasIndex(x => new { x.SessionId, x.Sequence });
        });

        b.Entity<DtcDefinition>(e =>
        {
            e.ToTable("DTCs");
            e.Property(x => x.Code).HasMaxLength(8).IsRequired();
            e.Property(x => x.Manufacturer).HasMaxLength(64);
            e.Property(x => x.Subsystem).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(512);
            e.Property(x => x.Source).HasMaxLength(256);
            e.Property(x => x.ContentVersion).HasMaxLength(64);
            e.HasIndex(x => x.Code);
            e.HasIndex(x => new { x.Code, x.Manufacturer });
        });

        b.Entity<Repair>(e =>
        {
            e.ToTable("Repairs");
            e.Property(x => x.Title).HasMaxLength(256);
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.DiagnosticSessionId);
            e.HasIndex(x => x.PerformedUtc);
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Parts).WithOne().HasForeignKey(x => x.RepairId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Part>(e =>
        {
            e.ToTable("Parts");
            e.Property(x => x.PartNumber).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(256);
            e.HasIndex(x => x.PartNumber);
        });

        b.Entity<Note>(e =>
        {
            e.ToTable("Notes");
            e.Property(x => x.Title).HasMaxLength(256);
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.DiagnosticSessionId);
            e.HasIndex(x => x.CustomerId);
        });

        b.Entity<MediaAttachment>(e =>
        {
            e.ToTable("MediaAttachments");
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.StoredFileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.DiagnosticSessionId);
            e.HasIndex(x => x.Sha256);
        });

        b.Entity<Estimate>(e =>
        {
            e.ToTable("Estimates");
            e.Property(x => x.Number).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.CustomerId);
            e.Ignore(x => x.Subtotal);
            e.Ignore(x => x.TaxableTotal);
            e.Ignore(x => x.Tax);
            e.Ignore(x => x.Total);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.EstimateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EstimateLine>(e =>
        {
            e.ToTable("EstimateLines");
            e.Property(x => x.Description).HasMaxLength(256);
            e.Property(x => x.PartNumber).HasMaxLength(64);
            e.Ignore(x => x.Total);
            e.HasIndex(x => x.EstimateId);
        });

        b.Entity<Inspection>(e =>
        {
            e.ToTable("Inspections");
            e.Property(x => x.TemplateName).HasMaxLength(128);
            e.HasIndex(x => x.VehicleId);
            e.HasOne(x => x.Vehicle).WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InspectionItem>(e =>
        {
            e.ToTable("InspectionItems");
            e.Property(x => x.Section).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.InspectionId);
        });

        b.Entity<Document>(e =>
        {
            e.ToTable("Documents");
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.StoredFileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Make).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(96);
            e.Property(x => x.EmbeddingModel).HasMaxLength(128);
            e.Ignore(x => x.IsWiringDiagram);
            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.Kind);
        });

        b.Entity<DocumentPage>(e =>
        {
            e.ToTable("DocumentPages");
            e.Property(x => x.Words).HasJsonConversion(jsonb);
            e.HasIndex(x => new { x.DocumentId, x.PageNumber }).IsUnique();
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DocumentChunk>(e =>
        {
            e.ToTable("DocumentChunks");
            e.Property(x => x.Heading).HasMaxLength(256);
            e.HasIndex(x => new { x.DocumentId, x.Ordinal });
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChunkEmbedding>(e =>
        {
            e.ToTable("Embeddings");
            e.Property(x => x.Model).HasMaxLength(128).IsRequired();
            e.HasIndex(x => new { x.ChunkId, x.Model }).IsUnique();
            e.HasIndex(x => new { x.Model, x.DocumentId });
            e.HasOne<DocumentChunk>().WithMany().HasForeignKey(x => x.ChunkId).OnDelete(DeleteBehavior.Cascade);
            ConfigureVector(e.Property(x => x.Vector));
        });

        b.Entity<AiConversation>(e =>
        {
            e.ToTable("AIConversations");
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(128);
            e.HasIndex(x => x.UpdatedUtc);
            e.HasIndex(x => x.DiagnosticSessionId);
            e.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiConversationMessage>(e =>
        {
            e.ToTable("AIConversationMessages");
            e.Property(x => x.ToolName).HasMaxLength(64);
            e.Property(x => x.ToolCallId).HasMaxLength(128);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Model).HasMaxLength(128);
            e.Property(x => x.Citations).HasJsonConversion(jsonb);
            e.HasIndex(x => new { x.ConversationId, x.Sequence });
        });

        b.Entity<SearchHistoryEntry>(e =>
        {
            e.ToTable("SearchHistory");
            e.Property(x => x.Query).HasMaxLength(512).IsRequired();
            e.HasIndex(x => x.SearchedUtc);
            e.HasIndex(x => x.Query);
        });

        b.Entity<SavedSearch>(e =>
        {
            e.ToTable("SavedSearches");
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Query).HasMaxLength(512);
        });

        b.Entity<Bookmark>(e =>
        {
            e.ToTable("Bookmarks");
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.TargetKey).HasMaxLength(128);
            e.HasIndex(x => new { x.Kind, x.TargetKey });
        });

        b.Entity<WebSource>(e =>
        {
            e.ToTable("WebSources");
            e.Property(x => x.Url).HasMaxLength(2048).IsRequired();
            e.Property(x => x.UrlHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Domain).HasMaxLength(256);
            e.Property(x => x.SearchProvider).HasMaxLength(64);
            e.HasIndex(x => x.UrlHash).IsUnique();
            e.HasIndex(x => x.Domain);
        });

        b.Entity<CachedResponse>(e =>
        {
            e.ToTable("CachedResponses");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(256);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresUtc);
        });

        b.Entity<TrainingCourse>(e =>
        {
            e.ToTable("TrainingCourses");
            e.Property(x => x.Key).HasMaxLength(96).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.ContentVersion).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
            e.HasMany(x => x.Lessons).WithOne().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Quizzes).WithOne().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Flashcards).WithOne().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TrainingLesson>(e =>
        {
            e.ToTable("TrainingLessons");
            e.Property(x => x.Key).HasMaxLength(96);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.DiagramKey).HasMaxLength(96);
            e.HasIndex(x => new { x.CourseId, x.Key });
        });

        b.Entity<TrainingQuiz>(e =>
        {
            e.ToTable("TrainingQuizzes");
            e.Property(x => x.Key).HasMaxLength(96);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.Questions).HasJsonConversion(jsonb);
            e.HasIndex(x => x.CourseId);
        });

        b.Entity<Flashcard>(e =>
        {
            e.ToTable("Flashcards");
            e.HasIndex(x => new { x.CourseId, x.DueUtc });
        });

        b.Entity<TrainingAttempt>(e =>
        {
            e.ToTable("TrainingAttempts");
            e.Property(x => x.ReferenceKey).HasMaxLength(96);
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.TraineeName).HasMaxLength(128);
            e.HasIndex(x => new { x.Kind, x.ReferenceKey });
            e.HasIndex(x => x.StartedUtc);
        });

        b.Entity<TrainingScenario>(e =>
        {
            e.ToTable("TrainingScenarios");
            e.Property(x => x.Key).HasMaxLength(96).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.ContentVersion).HasMaxLength(64);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<LiveDataSession>(e =>
        {
            e.ToTable("LiveDataSessions");
            e.Property(x => x.Title).HasMaxLength(256);
            e.Property(x => x.AdapterDescription).HasMaxLength(256);
            e.Property(x => x.Protocol).HasMaxLength(128);
            e.Property(x => x.EcuVin).HasMaxLength(17);
            e.HasIndex(x => x.VehicleId);
            e.HasIndex(x => x.StartedUtc);
        });

        b.Entity<LiveDataSample>(e =>
        {
            e.ToTable("LiveDataSamples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Pid).HasMaxLength(24).IsRequired();
            e.HasIndex(x => new { x.SessionId, x.Pid, x.OffsetMs });
            e.HasOne<LiveDataSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        // Entity ids are assigned by the application (UUIDv7 in Entity) — never by the database.
        // With EF's default (ValueGeneratedOnAdd for Guid keys), a new child added to a tracked
        // aggregate's collection (e.g. session.AddStep during an update) already has a non-default
        // key, so change detection treats it as an existing row and issues an UPDATE that affects
        // 0 rows (DbUpdateConcurrencyException). Declaring the keys as never generated makes EF
        // insert such children.
        foreach (var entityType in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            b.Entity(entityType.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
        }

        ConfigureProvider(b);
    }

    /// <summary>Provider hook for vector columns and extensions.</summary>
    protected abstract void ConfigureVector(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<float[]> property);

    protected virtual void ConfigureProvider(ModelBuilder modelBuilder)
    {
    }

    /// <summary>Always stores UTC; values read back are marked UTC (SQLite loses the Kind).</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    internal static ValueComparer<float[]> FloatArrayComparer { get; } = new(
        (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
        v => v == null ? 0 : v.Length,
        v => v.ToArray());
}

/// <summary>Desktop database (offline-first). Vectors are stored as float32 blobs.</summary>
public sealed class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options) : AppDbContext(options)
{
    protected override bool IsPostgres => false;

    protected override void ConfigureVector(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<float[]> property)
    {
        property.HasConversion(
            v => VectorBlob.ToBytes(v),
            v => VectorBlob.FromBytes(v),
            FloatArrayComparer);
    }
}

/// <summary>Shop server database. Vectors use the pgvector extension.</summary>
public sealed class PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options) : AppDbContext(options)
{
    protected override bool IsPostgres => true;

    protected override void ConfigureVector(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<float[]> property)
    {
        property.HasConversion(
                v => new Pgvector.Vector(v),
                v => v.ToArray(),
                FloatArrayComparer)
            .HasColumnType("vector");
    }

    protected override void ConfigureProvider(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");
    }
}

internal static class VectorBlob
{
    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, vector.Length * sizeof(float));
        return vector;
    }
}
