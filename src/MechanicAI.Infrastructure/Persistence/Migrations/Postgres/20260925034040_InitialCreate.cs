using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace MechanicAI.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "AIConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIConversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Bookmarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TargetKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookmarks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedResponses",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    RetrievedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedResponses", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AddressLine1 = table.Column<string>(type: "text", nullable: true),
                    AddressLine2 = table.Column<string>(type: "text", nullable: true),
                    City = table.Column<string>(type: "text", nullable: true),
                    Region = table.Column<string>(type: "text", nullable: true),
                    PostalCode = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsSample = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Make = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    YearFrom = table.Column<int>(type: "integer", nullable: true),
                    YearTo = table.Column<int>(type: "integer", nullable: true),
                    PageCount = table.Column<int>(type: "integer", nullable: false),
                    ChunkCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    StatusMessage = table.Column<string>(type: "text", nullable: true),
                    IndexedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UsedOcr = table.Column<bool>(type: "boolean", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DTCs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    System = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Subsystem = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsGeneric = table.Column<bool>(type: "boolean", nullable: false),
                    Symptoms = table.Column<List<string>>(type: "text[]", nullable: false),
                    Causes = table.Column<List<string>>(type: "text[]", nullable: false),
                    RelatedCodes = table.Column<List<string>>(type: "text[]", nullable: false),
                    SafetyTags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsUserDefined = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DTCs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LiveDataSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AdapterDescription = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Protocol = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EcuVin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Pids = table.Column<List<string>>(type: "text[]", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    AnalysisMarkdown = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveDataSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepairId = table.Column<Guid>(type: "uuid", nullable: true),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Caption = table.Column<string>(type: "text", nullable: true),
                    TakenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnalysisJson = table.Column<string>(type: "text", nullable: true),
                    AnalyzedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaAttachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    AuthorName = table.Column<string>(type: "text", nullable: true),
                    IsSample = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SavedSearches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Query = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Intent = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedSearches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SearchHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Query = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Intent = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    SearchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Technicians",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    Certifications = table.Column<string>(type: "text", nullable: true),
                    Specialties = table.Column<string>(type: "text", nullable: true),
                    IsApprentice = table.Column<bool>(type: "boolean", nullable: false),
                    HighVoltageCertified = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSample = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Technicians", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrainingAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ReferenceKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TraineeName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: false),
                    MaxScore = table.Column<double>(type: "double precision", nullable: false),
                    Passed = table.Column<bool>(type: "boolean", nullable: true),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrainingCourses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Category = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Level = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Exercises = table.Column<List<string>>(type: "text[]", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingCourses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrainingScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Difficulty = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    VehicleDescription = table.Column<string>(type: "text", nullable: false),
                    Complaint = table.Column<string>(type: "text", nullable: false),
                    Codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingScenarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    UrlHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetrievedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Snippet = table.Column<string>(type: "text", nullable: true),
                    ContentExcerpt = table.Column<string>(type: "text", nullable: true),
                    ContentFetchedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastQuery = table.Column<string>(type: "text", nullable: true),
                    SearchProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AIConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "text", nullable: true),
                    ToolName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolCallId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Citations = table.Column<string>(type: "jsonb", nullable: false),
                    ValidationWarnings = table.Column<List<string>>(type: "text[]", nullable: false),
                    AttachmentIds = table.Column<List<string>>(type: "text[]", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AIConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AIConversationMessages_AIConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "AIConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    Make = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Trim = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    Engine = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    DisplacementLiters = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Cylinders = table.Column<int>(type: "integer", nullable: true),
                    FuelType = table.Column<string>(type: "text", nullable: true),
                    Drivetrain = table.Column<string>(type: "text", nullable: true),
                    Transmission = table.Column<string>(type: "text", nullable: true),
                    BodyClass = table.Column<string>(type: "text", nullable: true),
                    Mileage = table.Column<int>(type: "integer", nullable: true),
                    MileageUnit = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Color = table.Column<string>(type: "text", nullable: true),
                    LicensePlate = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    DataSource = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    DecodedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAccessedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecallsCheckedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    IsSample = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vehicles_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DocumentChunks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    PageEnd = table.Column<int>(type: "integer", nullable: true),
                    Heading = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Text = table.Column<string>(type: "text", nullable: false),
                    TokenEstimate = table.Column<int>(type: "integer", nullable: false),
                    CharStart = table.Column<int>(type: "integer", nullable: false),
                    CharEnd = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentChunks_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Width = table.Column<double>(type: "double precision", nullable: false),
                    Height = table.Column<double>(type: "double precision", nullable: false),
                    FromOcr = table.Column<bool>(type: "boolean", nullable: false),
                    Words = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentPages_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LiveDataSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OffsetMs = table.Column<int>(type: "integer", nullable: false),
                    Pid = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveDataSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveDataSamples_LiveDataSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "LiveDataSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastLoginUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SecurityStamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TechnicianId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Technicians_TechnicianId",
                        column: x => x.TechnicianId,
                        principalTable: "Technicians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Flashcards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Front = table.Column<string>(type: "text", nullable: false),
                    Back = table.Column<string>(type: "text", nullable: false),
                    Box = table.Column<int>(type: "integer", nullable: false),
                    DueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReviewedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CorrectCount = table.Column<int>(type: "integer", nullable: false),
                    IncorrectCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Flashcards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Flashcards_TrainingCourses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "TrainingCourses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrainingLessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BodyMarkdown = table.Column<string>(type: "text", nullable: false),
                    DiagramKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    EstimatedMinutes = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    SafetyTags = table.Column<List<string>>(type: "text[]", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainingLessons_TrainingCourses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "TrainingCourses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrainingQuizzes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Questions = table.Column<string>(type: "jsonb", nullable: false),
                    IsAiGenerated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrainingQuizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrainingQuizzes_TrainingCourses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "TrainingCourses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    VehicleDescription = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Complaint = table.Column<string>(type: "text", nullable: false),
                    Symptoms = table.Column<List<string>>(type: "text[]", nullable: false),
                    Conditions = table.Column<List<string>>(type: "text[]", nullable: false),
                    Mileage = table.Column<int>(type: "integer", nullable: true),
                    TechnicianName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TechnicianId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    FinalDiagnosis = table.Column<string>(type: "text", nullable: true),
                    ConfirmedCauseNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepairPerformed = table.Column<string>(type: "text", nullable: true),
                    VerificationNotes = table.Column<string>(type: "text", nullable: true),
                    VerificationPassed = table.Column<bool>(type: "boolean", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AiSummary = table.Column<string>(type: "text", nullable: true),
                    PlaybookKeys = table.Column<List<string>>(type: "text[]", nullable: false),
                    ClarifyingQuestions = table.Column<string>(type: "jsonb", nullable: false),
                    VerificationPlan = table.Column<List<string>>(type: "text[]", nullable: false),
                    CompletedInitialChecks = table.Column<List<string>>(type: "text[]", nullable: false),
                    IsSample = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticSessions_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Estimates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    TaxRate = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ValidUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Estimates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Estimates_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Estimates_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Inspections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TechnicianName = table.Column<string>(type: "text", nullable: true),
                    Mileage = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Inspections_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Recalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Manufacturer = table.Column<string>(type: "text", nullable: true),
                    Component = table.Column<string>(type: "text", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Consequence = table.Column<string>(type: "text", nullable: true),
                    Remedy = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    ReportReceivedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ParkIt = table.Column<bool>(type: "boolean", nullable: false),
                    ParkOutside = table.Column<bool>(type: "boolean", nullable: false),
                    OverTheAirUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    RetrievedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recalls_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Repairs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: true),
                    DiagnosticSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    PerformedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Mileage = table.Column<int>(type: "integer", nullable: true),
                    TechnicianName = table.Column<string>(type: "text", nullable: true),
                    LaborHours = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    VerificationNotes = table.Column<string>(type: "text", nullable: true),
                    IsSample = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repairs_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "VehicleSpecifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RetrievedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Verification = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleSpecifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleSpecifications_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChunkId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Dimensions = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<Vector>(type: "vector", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Embeddings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Embeddings_DocumentChunks_ChunkId",
                        column: x => x.ChunkId,
                        principalTable: "DocumentChunks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "text", nullable: true),
                    CreatedByIp = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    PriorProbability = table.Column<double>(type: "double precision", nullable: false),
                    Probability = table.Column<double>(type: "double precision", nullable: false),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    StatusReason = table.Column<string>(type: "text", nullable: true),
                    IsManualStatus = table.Column<bool>(type: "boolean", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Origin = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    OriginDetail = table.Column<string>(type: "text", nullable: true),
                    SafetyTags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Sources = table.Column<string>(type: "jsonb", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticNodes_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Actor = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    ActorName = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    Evidence = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    TestId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsReverted = table.Column<bool>(type: "boolean", nullable: false),
                    RevertedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticSteps_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticTests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: true),
                    Procedure = table.Column<List<string>>(type: "text[]", nullable: false),
                    Tools = table.Column<List<string>>(type: "text[]", nullable: false),
                    ExpectedResult = table.Column<string>(type: "text", nullable: true),
                    SpecificationNote = table.Column<string>(type: "text", nullable: true),
                    EstimatedMinutes = table.Column<int>(type: "integer", nullable: false),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    Invasiveness = table.Column<int>(type: "integer", nullable: false),
                    SafetyTags = table.Column<List<string>>(type: "text[]", nullable: false),
                    RelatedCauseKeys = table.Column<List<string>>(type: "text[]", nullable: false),
                    Outcomes = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    SelectedOutcomeKey = table.Column<string>(type: "text", nullable: true),
                    Result = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    ActualResult = table.Column<string>(type: "text", nullable: true),
                    PerformedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PerformedBy = table.Column<string>(type: "text", nullable: true),
                    ExecutionOrder = table.Column<int>(type: "integer", nullable: true),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    InformationGain = table.Column<double>(type: "double precision", nullable: true),
                    PrimaryNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Origin = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    OriginDetail = table.Column<string>(type: "text", nullable: true),
                    Evidence = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Sources = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticTests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticTests_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionDtcs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FreezeFrame = table.Column<string>(type: "jsonb", nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionDtcs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionDtcs_DiagnosticSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EstimateLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EstimateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PartNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Taxable = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstimateLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EstimateLines_Estimates_EstimateId",
                        column: x => x.EstimateId,
                        principalTable: "Estimates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Section = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Rating = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Measurement = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionItems_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Parts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepairId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Brand = table.Column<string>(type: "text", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Supplier = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Parts_Repairs_RepairId",
                        column: x => x.RepairId,
                        principalTable: "Repairs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AIConversationMessages_ConversationId_Sequence",
                table: "AIConversationMessages",
                columns: new[] { "ConversationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_AIConversations_DiagnosticSessionId",
                table: "AIConversations",
                column: "DiagnosticSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AIConversations_UpdatedUtc",
                table: "AIConversations",
                column: "UpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TimestampUtc",
                table: "AuditLog",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_UserId",
                table: "AuditLog",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookmarks_Kind_TargetKey",
                table: "Bookmarks",
                columns: new[] { "Kind", "TargetKey" });

            migrationBuilder.CreateIndex(
                name: "IX_CachedResponses_ExpiresUtc",
                table: "CachedResponses",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_LastName_FirstName",
                table: "Customers",
                columns: new[] { "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Phone",
                table: "Customers",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticNodes_SessionId",
                table: "DiagnosticNodes",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSessions_Status",
                table: "DiagnosticSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSessions_UpdatedUtc",
                table: "DiagnosticSessions",
                column: "UpdatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSessions_VehicleId",
                table: "DiagnosticSessions",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticSteps_SessionId_Sequence",
                table: "DiagnosticSteps",
                columns: new[] { "SessionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticTests_SessionId",
                table: "DiagnosticTests",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_DocumentId_Ordinal",
                table: "DocumentChunks",
                columns: new[] { "DocumentId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPages_DocumentId_PageNumber",
                table: "DocumentPages",
                columns: new[] { "DocumentId", "PageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Kind",
                table: "Documents",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Sha256",
                table: "Documents",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_VehicleId",
                table: "Documents",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_DTCs_Code",
                table: "DTCs",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_DTCs_Code_Manufacturer",
                table: "DTCs",
                columns: new[] { "Code", "Manufacturer" });

            migrationBuilder.CreateIndex(
                name: "IX_Embeddings_ChunkId_Model",
                table: "Embeddings",
                columns: new[] { "ChunkId", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Embeddings_Model_DocumentId",
                table: "Embeddings",
                columns: new[] { "Model", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_EstimateLines_EstimateId",
                table: "EstimateLines",
                column: "EstimateId");

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_CustomerId",
                table: "Estimates",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_Number",
                table: "Estimates",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_VehicleId",
                table: "Estimates",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Flashcards_CourseId_DueUtc",
                table: "Flashcards",
                columns: new[] { "CourseId", "DueUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionItems_InspectionId",
                table: "InspectionItems",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_VehicleId",
                table: "Inspections",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveDataSamples_SessionId_Pid_OffsetMs",
                table: "LiveDataSamples",
                columns: new[] { "SessionId", "Pid", "OffsetMs" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveDataSessions_StartedUtc",
                table: "LiveDataSessions",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_LiveDataSessions_VehicleId",
                table: "LiveDataSessions",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAttachments_DiagnosticSessionId",
                table: "MediaAttachments",
                column: "DiagnosticSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAttachments_Sha256",
                table: "MediaAttachments",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_MediaAttachments_VehicleId",
                table: "MediaAttachments",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_CustomerId",
                table: "Notes",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_DiagnosticSessionId",
                table: "Notes",
                column: "DiagnosticSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_VehicleId",
                table: "Notes",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Parts_PartNumber",
                table: "Parts",
                column: "PartNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Parts_RepairId",
                table: "Parts",
                column: "RepairId");

            migrationBuilder.CreateIndex(
                name: "IX_Recalls_VehicleId_CampaignNumber",
                table: "Recalls",
                columns: new[] { "VehicleId", "CampaignNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_DiagnosticSessionId",
                table: "Repairs",
                column: "DiagnosticSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_PerformedUtc",
                table: "Repairs",
                column: "PerformedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Repairs_VehicleId",
                table: "Repairs",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchHistory_Query",
                table: "SearchHistory",
                column: "Query");

            migrationBuilder.CreateIndex(
                name: "IX_SearchHistory_SearchedUtc",
                table: "SearchHistory",
                column: "SearchedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SessionDtcs_Code",
                table: "SessionDtcs",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_SessionDtcs_SessionId",
                table: "SessionDtcs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Technicians_Name",
                table: "Technicians",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingAttempts_Kind_ReferenceKey",
                table: "TrainingAttempts",
                columns: new[] { "Kind", "ReferenceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingAttempts_StartedUtc",
                table: "TrainingAttempts",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingCourses_Key",
                table: "TrainingCourses",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrainingLessons_CourseId_Key",
                table: "TrainingLessons",
                columns: new[] { "CourseId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_TrainingQuizzes_CourseId",
                table: "TrainingQuizzes",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_TrainingScenarios_Key",
                table: "TrainingScenarios",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TechnicianId",
                table: "Users",
                column: "TechnicianId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CustomerId",
                table: "Vehicles",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_LastAccessedUtc",
                table: "Vehicles",
                column: "LastAccessedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Vin",
                table: "Vehicles",
                column: "Vin");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Year_Make_Model",
                table: "Vehicles",
                columns: new[] { "Year", "Make", "Model" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleSpecifications_VehicleId",
                table: "VehicleSpecifications",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_WebSources_Domain",
                table: "WebSources",
                column: "Domain");

            migrationBuilder.CreateIndex(
                name: "IX_WebSources_UrlHash",
                table: "WebSources",
                column: "UrlHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AIConversationMessages");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "Bookmarks");

            migrationBuilder.DropTable(
                name: "CachedResponses");

            migrationBuilder.DropTable(
                name: "DiagnosticNodes");

            migrationBuilder.DropTable(
                name: "DiagnosticSteps");

            migrationBuilder.DropTable(
                name: "DiagnosticTests");

            migrationBuilder.DropTable(
                name: "DocumentPages");

            migrationBuilder.DropTable(
                name: "DTCs");

            migrationBuilder.DropTable(
                name: "Embeddings");

            migrationBuilder.DropTable(
                name: "EstimateLines");

            migrationBuilder.DropTable(
                name: "Flashcards");

            migrationBuilder.DropTable(
                name: "InspectionItems");

            migrationBuilder.DropTable(
                name: "LiveDataSamples");

            migrationBuilder.DropTable(
                name: "MediaAttachments");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropTable(
                name: "Parts");

            migrationBuilder.DropTable(
                name: "Recalls");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "SavedSearches");

            migrationBuilder.DropTable(
                name: "SearchHistory");

            migrationBuilder.DropTable(
                name: "SessionDtcs");

            migrationBuilder.DropTable(
                name: "TrainingAttempts");

            migrationBuilder.DropTable(
                name: "TrainingLessons");

            migrationBuilder.DropTable(
                name: "TrainingQuizzes");

            migrationBuilder.DropTable(
                name: "TrainingScenarios");

            migrationBuilder.DropTable(
                name: "VehicleSpecifications");

            migrationBuilder.DropTable(
                name: "WebSources");

            migrationBuilder.DropTable(
                name: "AIConversations");

            migrationBuilder.DropTable(
                name: "DocumentChunks");

            migrationBuilder.DropTable(
                name: "Estimates");

            migrationBuilder.DropTable(
                name: "Inspections");

            migrationBuilder.DropTable(
                name: "LiveDataSessions");

            migrationBuilder.DropTable(
                name: "Repairs");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "DiagnosticSessions");

            migrationBuilder.DropTable(
                name: "TrainingCourses");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "Technicians");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
