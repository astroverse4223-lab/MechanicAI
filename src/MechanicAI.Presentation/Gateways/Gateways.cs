using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Application.Training;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Presentation.Gateways;

// Gateways are the narrow, mockable surface view models use to reach the application layer.
// The application services are sealed concrete classes; each gateway exposes only what the
// UI needs and is implemented by straight delegation (see ServiceGateways.cs).

public interface IVehicleGateway
{
    event EventHandler<Guid>? VehicleChanged;

    VinLookup AnalyzeLocally(string vin);

    Task<Result<VinLookup>> DecodeVinAsync(string vin, CancellationToken ct);

    Task<Result<Guid>> CreateFromVinAsync(string vin, int? mileage, CancellationToken ct);

    Task<Result<Guid>> SaveAsync(VehicleInput input, CancellationToken ct);

    Task<Vehicle?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<VehicleSummary>> ListAsync(string? search, CancellationToken ct);

    Task<IReadOnlyList<VehicleSummary>> RecentAsync(int take, CancellationToken ct);

    Task<Result> DeleteAsync(Guid id, CancellationToken ct);

    Task<Result> SetFavoriteAsync(Guid id, bool favorite, CancellationToken ct);

    Task<Result> RedecodeAsync(Guid id, CancellationToken ct);

    Task<Result<IReadOnlyList<Recall>>> RefreshRecallsAsync(Guid id, CancellationToken ct);

    Task<Result> SetRecallStatusAsync(Guid recallId, RecallStatus status, CancellationToken ct);

    Task MarkAccessedAsync(Guid id, CancellationToken ct);
}

public interface IDiagnosticsGateway
{
    event EventHandler<Guid>? SessionChanged;

    Task<Result<Guid>> StartAsync(StartDiagnosticSessionCommand command, CancellationToken ct);

    Task<Result<Guid>> StartFromTextAsync(string text, Guid? vehicleId, CancellationToken ct);

    Task<DiagnosticSessionView?> GetViewAsync(Guid sessionId, CancellationToken ct);

    Task<IReadOnlyList<DiagnosticSessionSummary>> ListAsync(int take, Guid? vehicleId, bool includeClosed, CancellationToken ct);

    Task<Result> RecordTestResultAsync(RecordTestResultCommand command, CancellationToken ct);

    Task<Result> RevertTestResultAsync(Guid sessionId, Guid testId, CancellationToken ct);

    Task<Result> GoBackAsync(Guid sessionId, CancellationToken ct);

    Task<Result> SkipTestAsync(Guid sessionId, Guid testId, string? reason, CancellationToken ct);

    Task<Result> SetCauseStatusAsync(Guid sessionId, Guid nodeId, CauseStatus status, string? reason, CancellationToken ct);

    Task<Result> AddObservationAsync(Guid sessionId, string observation, CancellationToken ct);

    Task<Result> SetInitialCheckAsync(Guid sessionId, string check, bool done, CancellationToken ct);

    Task<Result> AnswerQuestionAsync(Guid sessionId, int index, string answer, CancellationToken ct);

    Task<Result> AddDtcAsync(Guid sessionId, string code, CancellationToken ct);

    Task<Result> RemoveDtcAsync(Guid sessionId, string code, CancellationToken ct);

    Task<Result<Guid>> AddCauseAsync(AddCauseCommand command, CancellationToken ct);

    Task<Result> ConfirmDiagnosisAsync(Guid sessionId, Guid nodeId, string? notes, CancellationToken ct);

    Task<Result> RecordRepairAsync(RecordRepairCommand command, CancellationToken ct);

    Task<Result> RecordVerificationAsync(RecordVerificationCommand command, CancellationToken ct);

    Task<Result> ReopenAsync(Guid sessionId, CancellationToken ct);

    Task<Result> AbandonAsync(Guid sessionId, string? reason, CancellationToken ct);

    Task<Result> DeleteAsync(Guid sessionId, CancellationToken ct);

    Task<Result<DiagnosticAiResult>> AnalyzeWithAiAsync(Guid sessionId, Func<AgentEvent, Task>? onEvent, CancellationToken ct);

    /// <summary>Writes a report to the exports folder and returns its full path.</summary>
    Task<Result<string>> ExportReportAsync(Guid sessionId, ReportFormat format, CancellationToken ct);
}

public interface IDtcGateway
{
    Task<IReadOnlyList<DtcSearchResult>> SearchAsync(string query, int take, CancellationToken ct);

    Task<DtcDetail?> GetDetailAsync(string code, string? make, CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);

    Task<Result> SaveUserDefinitionAsync(string code, string? manufacturer, string description, string source, string? notes, CancellationToken ct);
}

public interface ILiveDataGateway
{
    event EventHandler<LiveFrame>? FrameReceived;

    event EventHandler<string>? StatusChanged;

    bool IsConnected { get; }

    bool IsStreaming { get; }

    bool IsRecording { get; }

    string? AdapterDescription { get; }

    string? Protocol { get; }

    bool IsSimulated { get; }

    IReadOnlySet<string> SupportedPids { get; }

    Task<IReadOnlyList<ObdAdapterInfo>> DiscoverAsync(CancellationToken ct);

    Task<Result> ConnectAsync(ObdAdapterInfo adapter, CancellationToken ct);

    Task DisconnectAsync();

    void StartStreaming(IReadOnlyList<string> pidKeys, TimeSpan interval);

    Task StopStreamingAsync();

    Task<Guid> StartRecordingAsync(Guid? vehicleId, Guid? sessionId, string? title, CancellationToken ct);

    Task<Result<Guid>> StopRecordingAsync(CancellationToken ct);

    Task<IReadOnlyList<LiveDataSession>> ListRecordingsAsync(CancellationToken ct);

    Task<Result> DeleteRecordingAsync(Guid recordingId, CancellationToken ct);

    Task<LiveDataStatistics?> ComputeStatisticsAsync(Guid recordingId, CancellationToken ct);

    Task<Result<string>> AnalyzeAsync(Guid recordingId, string? vehicleContext, Func<string, Task>? onText, CancellationToken ct);

    Task<Result<IReadOnlyList<ScannedDtc>>> ReadAllDtcsAsync(CancellationToken ct);

    Task<Result> ClearDtcsAsync(CancellationToken ct);

    Task<Result<FreezeFrameData?>> ReadFreezeFrameAsync(CancellationToken ct);

    Task<Result<string?>> ReadVinAsync(CancellationToken ct);

    Task<Result<MonitorStatusData?>> ReadMonitorsAsync(CancellationToken ct);
}

public interface IKnowledgeBaseGateway
{
    event EventHandler<Guid>? DocumentChanged;

    Task<Result<Guid>> UploadAsync(Stream content, string fileName, DocumentUploadOptions options, CancellationToken ct);

    Task<IReadOnlyList<Document>> ListAsync(DocumentKind? kind, string? search, CancellationToken ct);

    Task<Document?> GetAsync(Guid documentId, CancellationToken ct);

    Task<Result> DeleteAsync(Guid documentId, CancellationToken ct);

    Task<Result> ReindexAsync(Guid documentId, CancellationToken ct);

    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, KnowledgeSearchOptions options, CancellationToken ct);

    Task<Result<DocumentAnswer>> AskAsync(string question, DocumentQuestionTemplate template, KnowledgeSearchOptions? options, Func<string, Task>? onText,
        CancellationToken ct);

    Task<(int Documents, int Ready, int Chunks, int Embedded)> GetStatsAsync(CancellationToken ct);

    string GetFilePath(Document document);
}

public interface IWiringGateway
{
    Task<IReadOnlyList<Document>> ListDiagramsAsync(CancellationToken ct);

    Task<IReadOnlyList<WiringLabel>> ExtractLabelsAsync(Guid documentId, int? pageNumber, CancellationToken ct);

    Task<Result<WiringAnswer>> AskAsync(string question, Guid? documentId, int? pageNumber, Func<string, Task>? onText, CancellationToken ct);
}

public interface IResearchGateway
{
    Task<Result<ResearchResults>> SearchAsync(string query, string? vehicleContext, CancellationToken ct);

    Task<Result<ResearchSummary>> SummarizeAsync(string question, IReadOnlyList<ResearchSource> sources, Func<string, Task>? onText, CancellationToken ct);

    Task<Guid> BookmarkAsync(string title, string url, SourceType type, CancellationToken ct);
}

public interface IAssistantGateway
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct);

    Task<AiConversation?> GetAsync(Guid id, CancellationToken ct);

    Task<Guid> CreateAsync(Guid? vehicleId, Guid? sessionId, CancellationToken ct);

    Task<Result> RenameAsync(Guid id, string title, CancellationToken ct);

    Task<Result> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct);

    Task<Result> DeleteAsync(Guid id, CancellationToken ct);

    Task<Result<AssistantReply>> SendAsync(Guid conversationId, string text, IReadOnlyList<ChatImage> images, Func<AgentEvent, Task>? onEvent,
        CancellationToken ct);
}

public interface ITrainingGateway
{
    Task<IReadOnlyList<CourseSummary>> ListCoursesAsync(CancellationToken ct);

    Task<TrainingCourse?> GetCourseAsync(Guid courseId, CancellationToken ct);

    Task<LessonView?> GetLessonAsync(Guid lessonId, CancellationToken ct);

    Task MarkLessonCompleteAsync(Guid lessonId, bool completed, CancellationToken ct);

    Task<Result<QuizResult>> SubmitQuizAsync(Guid quizId, IReadOnlyList<int> answers, string? trainee, CancellationToken ct);

    Task<IReadOnlyList<Flashcard>> GetDueFlashcardsAsync(Guid? courseId, CancellationToken ct);

    Task ReviewFlashcardAsync(Guid flashcardId, bool correct, CancellationToken ct);

    IReadOnlyList<Exercise> GenerateExercises(IEnumerable<string> types, int count);

    Task RecordExerciseSetAsync(string courseKey, int correct, int total, string? trainee, CancellationToken ct);

    Task<Result<Guid>> GenerateAiQuizAsync(Guid lessonId, int questions, CancellationToken ct);
}

public interface IApprenticeGateway
{
    Task<IReadOnlyList<TrainingScenario>> ListScenariosAsync(CancellationToken ct);

    Task<Result<ScenarioBriefing>> StartAsync(string scenarioKey, string? trainee, CancellationToken ct);

    Task<IReadOnlyList<(string Key, string Title)>> GetTestMenuAsync(Guid attemptId, CancellationToken ct);

    Task<Result<ApprenticeTurn>> CheckAsync(Guid attemptId, string input, string? testKey, CancellationToken ct);

    Task<Result<ScenarioGrade>> SubmitDiagnosisAsync(Guid attemptId, string diagnosis, CancellationToken ct);
}

public interface IShopGateway
{
    Task<IReadOnlyList<Customer>> ListCustomersAsync(string? search, CancellationToken ct);

    Task<Result<Guid>> SaveCustomerAsync(CustomerInput input, CancellationToken ct);

    Task<Result> DeleteCustomerAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Estimate>> ListEstimatesAsync(CancellationToken ct);

    Task<Estimate?> GetEstimateAsync(Guid id, CancellationToken ct);

    Task<Result<Guid>> SaveEstimateAsync(Guid? id, Guid? customerId, Guid? vehicleId, decimal taxRate, string? notes,
        IReadOnlyList<EstimateLineInput> lines, EstimateStatus status, CancellationToken ct);

    Task<Result> DeleteEstimateAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Inspection>> ListInspectionsAsync(CancellationToken ct);

    Task<Result<Guid>> StartInspectionAsync(Guid vehicleId, int? mileage, string? technician, CancellationToken ct);

    Task<Result> UpdateInspectionItemAsync(Guid itemId, InspectionRating rating, string? measurement, string? notes, CancellationToken ct);

    Task<Result> CompleteInspectionAsync(Guid inspectionId, CancellationToken ct);
}

public interface IHistoryGateway
{
    Task<VehicleHistory?> GetVehicleHistoryAsync(Guid vehicleId, string? search, CancellationToken ct);

    Task<IReadOnlyList<(Guid SessionId, string Vehicle, string Diagnosis, string Complaint, IReadOnlyList<string> Codes, DateTime WhenUtc)>>
        SearchConfirmedDiagnosesAsync(string query, CancellationToken ct);
}

public interface ISearchGateway
{
    Task<UniversalSearchResults> SearchAsync(string query, bool includeWeb, CancellationToken ct);
}

/// <summary>Workstation-level operations: AI status, local models, sample data, sign-in.</summary>
public interface ISystemGateway
{
    Task<AiStatus> GetAiStatusAsync(bool refresh, CancellationToken ct);

    void InvalidateAiRouting();

    Task<bool> PingOllamaAsync(CancellationToken ct);

    Task<IReadOnlyList<LocalModelInfo>> ListLocalModelsAsync(CancellationToken ct);

    Task PullModelAsync(string model, IProgress<ModelPullProgress>? progress, CancellationToken ct);

    Task<Result<int>> InstallSampleDataAsync(CancellationToken ct);

    Task<Result> RemoveSampleDataAsync(CancellationToken ct);

    bool IsSignInRequired { get; }

    Task<bool> HasPasswordAsync(CancellationToken ct);

    Task<Result> SetPasswordAsync(string? currentPassword, string newPassword, CancellationToken ct);

    Task<Result> RemovePasswordAsync(string currentPassword, CancellationToken ct);

    Task<Result> SignInAsync(string password, CancellationToken ct);
}
