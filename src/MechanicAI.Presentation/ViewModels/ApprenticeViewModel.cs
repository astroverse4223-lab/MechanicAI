using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>Apprentice mode: diagnose a fictional case by choosing tests, then submit a diagnosis for grading.</summary>
public sealed partial class ApprenticeViewModel(IApprenticeGateway apprentice, IDialogService dialogs, ISettingsStore settings) : ViewModelBase
{
    public ObservableCollection<ScenarioItem> Scenarios { get; } = [];

    public ObservableCollection<ApprenticeTestOption> TestMenu { get; } = [];

    public ObservableCollection<TurnItem> Turns { get; } = [];

    public ObservableCollection<LabeledValue> FreezeFrame { get; } = [];

    public ObservableCollection<string> IdealPath { get; } = [];

    public ObservableCollection<string> CommonMistakes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScenarioCommand))]
    public partial ScenarioItem? SelectedScenario { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInProgress), nameof(HasAttempt))]
    [NotifyCanExecuteChangedFor(nameof(PerformTestCommand), nameof(AskCommand), nameof(SubmitDiagnosisCommand))]
    public partial Guid? AttemptId { get; set; }

    public bool HasAttempt => AttemptId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInProgress))]
    [NotifyCanExecuteChangedFor(nameof(PerformTestCommand), nameof(AskCommand), nameof(SubmitDiagnosisCommand))]
    public partial bool IsGraded { get; set; }

    public bool IsInProgress => AttemptId is not null && !IsGraded;

    [ObservableProperty]
    public partial string BriefingTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BriefingVehicle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BriefingComplaint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? CustomerStatement { get; set; }

    [ObservableProperty]
    public partial string Codes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PerformTestCommand))]
    public partial ApprenticeTestOption? SelectedTest { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string? FreeInput { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    public partial int Score { get; set; }

    public string ScoreText => Score.ToString(System.Globalization.CultureInfo.CurrentCulture);

    [ObservableProperty]
    public partial bool HasFreezeFrame { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitDiagnosisCommand))]
    public partial string? Diagnosis { get; set; }

    [ObservableProperty]
    public partial string GradeSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RootCause { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Debrief { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Passed { get; set; }

    public override Task OnNavigatedToAsync(object? parameter) => Scenarios.Count == 0 ? RunAsync(LoadScenariosAsync) : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadScenariosAsync);

    private async Task LoadScenariosAsync(CancellationToken ct)
    {
        var list = await apprentice.ListScenariosAsync(ct);
        Scenarios.Clear();
        foreach (var s in list) Scenarios.Add(ScenarioItem.From(s));
    }

    private bool CanStart() => SelectedScenario is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartScenarioAsync()
    {
        if (SelectedScenario is not { } scenario) return;
        if (IsInProgress && !await dialogs.ConfirmAsync("Abandon case?", "Start a new case? Progress on the current case will be lost.", "Start new", "Keep going"))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await apprentice.StartAsync(scenario.Key, Clean(settings.Current.Diagnostics.TechnicianName), ct);
            if (!Check(result)) return;
            var b = result.Value!;
            Reset();
            AttemptId = b.AttemptId;
            BriefingTitle = b.Title;
            BriefingVehicle = b.Mileage is { } m ? $"{b.Vehicle} · {m:N0} mi" : b.Vehicle;
            BriefingComplaint = b.Complaint;
            CustomerStatement = b.CustomerStatement;
            Codes = Format.Join(b.Codes, "No codes stored");
            Notice = b.Notice;
            if (b.FreezeFrame is not null)
            {
                foreach (var (key, value) in b.FreezeFrame) FreezeFrame.Add(new LabeledValue(key, value));
            }

            HasFreezeFrame = FreezeFrame.Count > 0;

            var menu = await apprentice.GetTestMenuAsync(b.AttemptId, ct);
            foreach (var (key, title) in menu) TestMenu.Add(new ApprenticeTestOption(key, title));
        });
    }

    private void Reset()
    {
        AttemptId = null;
        IsGraded = false;
        Score = 0;
        Diagnosis = null;
        FreeInput = null;
        SelectedTest = null;
        GradeSummary = RootCause = Debrief = string.Empty;
        Turns.Clear();
        TestMenu.Clear();
        FreezeFrame.Clear();
        HasFreezeFrame = false;
        IdealPath.Clear();
        CommonMistakes.Clear();
    }

    private bool CanPerformTest() => IsInProgress && SelectedTest is not null;

    [RelayCommand(CanExecute = nameof(CanPerformTest))]
    private Task PerformTestAsync() => SelectedTest is { } test ? CheckAsync(test.Title, test.Key) : Task.CompletedTask;

    private bool CanAsk() => IsInProgress && !string.IsNullOrWhiteSpace(FreeInput);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        var text = FreeInput!.Trim();
        FreeInput = null;
        await CheckAsync(text, null);
    }

    private Task CheckAsync(string input, string? testKey) => RunAsync(async ct =>
    {
        if (AttemptId is not { } attempt) return;
        var result = await apprentice.CheckAsync(attempt, input, testKey, ct);
        if (!Check(result)) return;
        Turns.Insert(0, TurnItem.From(result.Value!));
        Score += result.Value!.Points;
    });

    private bool CanSubmit() => IsInProgress && !string.IsNullOrWhiteSpace(Diagnosis);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitDiagnosisAsync()
    {
        if (AttemptId is not { } attempt) return;
        if (!await dialogs.ConfirmAsync("Submit diagnosis?", $"Submit \"{Diagnosis!.Trim()}\" as your diagnosis? The case ends and the root cause is revealed.", "Submit",
                "Keep testing"))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            var result = await apprentice.SubmitDiagnosisAsync(attempt, Diagnosis!.Trim(), ct);
            if (!Check(result)) return;
            var g = result.Value!;
            IsGraded = true;
            Passed = g.Passed;
            Score = g.Score;
            GradeSummary = $"{(g.RootCauseCorrect ? "Correct root cause" : "Root cause missed")} · {g.Score}/{g.MaxScore} points · {(g.Passed ? "PASSED" : "not passed")}";
            RootCause = g.RootCause;
            Debrief = g.Debrief;
            foreach (var step in g.IdealPath) IdealPath.Add(step);
            foreach (var mistake in g.CommonMistakes) CommonMistakes.Add(mistake);
        }, "Grading…");
    }

    [RelayCommand]
    private void Restart() => Reset();
}
