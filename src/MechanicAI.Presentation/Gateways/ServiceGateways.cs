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

internal sealed class VehicleGateway(VehicleService vehicles) : IVehicleGateway
{
    public event EventHandler<Guid>? VehicleChanged
    {
        add => vehicles.VehicleChanged += value;
        remove => vehicles.VehicleChanged -= value;
    }

    public VinLookup AnalyzeLocally(string vin) => VehicleService.AnalyzeLocally(vin);

    public Task<Result<VinLookup>> DecodeVinAsync(string vin, CancellationToken ct) => vehicles.DecodeVinAsync(vin, null, ct);

    public Task<Result<Guid>> CreateFromVinAsync(string vin, int? mileage, CancellationToken ct) => vehicles.CreateFromVinAsync(vin, mileage, null, ct);

    public Task<Result<Guid>> SaveAsync(VehicleInput input, CancellationToken ct) => vehicles.SaveAsync(input, ct);

    public Task<Vehicle?> GetAsync(Guid id, CancellationToken ct) => vehicles.GetAsync(id, ct);

    public Task<IReadOnlyList<VehicleSummary>> ListAsync(string? search, CancellationToken ct) => vehicles.ListAsync(search, 200, ct);

    public Task<IReadOnlyList<VehicleSummary>> RecentAsync(int take, CancellationToken ct) => vehicles.RecentAsync(take, ct);

    public Task<Result> DeleteAsync(Guid id, CancellationToken ct) => vehicles.DeleteAsync(id, ct);

    public Task<Result> SetFavoriteAsync(Guid id, bool favorite, CancellationToken ct) => vehicles.SetFavoriteAsync(id, favorite, ct);

    public Task<Result> RedecodeAsync(Guid id, CancellationToken ct) => vehicles.RedecodeAsync(id, ct);

    public Task<Result<IReadOnlyList<Recall>>> RefreshRecallsAsync(Guid id, CancellationToken ct) => vehicles.RefreshRecallsAsync(id, ct);

    public Task<Result> SetRecallStatusAsync(Guid recallId, RecallStatus status, CancellationToken ct) => vehicles.SetRecallStatusAsync(recallId, status, ct);

    public Task MarkAccessedAsync(Guid id, CancellationToken ct) => vehicles.MarkAccessedAsync(id, ct);
}

internal sealed class DiagnosticsGateway(DiagnosticSessionService sessions, DiagnosticAiAdvisor advisor, ReportService reports) : IDiagnosticsGateway
{
    public event EventHandler<Guid>? SessionChanged
    {
        add => sessions.SessionChanged += value;
        remove => sessions.SessionChanged -= value;
    }

    public Task<Result<Guid>> StartAsync(StartDiagnosticSessionCommand command, CancellationToken ct) => sessions.StartAsync(command, ct);

    public Task<Result<Guid>> StartFromTextAsync(string text, Guid? vehicleId, CancellationToken ct) => sessions.StartFromTextAsync(text, vehicleId, ct);

    public Task<DiagnosticSessionView?> GetViewAsync(Guid sessionId, CancellationToken ct) => sessions.GetViewAsync(sessionId, ct);

    public Task<IReadOnlyList<DiagnosticSessionSummary>> ListAsync(int take, Guid? vehicleId, bool includeClosed, CancellationToken ct) =>
        sessions.ListAsync(take, vehicleId, includeClosed, ct);

    public Task<Result> RecordTestResultAsync(RecordTestResultCommand command, CancellationToken ct) => sessions.RecordTestResultAsync(command, ct);

    public Task<Result> RevertTestResultAsync(Guid sessionId, Guid testId, CancellationToken ct) => sessions.RevertTestResultAsync(sessionId, testId, ct);

    public Task<Result> GoBackAsync(Guid sessionId, CancellationToken ct) => sessions.GoBackAsync(sessionId, ct);

    public Task<Result> SkipTestAsync(Guid sessionId, Guid testId, string? reason, CancellationToken ct) => sessions.SkipTestAsync(sessionId, testId, reason, ct);

    public Task<Result> SetCauseStatusAsync(Guid sessionId, Guid nodeId, CauseStatus status, string? reason, CancellationToken ct) =>
        sessions.SetCauseStatusAsync(sessionId, nodeId, status, reason, ct);

    public Task<Result> AddObservationAsync(Guid sessionId, string observation, CancellationToken ct) => sessions.AddObservationAsync(sessionId, observation, ct);

    public Task<Result> SetInitialCheckAsync(Guid sessionId, string check, bool done, CancellationToken ct) =>
        sessions.SetInitialCheckAsync(sessionId, check, done, ct);

    public Task<Result> AnswerQuestionAsync(Guid sessionId, int index, string answer, CancellationToken ct) =>
        sessions.AnswerQuestionAsync(sessionId, index, answer, ct);

    public Task<Result> AddDtcAsync(Guid sessionId, string code, CancellationToken ct) => sessions.AddDtcAsync(sessionId, code, ct: ct);

    public Task<Result> RemoveDtcAsync(Guid sessionId, string code, CancellationToken ct) => sessions.RemoveDtcAsync(sessionId, code, ct);

    public Task<Result<Guid>> AddCauseAsync(AddCauseCommand command, CancellationToken ct) => sessions.AddCauseAsync(command, ct);

    public Task<Result> ConfirmDiagnosisAsync(Guid sessionId, Guid nodeId, string? notes, CancellationToken ct) =>
        sessions.ConfirmDiagnosisAsync(sessionId, nodeId, notes, ct);

    public Task<Result> RecordRepairAsync(RecordRepairCommand command, CancellationToken ct) => sessions.RecordRepairAsync(command, ct);

    public Task<Result> RecordVerificationAsync(RecordVerificationCommand command, CancellationToken ct) => sessions.RecordVerificationAsync(command, ct);

    public Task<Result> ReopenAsync(Guid sessionId, CancellationToken ct) => sessions.ReopenAsync(sessionId, ct);

    public Task<Result> AbandonAsync(Guid sessionId, string? reason, CancellationToken ct) => sessions.AbandonAsync(sessionId, reason, ct);

    public Task<Result> DeleteAsync(Guid sessionId, CancellationToken ct) => sessions.DeleteAsync(sessionId, ct);

    public Task<Result<DiagnosticAiResult>> AnalyzeWithAiAsync(Guid sessionId, Func<AgentEvent, Task>? onEvent, CancellationToken ct) =>
        advisor.AnalyzeAsync(sessionId, onEvent, ct);

    public Task<Result<string>> ExportReportAsync(Guid sessionId, ReportFormat format, CancellationToken ct) => reports.ExportSessionAsync(sessionId, format, ct);
}

internal sealed class DtcGateway(DtcService dtcs) : IDtcGateway
{
    public Task<IReadOnlyList<DtcSearchResult>> SearchAsync(string query, int take, CancellationToken ct) => dtcs.SearchAsync(query, take, ct);

    public Task<DtcDetail?> GetDetailAsync(string code, string? make, CancellationToken ct) => dtcs.GetDetailAsync(code, make, ct);

    public Task<int> CountAsync(CancellationToken ct) => dtcs.CountAsync(ct);

    public Task<Result> SaveUserDefinitionAsync(string code, string? manufacturer, string description, string source, string? notes, CancellationToken ct) =>
        dtcs.SaveUserDefinitionAsync(code, manufacturer, description, source, notes, ct);
}

internal sealed class LiveDataGateway(LiveDataService live) : ILiveDataGateway
{
    public event EventHandler<LiveFrame>? FrameReceived
    {
        add => live.FrameReceived += value;
        remove => live.FrameReceived -= value;
    }

    public event EventHandler<string>? StatusChanged
    {
        add => live.StatusChanged += value;
        remove => live.StatusChanged -= value;
    }

    public bool IsConnected => live.IsConnected;

    public bool IsStreaming => live.IsStreaming;

    public bool IsRecording => live.IsRecording;

    public string? AdapterDescription => live.Connection?.Adapter.DisplayName;

    public string? Protocol => live.Connection?.Protocol;

    public bool IsSimulated => live.Connection?.Adapter.IsSimulator == true;

    public IReadOnlySet<string> SupportedPids => live.SupportedPids;

    public Task<IReadOnlyList<ObdAdapterInfo>> DiscoverAsync(CancellationToken ct) => live.DiscoverAsync(ct);

    public Task<Result> ConnectAsync(ObdAdapterInfo adapter, CancellationToken ct) => live.ConnectAsync(adapter, ct);

    public Task DisconnectAsync() => live.DisconnectAsync();

    public void StartStreaming(IReadOnlyList<string> pidKeys, TimeSpan interval) => live.StartStreaming(pidKeys, interval);

    public Task StopStreamingAsync() => live.StopStreamingAsync();

    public Task<Guid> StartRecordingAsync(Guid? vehicleId, Guid? sessionId, string? title, CancellationToken ct) =>
        live.StartRecordingAsync(vehicleId, sessionId, title, ct);

    public Task<Result<Guid>> StopRecordingAsync(CancellationToken ct) => live.StopRecordingAsync(ct);

    public Task<IReadOnlyList<LiveDataSession>> ListRecordingsAsync(CancellationToken ct) => live.ListRecordingsAsync(null, ct);

    public Task<Result> DeleteRecordingAsync(Guid recordingId, CancellationToken ct) => live.DeleteRecordingAsync(recordingId, ct);

    public Task<LiveDataStatistics?> ComputeStatisticsAsync(Guid recordingId, CancellationToken ct) => live.ComputeStatisticsAsync(recordingId, ct);

    public Task<Result<string>> AnalyzeAsync(Guid recordingId, string? vehicleContext, Func<string, Task>? onText, CancellationToken ct) =>
        live.AnalyzeAsync(recordingId, vehicleContext, onText, ct);

    public Task<Result<IReadOnlyList<ScannedDtc>>> ReadAllDtcsAsync(CancellationToken ct) => live.ReadAllDtcsAsync(ct);

    public Task<Result> ClearDtcsAsync(CancellationToken ct) => live.ClearDtcsAsync(ct);

    public Task<Result<FreezeFrameData?>> ReadFreezeFrameAsync(CancellationToken ct) => live.ReadFreezeFrameAsync(ct);

    public Task<Result<string?>> ReadVinAsync(CancellationToken ct) => live.ReadVinAsync(ct);

    public Task<Result<MonitorStatusData?>> ReadMonitorsAsync(CancellationToken ct) => live.ReadMonitorsAsync(ct);
}

internal sealed class KnowledgeBaseGateway(KnowledgeBaseService kb) : IKnowledgeBaseGateway
{
    public event EventHandler<Guid>? DocumentChanged
    {
        add => kb.DocumentChanged += value;
        remove => kb.DocumentChanged -= value;
    }

    public Task<Result<Guid>> UploadAsync(Stream content, string fileName, DocumentUploadOptions options, CancellationToken ct) =>
        kb.UploadAsync(content, fileName, options, ct);

    public Task<IReadOnlyList<Document>> ListAsync(DocumentKind? kind, string? search, CancellationToken ct) => kb.ListAsync(kind, null, search, ct);

    public Task<Document?> GetAsync(Guid documentId, CancellationToken ct) => kb.GetAsync(documentId, ct);

    public Task<Result> DeleteAsync(Guid documentId, CancellationToken ct) => kb.DeleteAsync(documentId, ct);

    public Task<Result> ReindexAsync(Guid documentId, CancellationToken ct) => kb.ReindexAsync(documentId, ct);

    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, KnowledgeSearchOptions options, CancellationToken ct) => kb.SearchAsync(query, options, ct);

    public Task<Result<DocumentAnswer>> AskAsync(string question, DocumentQuestionTemplate template, KnowledgeSearchOptions? options, Func<string, Task>? onText,
        CancellationToken ct) => kb.AskAsync(question, template, options, onText, ct);

    public Task<(int Documents, int Ready, int Chunks, int Embedded)> GetStatsAsync(CancellationToken ct) => kb.GetStatsAsync(ct);

    public string GetFilePath(Document document) => kb.GetFilePath(document);
}

internal sealed class WiringGateway(WiringService wiring) : IWiringGateway
{
    public Task<IReadOnlyList<Document>> ListDiagramsAsync(CancellationToken ct) => wiring.ListDiagramsAsync(null, ct);

    public Task<IReadOnlyList<WiringLabel>> ExtractLabelsAsync(Guid documentId, int? pageNumber, CancellationToken ct) =>
        wiring.ExtractLabelsAsync(documentId, pageNumber, ct);

    public Task<Result<WiringAnswer>> AskAsync(string question, Guid? documentId, int? pageNumber, Func<string, Task>? onText, CancellationToken ct) =>
        wiring.AskAsync(question, documentId, pageNumber, onText, ct);
}

internal sealed class ResearchGateway(ResearchService research, SearchHistoryService history) : IResearchGateway
{
    public Task<Result<ResearchResults>> SearchAsync(string query, string? vehicleContext, CancellationToken ct) =>
        research.SearchAsync(query, vehicleContext, null, ct);

    public Task<Result<ResearchSummary>> SummarizeAsync(string question, IReadOnlyList<ResearchSource> sources, Func<string, Task>? onText, CancellationToken ct) =>
        research.SummarizeAsync(question, sources, onText, ct);

    public Task<Guid> BookmarkAsync(string title, string url, SourceType type, CancellationToken ct) =>
        history.AddBookmarkAsync(BookmarkKind.WebSource, title, url, null, null, type, ct);
}

internal sealed class AssistantGateway(AssistantService assistant) : IAssistantGateway
{
    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct) => assistant.ListAsync(100, ct);

    public Task<AiConversation?> GetAsync(Guid id, CancellationToken ct) => assistant.GetAsync(id, ct);

    public Task<Guid> CreateAsync(Guid? vehicleId, Guid? sessionId, CancellationToken ct) =>
        assistant.CreateAsync(ConversationKind.Assistant, vehicleId, sessionId, null, ct);

    public Task<Result> RenameAsync(Guid id, string title, CancellationToken ct) => assistant.RenameAsync(id, title, ct);

    public Task<Result> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct) => assistant.SetPinnedAsync(id, pinned, ct);

    public Task<Result> DeleteAsync(Guid id, CancellationToken ct) => assistant.DeleteAsync(id, ct);

    public Task<Result<AssistantReply>> SendAsync(Guid conversationId, string text, IReadOnlyList<ChatImage> images, Func<AgentEvent, Task>? onEvent,
        CancellationToken ct) => assistant.SendAsync(conversationId, text, images, onEvent, ct);
}

internal sealed class TrainingGateway(TrainingService training) : ITrainingGateway
{
    public Task<IReadOnlyList<CourseSummary>> ListCoursesAsync(CancellationToken ct) => training.ListCoursesAsync(ct);

    public Task<TrainingCourse?> GetCourseAsync(Guid courseId, CancellationToken ct) => training.GetCourseAsync(courseId, ct);

    public Task<LessonView?> GetLessonAsync(Guid lessonId, CancellationToken ct) => training.GetLessonAsync(lessonId, ct);

    public Task MarkLessonCompleteAsync(Guid lessonId, bool completed, CancellationToken ct) => training.MarkLessonCompleteAsync(lessonId, completed, ct);

    public Task<Result<QuizResult>> SubmitQuizAsync(Guid quizId, IReadOnlyList<int> answers, string? trainee, CancellationToken ct) =>
        training.SubmitQuizAsync(quizId, answers, trainee, ct);

    public Task<IReadOnlyList<Flashcard>> GetDueFlashcardsAsync(Guid? courseId, CancellationToken ct) => training.GetDueFlashcardsAsync(courseId, 20, ct);

    public Task ReviewFlashcardAsync(Guid flashcardId, bool correct, CancellationToken ct) => training.ReviewFlashcardAsync(flashcardId, correct, ct);

    public IReadOnlyList<Exercise> GenerateExercises(IEnumerable<string> types, int count) => TrainingService.GenerateExercises(types, count);

    public Task RecordExerciseSetAsync(string courseKey, int correct, int total, string? trainee, CancellationToken ct) =>
        training.RecordExerciseSetAsync(courseKey, correct, total, trainee, ct);

    public Task<Result<Guid>> GenerateAiQuizAsync(Guid lessonId, int questions, CancellationToken ct) => training.GenerateAiQuizAsync(lessonId, questions, ct);
}

internal sealed class ApprenticeGateway(ApprenticeService apprentice) : IApprenticeGateway
{
    public Task<IReadOnlyList<TrainingScenario>> ListScenariosAsync(CancellationToken ct) => apprentice.ListScenariosAsync(ct);

    public Task<Result<ScenarioBriefing>> StartAsync(string scenarioKey, string? trainee, CancellationToken ct) => apprentice.StartAsync(scenarioKey, trainee, ct);

    public Task<IReadOnlyList<(string Key, string Title)>> GetTestMenuAsync(Guid attemptId, CancellationToken ct) => apprentice.GetTestMenuAsync(attemptId, ct);

    public Task<Result<ApprenticeTurn>> CheckAsync(Guid attemptId, string input, string? testKey, CancellationToken ct) =>
        apprentice.CheckAsync(attemptId, input, testKey, ct);

    public Task<Result<ScenarioGrade>> SubmitDiagnosisAsync(Guid attemptId, string diagnosis, CancellationToken ct) =>
        apprentice.SubmitDiagnosisAsync(attemptId, diagnosis, ct);
}

internal sealed class ShopGateway(ShopService shop) : IShopGateway
{
    public Task<IReadOnlyList<Customer>> ListCustomersAsync(string? search, CancellationToken ct) => shop.ListCustomersAsync(search, ct);

    public Task<Result<Guid>> SaveCustomerAsync(CustomerInput input, CancellationToken ct) => shop.SaveCustomerAsync(input, ct);

    public Task<Result> DeleteCustomerAsync(Guid id, CancellationToken ct) => shop.DeleteCustomerAsync(id, ct);

    public Task<IReadOnlyList<Estimate>> ListEstimatesAsync(CancellationToken ct) => shop.ListEstimatesAsync(null, ct);

    public Task<Estimate?> GetEstimateAsync(Guid id, CancellationToken ct) => shop.GetEstimateAsync(id, ct);

    public Task<Result<Guid>> SaveEstimateAsync(Guid? id, Guid? customerId, Guid? vehicleId, decimal taxRate, string? notes,
        IReadOnlyList<EstimateLineInput> lines, EstimateStatus status, CancellationToken ct) =>
        shop.SaveEstimateAsync(id, customerId, vehicleId, null, taxRate, notes, lines, status, ct);

    public Task<Result> DeleteEstimateAsync(Guid id, CancellationToken ct) => shop.DeleteEstimateAsync(id, ct);

    public Task<IReadOnlyList<Inspection>> ListInspectionsAsync(CancellationToken ct) => shop.ListInspectionsAsync(null, ct);

    public Task<Result<Guid>> StartInspectionAsync(Guid vehicleId, int? mileage, string? technician, CancellationToken ct) =>
        shop.StartInspectionAsync(vehicleId, mileage, technician, ct);

    public Task<Result> UpdateInspectionItemAsync(Guid itemId, InspectionRating rating, string? measurement, string? notes, CancellationToken ct) =>
        shop.UpdateInspectionItemAsync(itemId, rating, measurement, notes, ct);

    public Task<Result> CompleteInspectionAsync(Guid inspectionId, CancellationToken ct) => shop.CompleteInspectionAsync(inspectionId, ct);
}

internal sealed class HistoryGateway(HistoryService history) : IHistoryGateway
{
    public Task<VehicleHistory?> GetVehicleHistoryAsync(Guid vehicleId, string? search, CancellationToken ct) => history.GetVehicleHistoryAsync(vehicleId, search, ct);

    public Task<IReadOnlyList<(Guid SessionId, string Vehicle, string Diagnosis, string Complaint, IReadOnlyList<string> Codes, DateTime WhenUtc)>>
        SearchConfirmedDiagnosesAsync(string query, CancellationToken ct) => history.SearchConfirmedDiagnosesAsync(query, 20, ct);
}

internal sealed class SearchGateway(UniversalSearchService search) : ISearchGateway
{
    public Task<UniversalSearchResults> SearchAsync(string query, bool includeWeb, CancellationToken ct) => search.SearchAsync(query, includeWeb, ct);
}

internal sealed class SystemGateway(IAiRouter router, IOllamaManagement ollama, SampleDataService samples, WorkstationAuthService auth) : ISystemGateway
{
    public Task<AiStatus> GetAiStatusAsync(bool refresh, CancellationToken ct) => router.GetStatusAsync(refresh, ct);

    public void InvalidateAiRouting() => router.Invalidate();

    public Task<bool> PingOllamaAsync(CancellationToken ct) => ollama.PingAsync(ct);

    public Task<IReadOnlyList<LocalModelInfo>> ListLocalModelsAsync(CancellationToken ct) => ollama.ListModelsAsync(ct);

    public Task PullModelAsync(string model, IProgress<ModelPullProgress>? progress, CancellationToken ct) => ollama.PullModelAsync(model, progress, ct);

    public Task<Result<int>> InstallSampleDataAsync(CancellationToken ct) => samples.InstallAsync(ct);

    public Task<Result> RemoveSampleDataAsync(CancellationToken ct) => samples.RemoveAsync(ct);

    public bool IsSignInRequired => auth.IsSignInRequired;

    public Task<bool> HasPasswordAsync(CancellationToken ct) => auth.HasPasswordAsync(ct);

    public Task<Result> SetPasswordAsync(string? currentPassword, string newPassword, CancellationToken ct) => auth.SetPasswordAsync(currentPassword, newPassword, ct);

    public Task<Result> RemovePasswordAsync(string currentPassword, CancellationToken ct) => auth.RemovePasswordAsync(currentPassword, ct);

    public Task<Result> SignInAsync(string password, CancellationToken ct) => auth.SignInAsync(password, ct);
}
