using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>
/// Guided diagnostic sessions: intake, cause probabilities, the recommended next test with
/// outcome recording, go-back, confirmation, repair, verification, AI analysis, and reports.
/// </summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private readonly IDiagnosticsGateway _diagnostics;
    private readonly IVehicleGateway _vehicles;
    private readonly IDialogService _dialogs;
    private readonly IDispatcher _dispatcher;
    private readonly ISettingsStore _settings;
    private readonly ILauncher _launcher;
    private readonly IClock _clock;
    private bool _suppressSessionEvents;

    public DiagnosticsViewModel(
        IDiagnosticsGateway diagnostics,
        IVehicleGateway vehicles,
        IDialogService dialogs,
        IDispatcher dispatcher,
        ISettingsStore settings,
        ILauncher launcher,
        IClock clock)
    {
        _diagnostics = diagnostics;
        _vehicles = vehicles;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _settings = settings;
        _launcher = launcher;
        _clock = clock;
        _diagnostics.SessionChanged += OnSessionChanged;
    }

    // ---------------------------------------------------------------- session list

    public ObservableCollection<SessionItem> Sessions { get; } = [];

    [ObservableProperty]
    public partial bool ShowClosedSessions { get; set; } = true;

    [ObservableProperty]
    public partial SessionItem? SelectedSession { get; set; }

    // ---------------------------------------------------------------- new session form

    public ObservableCollection<VehicleItem> VehicleChoices { get; } = [];

    [ObservableProperty]
    public partial bool IsNewSessionOpen { get; set; }

    /// <summary>Index into <see cref="VehicleChoices"/>; -1 = no saved vehicle.</summary>
    [ObservableProperty]
    public partial int NewVehicleIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string? NewVehicleDescription { get; set; }

    [ObservableProperty]
    public partial string? NewComplaint { get; set; }

    [ObservableProperty]
    public partial string? NewSymptoms { get; set; }

    [ObservableProperty]
    public partial string? NewDtcs { get; set; }

    [ObservableProperty]
    public partial string? NewConditions { get; set; }

    [ObservableProperty]
    public partial string? NewMileage { get; set; }

    [ObservableProperty]
    public partial string? NewSessionError { get; set; }

    // ---------------------------------------------------------------- current session

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession), nameof(IsSessionOpen))]
    [NotifyCanExecuteChangedFor(nameof(RecordResultCommand), nameof(SkipTestCommand), nameof(GoBackCommand), nameof(AbandonCommand), nameof(ReopenCommand))]
    public partial Guid? CurrentSessionId { get; set; }

    public bool HasSession => CurrentSessionId is not null;

    [ObservableProperty]
    public partial string SessionTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VehicleDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StageLabel { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageProgress))]
    public partial int StageIndex { get; set; }

    /// <summary>Workflow progress for a ProgressBar (0 to <see cref="StageCount"/>).</summary>
    public double StageProgress => StageIndex;

    public double StageCount => DiagnosticStages.Workflow.Count - 1;

    [ObservableProperty]
    public partial string Complaint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SymptomsText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSessionOpen))]
    [NotifyCanExecuteChangedFor(nameof(RecordResultCommand), nameof(SkipTestCommand), nameof(GoBackCommand), nameof(AbandonCommand), nameof(ReopenCommand))]
    public partial bool IsClosed { get; set; }

    public bool IsSessionOpen => HasSession && !IsClosed;

    [ObservableProperty]
    public partial bool IsSample { get; set; }

    [ObservableProperty]
    public partial bool CanRecordRepair { get; set; }

    [ObservableProperty]
    public partial bool CanVerify { get; set; }

    [ObservableProperty]
    public partial string? FinalDiagnosis { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiSummary))]
    public partial string? AiSummary { get; set; }

    public bool HasAiSummary => !string.IsNullOrWhiteSpace(AiSummary);

    [ObservableProperty]
    public partial bool EvidenceOutsideTree { get; set; }

    [ObservableProperty]
    public partial bool ShowProbabilities { get; set; } = true;

    public ObservableCollection<string> Dtcs { get; } = [];

    public ObservableCollection<CauseItem> Causes { get; } = [];

    public ObservableCollection<TestItem> RankedTests { get; } = [];

    public ObservableCollection<TestItem> CompletedTests { get; } = [];

    public ObservableCollection<StepItem> Timeline { get; } = [];

    public ObservableCollection<WarningItem> SafetyWarnings { get; } = [];

    public ObservableCollection<CheckItem> InitialChecks { get; } = [];

    public ObservableCollection<QuestionItem> Questions { get; } = [];

    [ObservableProperty]
    public partial bool HasQuestions { get; set; }

    public IReadOnlyList<string> WorkflowStages { get; } = DiagnosticStages.Workflow.Select(w => w.Label).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendedTest))]
    public partial TestItem? RecommendedTest { get; set; }

    public bool HasRecommendedTest => RecommendedTest is not null;

    /// <summary>The test whose result is being recorded (defaults to the recommended test).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTest))]
    [NotifyCanExecuteChangedFor(nameof(RecordResultCommand), nameof(SkipTestCommand))]
    public partial TestItem? ActiveTest { get; set; }

    public bool HasActiveTest => ActiveTest is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordResultCommand))]
    public partial OutcomeOption? SelectedOutcome { get; set; }

    [ObservableProperty]
    public partial string? ActualResult { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddObservationCommand))]
    public partial string? ObservationText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDtcCommand))]
    public partial string? NewDtcText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCauseCommand))]
    public partial string? NewCauseTitle { get; set; }

    [ObservableProperty]
    public partial string? RepairDescription { get; set; }

    [ObservableProperty]
    public partial string? LaborHours { get; set; }

    [ObservableProperty]
    public partial string? VerificationNotes { get; set; }

    [ObservableProperty]
    public partial string? AiActivity { get; set; }

    [ObservableProperty]
    public partial int ReportFormatIndex { get; set; }

    public IReadOnlyList<string> ReportFormats { get; } = new[] { "HTML", "Markdown", "JSON" };

    // ---------------------------------------------------------------- navigation

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        ShowProbabilities = _settings.Current.Diagnostics.ShowProbabilities;
        await RunAsync(LoadListAsync);
        switch (parameter)
        {
            case Guid id:
                await OpenSessionAsync(id);
                break;
            case NavigationParameters.StartDiagnosisFromText start:
                await StartFromTextAsync(start.Text, start.VehicleId);
                break;
            case NavigationParameters.NewDiagnosisForVehicle forVehicle:
                await OpenNewSessionFormAsync(forVehicle.VehicleId);
                break;
            default:
                if (CurrentSessionId is null && _settings.Current.ActiveSessionId is { } active && Sessions.Any(s => s.Id == active))
                {
                    await OpenSessionAsync(active);
                }

                break;
        }
    }

    private async Task LoadListAsync(CancellationToken ct)
    {
        var list = await _diagnostics.ListAsync(100, null, ShowClosedSessions, ct);
        var now = _clock.UtcNow;
        Sessions.Clear();
        foreach (var s in list) Sessions.Add(SessionItem.From(s, now));
        if (CurrentSessionId is { } id) SetSelectedSessionSilently(Sessions.FirstOrDefault(s => s.Id == id));
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        await LoadListAsync(ct);
        if (CurrentSessionId is { } id) await LoadSessionAsync(id, ct);
    });

    partial void OnShowClosedSessionsChanged(bool value) => _ = RunAsync(LoadListAsync);

    private bool _settingSelection;

    private void SetSelectedSessionSilently(SessionItem? item)
    {
        _settingSelection = true;
        try
        {
            SelectedSession = item;
        }
        finally
        {
            _settingSelection = false;
        }
    }

    partial void OnSelectedSessionChanged(SessionItem? value)
    {
        if (_settingSelection || value is null || value.Id == CurrentSessionId) return;
        _ = OpenSessionAsync(value.Id);
    }

    public Task OpenSessionAsync(Guid id) => RunAsync(async ct =>
    {
        await LoadSessionAsync(id, ct);
        if (CurrentSessionId == id) await _settings.UpdateAsync(s => s.ActiveSessionId = id, ct);
    });

    private void OnSessionChanged(object? sender, Guid id)
    {
        if (_suppressSessionEvents) return;
        _dispatcher.Run(() =>
        {
            if (IsBusy) return;
            _ = RunAsync(async ct =>
            {
                await LoadListAsync(ct);
                if (id == CurrentSessionId) await LoadSessionAsync(id, ct);
            });
        });
    }

    // ---------------------------------------------------------------- loading a session

    internal async Task LoadSessionAsync(Guid id, CancellationToken ct)
    {
        var view = await _diagnostics.GetViewAsync(id, ct);
        if (view is null)
        {
            ClearSession();
            ShowError(Error.NotFound("Diagnostic session"));
            return;
        }

        Apply(view);
    }

    private void ClearSession()
    {
        CurrentSessionId = null;
        SessionTitle = VehicleDescription = StageLabel = Complaint = SymptomsText = string.Empty;
        FinalDiagnosis = AiSummary = null;
        RecommendedTest = ActiveTest = null;
        SelectedOutcome = null;
        Dtcs.Clear();
        Causes.Clear();
        RankedTests.Clear();
        CompletedTests.Clear();
        Timeline.Clear();
        SafetyWarnings.Clear();
        InitialChecks.Clear();
        Questions.Clear();
        HasQuestions = false;
    }

    private void Apply(DiagnosticSessionView view)
    {
        var s = view.Session;
        CurrentSessionId = s.Id;
        SetSelectedSessionSilently(Sessions.FirstOrDefault(x => x.Id == s.Id));
        SessionTitle = s.Title;
        VehicleDescription = string.IsNullOrWhiteSpace(s.VehicleDescription) ? "No vehicle recorded" : s.VehicleDescription;
        StageLabel = view.StageLabel;
        var stage = DiagnosticStages.Workflow.Select((w, i) => (w.Status, i)).FirstOrDefault(w => w.Status == s.Status);
        StageIndex = s.Status == DiagnosticSessionStatus.Abandoned ? DiagnosticStages.Workflow.Count - 1 : stage.i;
        Complaint = string.IsNullOrWhiteSpace(s.Complaint) ? "—" : s.Complaint;
        SymptomsText = Format.Join(s.Symptoms.Concat(s.Conditions), "No symptoms recorded");
        IsClosed = s.IsClosed;
        IsSample = s.IsSample;
        FinalDiagnosis = s.FinalDiagnosis;
        AiSummary = s.AiSummary;
        EvidenceOutsideTree = view.EvidenceOutsideTree;
        CanRecordRepair = s.Status == DiagnosticSessionStatus.Repair;
        CanVerify = s.Status == DiagnosticSessionStatus.Verification;

        Dtcs.Clear();
        foreach (var d in s.Dtcs) Dtcs.Add(string.IsNullOrWhiteSpace(d.Description) ? d.Code : $"{d.Code} — {d.Description}");

        Causes.Clear();
        foreach (var cause in s.Causes.OrderByDescending(c => c.Probability))
        {
            Causes.Add(CauseItem.From(cause, view.LeadingCause?.Id == cause.Id));
        }

        RankedTests.Clear();
        foreach (var ranking in view.Ranking) RankedTests.Add(TestItem.From(ranking.Test, ranking.InformationGain));

        CompletedTests.Clear();
        foreach (var test in s.CompletedTests) CompletedTests.Add(TestItem.From(test));
        foreach (var test in s.Tests.Where(t => t.Status == TestStatus.Skipped)) CompletedTests.Add(TestItem.From(test));

        var previousActive = ActiveTest?.Id;
        RecommendedTest = RankedTests.FirstOrDefault();
        ActiveTest = RankedTests.FirstOrDefault(t => t.Id == previousActive) ?? RecommendedTest;
        SelectedOutcome = null;
        ActualResult = null;

        Timeline.Clear();
        foreach (var step in s.Steps.OrderByDescending(x => x.Sequence)) Timeline.Add(StepItem.From(step));

        SafetyWarnings.Clear();
        foreach (var w in view.SafetyWarnings) SafetyWarnings.Add(WarningItem.From(w));

        InitialChecks.Clear();
        foreach (var check in view.InitialChecks)
        {
            var item = new CheckItem(check, s.CompletedInitialChecks.Contains(check));
            item.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName == nameof(CheckItem.IsDone)) await ToggleInitialCheckAsync(item);
            };
            InitialChecks.Add(item);
        }

        Questions.Clear();
        for (var i = 0; i < s.ClarifyingQuestions.Count; i++)
        {
            Questions.Add(new QuestionItem(i, s.ClarifyingQuestions[i].Question, s.ClarifyingQuestions[i].Answer));
        }

        HasQuestions = Questions.Count > 0;
    }

    partial void OnActiveTestChanged(TestItem? value)
    {
        SelectedOutcome = null;
        ActualResult = null;
    }

    // ---------------------------------------------------------------- new session

    [RelayCommand]
    private Task NewSessionAsync() => OpenNewSessionFormAsync(_settings.Current.ActiveVehicleId);

    private Task OpenNewSessionFormAsync(Guid? vehicleId) => RunAsync(async ct =>
    {
        var vehicles = await _vehicles.ListAsync(null, ct);
        VehicleChoices.Clear();
        foreach (var v in vehicles) VehicleChoices.Add(VehicleItem.From(v));
        NewVehicleIndex = vehicleId is { } id ? VehicleChoices.ToList().FindIndex(v => v.Id == id) : -1;
        NewVehicleDescription = NewComplaint = NewSymptoms = NewDtcs = NewConditions = NewMileage = NewSessionError = null;
        IsNewSessionOpen = true;
    });

    [RelayCommand]
    private void CancelNewSession()
    {
        IsNewSessionOpen = false;
        NewSessionError = null;
    }

    /// <summary>Validates the new-session form and builds the command (null + message when invalid).</summary>
    internal (StartDiagnosticSessionCommand? Command, string? Error) BuildStartCommand()
    {
        var dtcs = Format.SplitList(NewDtcs?.Replace(' ', ','));
        var invalid = dtcs.Where(d => !DtcCode.TryParse(d, out _, assumePowertrain: true)).ToList();
        if (invalid.Count > 0) return (null, $"Not a valid trouble code: {string.Join(", ", invalid)}. Codes look like P0302, U0100, B0001, or C0035.");
        var symptoms = Format.SplitList(NewSymptoms);
        if (string.IsNullOrWhiteSpace(NewComplaint) && symptoms.Count == 0 && dtcs.Count == 0)
        {
            return (null, "Enter the customer complaint, at least one symptom, or a trouble code.");
        }

        int? mileage = null;
        if (!string.IsNullOrWhiteSpace(NewMileage))
        {
            mileage = Format.ParseInt(NewMileage);
            if (mileage is null or < 0) return (null, "Mileage must be a whole number.");
        }

        var vehicle = NewVehicleIndex >= 0 && NewVehicleIndex < VehicleChoices.Count ? VehicleChoices[NewVehicleIndex] : null;
        return (new StartDiagnosticSessionCommand
        {
            VehicleId = vehicle?.Id,
            VehicleDescription = vehicle is null ? Clean(NewVehicleDescription) : null,
            Complaint = NewComplaint?.Trim() ?? string.Empty,
            Symptoms = symptoms,
            Dtcs = dtcs,
            Conditions = Format.SplitList(NewConditions),
            Mileage = mileage,
            TechnicianName = Clean(_settings.Current.Diagnostics.TechnicianName),
        }, null);
    }

    [RelayCommand]
    private Task StartSessionAsync() => RunAsync(async ct =>
    {
        var (command, error) = BuildStartCommand();
        NewSessionError = error;
        if (command is null) return;
        var result = await MutateAsync(() => _diagnostics.StartAsync(command, ct));
        if (!result.IsSuccess)
        {
            NewSessionError = result.Error?.Message;
            return;
        }

        IsNewSessionOpen = false;
        await AfterStartAsync(((Result<Guid>)result).Value, ct);
    }, "Building the diagnostic tree…");

    private Task StartFromTextAsync(string text, Guid? vehicleId) => RunAsync(async ct =>
    {
        var result = await MutateAsync(() => _diagnostics.StartFromTextAsync(text, vehicleId, ct));
        if (!Check(result)) return;
        await AfterStartAsync(((Result<Guid>)result).Value, ct);
    }, "Building the diagnostic tree…");

    private async Task AfterStartAsync(Guid id, CancellationToken ct)
    {
        await LoadListAsync(ct);
        await LoadSessionAsync(id, ct);
        await _settings.UpdateAsync(s => s.ActiveSessionId = id, ct);
    }

    // ---------------------------------------------------------------- testing

    [RelayCommand]
    private void SelectTest(TestItem? test)
    {
        if (test is not null) ActiveTest = test;
    }

    private bool CanRecordResult() => IsSessionOpen && ActiveTest is not null && SelectedOutcome is not null;

    [RelayCommand(CanExecute = nameof(CanRecordResult))]
    private Task RecordResultAsync() => SessionOperationAsync(async (id, ct) =>
    {
        if (ActiveTest is null || SelectedOutcome is null) return Result.Success();
        return await _diagnostics.RecordTestResultAsync(
            new RecordTestResultCommand(id, ActiveTest.Id, SelectedOutcome.Key, Clean(ActualResult), Clean(_settings.Current.Diagnostics.TechnicianName)), ct);
    });

    private bool CanSkipTest() => IsSessionOpen && ActiveTest is not null;

    [RelayCommand(CanExecute = nameof(CanSkipTest))]
    private async Task SkipTestAsync()
    {
        if (ActiveTest is not { } test) return;
        var reason = await _dialogs.PromptAsync("Skip test", $"Why are you skipping \"{test.Title}\"? (optional)", null, "Skip");
        if (reason is null) return;
        await SessionOperationAsync((id, ct) => _diagnostics.SkipTestAsync(id, test.Id, Clean(reason), ct));
    }

    [RelayCommand]
    private Task RevertTestAsync(TestItem? test) =>
        test is null ? Task.CompletedTask : SessionOperationAsync((id, ct) => _diagnostics.RevertTestResultAsync(id, test.Id, ct));

    private bool CanGoBack() => IsSessionOpen;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => SessionOperationAsync((id, ct) => _diagnostics.GoBackAsync(id, ct));

    [RelayCommand]
    private async Task RuleOutCauseAsync(CauseItem? cause)
    {
        if (cause is null) return;
        var reason = await _dialogs.PromptAsync("Rule out cause", $"What rules out \"{cause.Title}\"?", null, "Rule out");
        if (reason is null) return;
        await SessionOperationAsync((id, ct) => _diagnostics.SetCauseStatusAsync(id, cause.Id, CauseStatus.RuledOut, Clean(reason), ct));
    }

    [RelayCommand]
    private Task ReopenCauseAsync(CauseItem? cause) =>
        cause is null ? Task.CompletedTask : SessionOperationAsync((id, ct) => _diagnostics.SetCauseStatusAsync(id, cause.Id, CauseStatus.Open, null, ct));

    [RelayCommand]
    private async Task ConfirmDiagnosisAsync(CauseItem? cause)
    {
        if (cause is null) return;
        var notes = await _dialogs.PromptAsync("Confirm diagnosis",
            $"Confirm \"{cause.Title}\" as the cause? Describe the evidence that confirms it (optional).", null, "Confirm");
        if (notes is null) return;
        await SessionOperationAsync((id, ct) => _diagnostics.ConfirmDiagnosisAsync(id, cause.Id, Clean(notes), ct));
    }

    private bool CanAddCause() => !string.IsNullOrWhiteSpace(NewCauseTitle);

    [RelayCommand(CanExecute = nameof(CanAddCause))]
    private async Task AddCauseAsync()
    {
        var title = NewCauseTitle!.Trim();
        if (await SessionOperationAsync((id, ct) => AsResult(_diagnostics.AddCauseAsync(new AddCauseCommand { SessionId = id, Title = title }, ct))))
        {
            NewCauseTitle = null;
        }
    }

    private bool CanAddObservation() => !string.IsNullOrWhiteSpace(ObservationText);

    [RelayCommand(CanExecute = nameof(CanAddObservation))]
    private async Task AddObservationAsync()
    {
        var text = ObservationText!.Trim();
        if (await SessionOperationAsync((id, ct) => _diagnostics.AddObservationAsync(id, text, ct))) ObservationText = null;
    }

    private bool CanAddDtc() => !string.IsNullOrWhiteSpace(NewDtcText);

    [RelayCommand(CanExecute = nameof(CanAddDtc))]
    private async Task AddDtcAsync()
    {
        var code = NewDtcText!.Trim();
        if (!DtcCode.TryParse(code, out _, assumePowertrain: true))
        {
            ShowError(Error.Validation($"'{code}' is not a valid trouble code. Codes look like P0302, U0100, B0001, or C0035."));
            return;
        }

        if (await SessionOperationAsync((id, ct) => _diagnostics.AddDtcAsync(id, code, ct))) NewDtcText = null;
    }

    [RelayCommand]
    private Task RemoveDtcAsync(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return Task.CompletedTask;
        var code = display.Split(' ', 2)[0];
        return SessionOperationAsync((id, ct) => _diagnostics.RemoveDtcAsync(id, code, ct));
    }

    private Task ToggleInitialCheckAsync(CheckItem item) =>
        SessionOperationAsync((id, ct) => _diagnostics.SetInitialCheckAsync(id, item.Text, item.IsDone, ct), reload: false);

    [RelayCommand]
    private Task AnswerQuestionAsync(QuestionItem? question)
    {
        if (question is null || string.IsNullOrWhiteSpace(question.Answer)) return Task.CompletedTask;
        return SessionOperationAsync((id, ct) => _diagnostics.AnswerQuestionAsync(id, question.Index, question.Answer, ct));
    }

    // ---------------------------------------------------------------- repair & verification

    [RelayCommand]
    private async Task RecordRepairAsync()
    {
        if (string.IsNullOrWhiteSpace(RepairDescription))
        {
            ShowError(Error.Validation("Describe the repair performed."));
            return;
        }

        decimal? hours = null;
        if (!string.IsNullOrWhiteSpace(LaborHours))
        {
            hours = Format.ParseDecimal(LaborHours);
            if (hours is null or < 0)
            {
                ShowError(Error.Validation("Labor hours must be a number."));
                return;
            }
        }

        var description = RepairDescription.Trim();
        if (await SessionOperationAsync((id, ct) =>
                _diagnostics.RecordRepairAsync(new RecordRepairCommand(id, description, [], hours, Clean(_settings.Current.Diagnostics.TechnicianName)), ct)))
        {
            RepairDescription = null;
            LaborHours = null;
        }
    }

    [RelayCommand]
    private Task VerifyPassedAsync() => RecordVerificationAsync(true);

    [RelayCommand]
    private Task VerifyFailedAsync() => RecordVerificationAsync(false);

    private async Task RecordVerificationAsync(bool passed)
    {
        var notes = VerificationNotes?.Trim() ?? string.Empty;
        if (!passed && notes.Length == 0)
        {
            ShowError(Error.Validation("Describe what still happens so testing can continue."));
            return;
        }

        if (await SessionOperationAsync((id, ct) =>
                _diagnostics.RecordVerificationAsync(new RecordVerificationCommand(id, passed, notes, Clean(_settings.Current.Diagnostics.TechnicianName)), ct)))
        {
            VerificationNotes = null;
        }
    }

    private bool CanReopen() => HasSession && IsClosed;

    [RelayCommand(CanExecute = nameof(CanReopen))]
    private Task ReopenAsync() => SessionOperationAsync((id, ct) => _diagnostics.ReopenAsync(id, ct));

    private bool CanAbandon() => IsSessionOpen;

    [RelayCommand(CanExecute = nameof(CanAbandon))]
    private async Task AbandonAsync()
    {
        var reason = await _dialogs.PromptAsync("Close session", "Close this session without a repair? Reason (optional):", null, "Close session");
        if (reason is null) return;
        await SessionOperationAsync((id, ct) => _diagnostics.AbandonAsync(id, Clean(reason), ct));
    }

    [RelayCommand]
    private async Task DeleteSessionAsync()
    {
        if (CurrentSessionId is not { } id) return;
        if (!await _dialogs.ConfirmAsync("Delete session?", $"Delete \"{SessionTitle}\" and its complete audit trail? This cannot be undone.", "Delete",
                "Cancel", destructive: true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            if (!Check(await MutateAsync(() => _diagnostics.DeleteAsync(id, ct)))) return;
            ClearSession();
            await LoadListAsync(ct);
        });
    }

    // ---------------------------------------------------------------- AI & reports

    [RelayCommand]
    private Task AnalyzeWithAiAsync() => RunAsync(async ct =>
    {
        if (CurrentSessionId is not { } id) return;
        AiActivity = "Starting AI analysis…";
        var result = await MutateAsync(() => _diagnostics.AnalyzeWithAiAsync(id, e =>
        {
            var text = e switch
            {
                AgentStatus s => s.Message,
                AgentToolStarted t => $"Using {t.Tool}…",
                AgentToolFinished f => f.IsError ? $"{f.Tool} failed: {f.Summary}" : $"{f.Tool}: {f.Summary}",
                _ => null,
            };
            if (text is not null) _dispatcher.Run(() => AiActivity = text);
            return Task.CompletedTask;
        }, ct));
        AiActivity = null;
        if (!Check(result)) return;
        var value = ((Result<DiagnosticAiResult>)result).Value!;
        await LoadSessionAsync(id, ct);
        ShowNotice("AI analysis added",
            $"{value.CausesAdded} cause(s), {value.TestsAdded} test(s) and {value.PriorsAdjusted} likelihood adjustment(s) proposed. AI suggestions are labeled as AI inference.",
            NoticeSeverity.Informational);
    }, "AI is analyzing the session…");

    [RelayCommand]
    private Task ExportReportAsync() => RunAsync(async ct =>
    {
        if (CurrentSessionId is not { } id) return;
        var format = ReportFormatIndex switch
        {
            1 => ReportFormat.Markdown,
            2 => ReportFormat.Json,
            _ => ReportFormat.Html,
        };
        var result = await _diagnostics.ExportReportAsync(id, format, ct);
        if (!Check(result)) return;
        await _launcher.OpenFileAsync(result.Value!);
        StatusMessage = $"Report saved to {result.Value}";
    });

    // ---------------------------------------------------------------- helpers

    private static async Task<Result> AsResult<T>(Task<Result<T>> task) => await task;

    /// <summary>Runs a mutation while ignoring the service's change event (we reload explicitly).</summary>
    private async Task<Result> MutateAsync(Func<Task<Result>> mutation)
    {
        _suppressSessionEvents = true;
        try
        {
            return await mutation();
        }
        finally
        {
            _suppressSessionEvents = false;
        }
    }

    private async Task<Result> MutateAsync<T>(Func<Task<Result<T>>> mutation) => await MutateAsync(async () => (Result)await mutation());

    /// <summary>Applies a mutation to the current session, shows any error, and reloads. Returns true on success.</summary>
    private async Task<bool> SessionOperationAsync(Func<Guid, CancellationToken, Task<Result>> operation, bool reload = true)
    {
        if (CurrentSessionId is not { } id) return false;
        var succeeded = false;
        await RunAsync(async ct =>
        {
            var result = await MutateAsync(() => operation(id, ct));
            if (!Check(result)) return;
            succeeded = true;
            if (reload)
            {
                await LoadSessionAsync(id, ct);
                await LoadListAsync(ct);
            }
        });
        return succeeded;
    }
}
