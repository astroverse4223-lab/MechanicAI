using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Commands;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.DTOs;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;
using MechanicAI.Presentation.ViewModels;

namespace MechanicAI.Presentation.Tests;

public sealed class DiagnosticsViewModelTests
{
    private readonly IDiagnosticsGateway _diagnostics = Substitute.For<IDiagnosticsGateway>();
    private readonly IVehicleGateway _vehicles = Substitute.For<IVehicleGateway>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ILauncher _launcher = Substitute.For<ILauncher>();
    private readonly InMemorySettingsStore _settings = new();

    public DiagnosticsViewModelTests()
    {
        _diagnostics.ListAsync(Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([]);
        _vehicles.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    private DiagnosticsViewModel Create() =>
        new(_diagnostics, _vehicles, _dialogs, new ImmediateDispatcher(), _settings, _launcher, new FixedClock(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc)));

    /// <summary>A small session: two causes, one recommended test with pass/fail outcomes.</summary>
    private static DiagnosticSessionView BuildView(Guid sessionId, out DiagnosticTest test, out DiagnosticNode coil)
    {
        var session = new DiagnosticSession
        {
            Id = sessionId,
            Title = "Misfire cylinder 2",
            VehicleDescription = "2019 Ford F-150 3.5L",
            Complaint = "Rough idle",
            Status = DiagnosticSessionStatus.Testing,
        };
        coil = new DiagnosticNode { SessionId = sessionId, Kind = DiagnosticNodeKind.Cause, Key = "ignition-coil", Title = "Ignition coil", Probability = 0.62 };
        var plug = new DiagnosticNode { SessionId = sessionId, Kind = DiagnosticNodeKind.Cause, Key = "spark-plug", Title = "Spark plug", Probability = 0.38 };
        session.Nodes.AddRange([coil, plug]);
        test = new DiagnosticTest
        {
            SessionId = sessionId,
            Key = "swap-coil",
            Title = "Swap coil to another cylinder",
            Procedure = ["Swap coils 2 and 3", "Clear codes and run"],
            Outcomes =
            [
                new TestOutcomeDefinition { Key = "follows", Label = "Misfire follows the coil", Normal = false },
                new TestOutcomeDefinition { Key = "stays", Label = "Misfire stays on cylinder 2", Normal = true },
            ],
        };
        session.Tests.Add(test);
        session.Dtcs.Add(new SessionDtc { SessionId = sessionId, Code = "P0302", Description = "Cylinder 2 misfire detected" });
        return new DiagnosticSessionView(session, [new TestRanking(test, 0.9, 1.2)], coil, false, [], DiagnosticStages.InitialChecks);
    }

    [Fact]
    public void Start_form_rejects_invalid_trouble_codes()
    {
        var vm = Create();
        vm.NewComplaint = "Check engine light";
        vm.NewDtcs = "P0302, X12";

        var (command, error) = vm.BuildStartCommand();

        Assert.Null(command);
        Assert.Contains("X12", error);
    }

    [Fact]
    public void Start_form_requires_a_complaint_symptom_or_code()
    {
        var vm = Create();

        var (command, error) = vm.BuildStartCommand();

        Assert.Null(command);
        Assert.StartsWith("Enter the customer complaint", error);
    }

    [Fact]
    public void Start_form_builds_the_command()
    {
        _settings.Current.Diagnostics.TechnicianName = "Sam";
        var vm = Create();
        vm.NewComplaint = " Rough idle ";
        vm.NewDtcs = "p0302 P0300";
        vm.NewSymptoms = "rough idle; hesitation";
        vm.NewMileage = "84,000";
        vm.NewVehicleDescription = "2019 F-150";

        var (command, error) = vm.BuildStartCommand();

        Assert.Null(error);
        Assert.Equal("Rough idle", command!.Complaint);
        Assert.Equal(["p0302", "P0300"], command.Dtcs);
        Assert.Equal(["rough idle", "hesitation"], command.Symptoms);
        Assert.Equal(84000, command.Mileage);
        Assert.Equal("2019 F-150", command.VehicleDescription);
        Assert.Equal("Sam", command.TechnicianName);
    }

    [Fact]
    public async Task Start_failure_is_shown_in_the_form()
    {
        _diagnostics.StartAsync(Arg.Any<StartDiagnosticSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>(Error.NotFound("Vehicle")));
        var vm = Create();
        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.NewComplaint = "No start";

        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal("Vehicle was not found.", vm.NewSessionError);
        Assert.True(vm.IsNewSessionOpen);
        Assert.False(vm.HasSession);
    }

    [Fact]
    public async Task Navigating_with_free_text_starts_and_loads_a_session()
    {
        var id = Guid.NewGuid();
        _diagnostics.StartFromTextAsync("2019 F-150 P0302", null, Arg.Any<CancellationToken>()).Returns(Result.Success(id));
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out _));
        var vm = Create();

        await vm.OnNavigatedToAsync(new NavigationParameters.StartDiagnosisFromText("2019 F-150 P0302"));

        Assert.Equal(id, vm.CurrentSessionId);
        Assert.Equal("Misfire cylinder 2", vm.SessionTitle);
        Assert.Equal(2, vm.Causes.Count);
        Assert.True(vm.Causes[0].IsLeading);
        Assert.Equal("Ignition coil", vm.Causes[0].Title);
        Assert.Equal(62, vm.Causes[0].ProbabilityPercent);
        Assert.Equal("Swap coil to another cylinder", vm.RecommendedTest?.Title);
        Assert.Same(vm.RecommendedTest, vm.ActiveTest);
        Assert.Equal(2, vm.ActiveTest!.Outcomes.Count);
        Assert.Equal("Abnormal (FAIL)", vm.ActiveTest.Outcomes[0].ResultText);
        Assert.Contains("P0302 — Cylinder 2 misfire detected", vm.Dtcs);
        Assert.Equal(DiagnosticStages.InitialChecks.Count, vm.InitialChecks.Count);
        Assert.Equal(id, _settings.Current.ActiveSessionId);
    }

    [Fact]
    public async Task Free_text_that_cannot_start_a_session_shows_a_warning()
    {
        _diagnostics.StartFromTextAsync("hello", null, Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>(Error.Validation("Describe the problem or enter a trouble code so a diagnostic session can be built.")));
        var vm = Create();

        await vm.OnNavigatedToAsync(new NavigationParameters.StartDiagnosisFromText("hello"));

        Assert.False(vm.HasSession);
        Assert.True(vm.IsNoticeOpen);
        Assert.Equal(NoticeSeverity.Warning, vm.NoticeSeverity);
    }

    [Fact]
    public async Task Recording_a_result_requires_an_outcome_and_sends_it()
    {
        var id = Guid.NewGuid();
        var view = BuildView(id, out var test, out _);
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(view);
        _diagnostics.RecordTestResultAsync(Arg.Any<RecordTestResultCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = Create();
        await vm.OpenSessionAsync(id);

        Assert.False(vm.RecordResultCommand.CanExecute(null));
        vm.SelectedOutcome = vm.ActiveTest!.Outcomes[0];
        vm.ActualResult = "  Misfire moved to cyl 3 ";
        Assert.True(vm.RecordResultCommand.CanExecute(null));
        await vm.RecordResultCommand.ExecuteAsync(null);

        await _diagnostics.Received(1).RecordTestResultAsync(
            Arg.Is<RecordTestResultCommand>(c => c.SessionId == id && c.TestId == test.Id && c.OutcomeKey == "follows" && c.ActualResult == "Misfire moved to cyl 3"),
            Arg.Any<CancellationToken>());
        await _diagnostics.Received(2).GetViewAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rejected_result_is_displayed_and_the_session_is_not_reloaded()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out _));
        _diagnostics.RecordTestResultAsync(Arg.Any<RecordTestResultCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(Error.Validation("This session is closed. Reopen it to record more results.")));
        var vm = Create();
        await vm.OpenSessionAsync(id);
        vm.SelectedOutcome = vm.ActiveTest!.Outcomes[1];

        await vm.RecordResultCommand.ExecuteAsync(null);

        Assert.Equal("This session is closed. Reopen it to record more results.", vm.NoticeMessage);
        await _diagnostics.Received(1).GetViewAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Go_back_reverts_through_the_gateway()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out _));
        _diagnostics.GoBackAsync(id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = Create();
        await vm.OpenSessionAsync(id);

        await vm.GoBackCommand.ExecuteAsync(null);

        await _diagnostics.Received(1).GoBackAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirming_a_diagnosis_uses_the_dialog_and_can_be_cancelled()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out var coil));
        _diagnostics.ConfirmDiagnosisAsync(id, coil.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = Create();
        await vm.OpenSessionAsync(id);
        var cause = vm.Causes.First(c => c.Id == coil.Id);

        _dialogs.PromptAsync(default!, default!, default, default!).ReturnsForAnyArgs((string?)null);
        await vm.ConfirmDiagnosisCommand.ExecuteAsync(cause);
        await _diagnostics.DidNotReceiveWithAnyArgs().ConfirmDiagnosisAsync(default, default, default, default);

        _dialogs.PromptAsync(default!, default!, default, default!).ReturnsForAnyArgs("Primary resistance open");
        await vm.ConfirmDiagnosisCommand.ExecuteAsync(cause);
        await _diagnostics.Received(1).ConfirmDiagnosisAsync(id, coil.Id, "Primary resistance open", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_an_invalid_code_is_rejected_locally()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out _));
        var vm = Create();
        await vm.OpenSessionAsync(id);
        vm.NewDtcText = "ZZZ";

        await vm.AddDtcCommand.ExecuteAsync(null);

        Assert.Contains("not a valid trouble code", vm.NoticeMessage);
        await _diagnostics.DidNotReceiveWithAnyArgs().AddDtcAsync(default, default!, default);
    }

    [Fact]
    public async Task Failed_verification_requires_notes()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out _));
        var vm = Create();
        await vm.OpenSessionAsync(id);

        await vm.VerifyFailedCommand.ExecuteAsync(null);

        Assert.Equal(NoticeSeverity.Warning, vm.NoticeSeverity);
        await _diagnostics.DidNotReceiveWithAnyArgs().RecordVerificationAsync(default!, default);
    }

    [Fact]
    public async Task Missing_session_shows_not_found()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns((DiagnosticSessionView?)null);
        var vm = Create();

        await vm.OpenSessionAsync(id);

        Assert.False(vm.HasSession);
        Assert.Equal(ErrorKind.NotFound, vm.LastError?.Kind);
    }

    [Fact]
    public async Task Export_opens_the_written_report()
    {
        var id = Guid.NewGuid();
        _diagnostics.GetViewAsync(id, Arg.Any<CancellationToken>()).Returns(BuildView(id, out _, out _));
        _diagnostics.ExportReportAsync(id, Application.Services.ReportFormat.Markdown, Arg.Any<CancellationToken>()).Returns(Result.Success("/tmp/report.md"));
        var vm = Create();
        await vm.OpenSessionAsync(id);
        vm.ReportFormatIndex = 1;

        await vm.ExportReportCommand.ExecuteAsync(null);

        await _launcher.Received(1).OpenFileAsync("/tmp/report.md");
    }
}
