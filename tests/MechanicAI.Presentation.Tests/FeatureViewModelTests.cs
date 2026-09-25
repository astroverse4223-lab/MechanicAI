using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;
using MechanicAI.Presentation.ViewModels;

namespace MechanicAI.Presentation.Tests;

public sealed class DtcLookupViewModelTests
{
    [Fact]
    public async Task Navigating_with_a_code_searches_and_opens_its_detail()
    {
        var dtcs = Substitute.For<IDtcGateway>();
        dtcs.SearchAsync("P0301", 50, Arg.Any<CancellationToken>()).Returns(
            [new DtcSearchResult("P0301", "Cylinder 1 misfire detected", "Ignition", DtcSystem.Powertrain, true, null, "Exact code", true)]);
        dtcs.GetDetailAsync("P0301", null, Arg.Any<CancellationToken>()).Returns(new DtcDetail("P0301", DtcSystem.Powertrain, true, "Ignition system or misfire",
            [new DtcDefinition { Code = "P0301", Description = "Cylinder 1 misfire detected", Source = "SAE J2012", Causes = ["Spark plug", "Ignition coil"] }],
            [], [], [], 3));
        var navigation = Substitute.For<INavigationService>();
        var vm = new DtcLookupViewModel(dtcs, Substitute.For<IVehicleGateway>(), new InMemorySettingsStore(), navigation, Substitute.For<IClipboard>());

        await vm.OnNavigatedToAsync("P0301");

        Assert.Single(vm.Results);
        Assert.Equal("P0301", vm.DetailCode);
        Assert.Equal("Cylinder 1 misfire detected", vm.DetailDescription);
        Assert.Equal(["Spark plug", "Ignition coil"], vm.PossibleCauses);
        Assert.Contains("3", vm.SeenText);

        vm.StartDiagnosisCommand.Execute(null);
        navigation.Received(1).NavigateTo(PageKey.Diagnostics, Arg.Is<object?>(new NavigationParameters.StartDiagnosisFromText("P0301", null)));
    }

    [Fact]
    public async Task No_results_shows_an_informational_notice()
    {
        var dtcs = Substitute.For<IDtcGateway>();
        dtcs.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var vm = new DtcLookupViewModel(dtcs, Substitute.For<IVehicleGateway>(), new InMemorySettingsStore(), Substitute.For<INavigationService>(),
            Substitute.For<IClipboard>());
        Assert.False(vm.SearchCommand.CanExecute(null));
        vm.Query = "P1XYZ";

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.HasResults);
        Assert.Equal(NoticeSeverity.Informational, vm.NoticeSeverity);
    }
}

public sealed class KnowledgeBaseViewModelTests
{
    [Theory]
    [InlineData("manual.pdf", 1000, null)]
    [InlineData("notes.md", 10, null)]
    [InlineData("setup.exe", 1000, "unsupported file type")]
    [InlineData("empty.pdf", 0, "empty")]
    public void Upload_validation(string name, long size, string? expectedFragment)
    {
        var file = new PickedFile(name, null, size, _ => Task.FromResult<Stream>(new MemoryStream()));

        var error = KnowledgeBaseViewModel.ValidateUpload(file);

        if (expectedFragment is null) Assert.Null(error);
        else Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public async Task Upload_skips_invalid_files_and_reports_them()
    {
        var kb = Substitute.For<IKnowledgeBaseGateway>();
        kb.ListAsync(Arg.Any<DocumentKind?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
        kb.UploadAsync(Arg.Any<Stream>(), "manual.pdf", Arg.Any<DocumentUploadOptions>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));
        var files = Substitute.For<IFilePicker>();
        files.PickFilesAsync(Arg.Any<IReadOnlyList<string>>()).Returns([TestFiles.File("manual.pdf", [1, 2, 3]), TestFiles.File("virus.exe", [1])]);
        var vm = new KnowledgeBaseViewModel(kb, files, Substitute.For<IDialogService>(), Substitute.For<ILauncher>(), new ImmediateDispatcher(),
            Substitute.For<IBackgroundTaskQueue>());

        await vm.UploadCommand.ExecuteAsync(null);

        await kb.Received(1).UploadAsync(Arg.Any<Stream>(), "manual.pdf", Arg.Is<DocumentUploadOptions>(o => o.Kind == DocumentKind.ServiceManual),
            Arg.Any<CancellationToken>());
        await kb.DidNotReceive().UploadAsync(Arg.Any<Stream>(), "virus.exe", Arg.Any<DocumentUploadOptions>(), Arg.Any<CancellationToken>());
        Assert.Contains("virus.exe", vm.NoticeMessage);
    }

    [Fact]
    public async Task Ask_streams_text_then_shows_sources()
    {
        var kb = Substitute.For<IKnowledgeBaseGateway>();
        kb.AskAsync("torque for caliper bracket", DocumentQuestionTemplate.Free, Arg.Any<KnowledgeSearchOptions?>(), Arg.Any<Func<string, Task>?>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var onText = call.ArgAt<Func<string, Task>?>(3)!;
                await onText("Partial ");
                return Result.Success(new DocumentAnswer("Final answer [D1]", [], [new Domain.ValueObjects.SourceCitation { Label = "D1", Title = "Brakes manual", PageNumber = 12 }],
                    [], "llama", true));
            });
        var vm = new KnowledgeBaseViewModel(kb, Substitute.For<IFilePicker>(), Substitute.For<IDialogService>(), Substitute.For<ILauncher>(),
            new ImmediateDispatcher(), Substitute.For<IBackgroundTaskQueue>());
        Assert.False(vm.AskCommand.CanExecute(null));
        vm.Question = "torque for caliper bracket";

        await vm.AskCommand.ExecuteAsync(null);

        Assert.Equal("Final answer [D1]", vm.Answer);
        var source = Assert.Single(vm.AnswerSources);
        Assert.Contains("page 12", source.Detail);
    }

    [Fact]
    public async Task Ask_failure_clears_the_answer_and_shows_setup_needed()
    {
        var kb = Substitute.For<IKnowledgeBaseGateway>();
        kb.AskAsync(default!, default, default, default, default).ReturnsForAnyArgs(Result.Failure<DocumentAnswer>(Error.NotConfigured("No AI model is configured.")));
        var vm = new KnowledgeBaseViewModel(kb, Substitute.For<IFilePicker>(), Substitute.For<IDialogService>(), Substitute.For<ILauncher>(),
            new ImmediateDispatcher(), Substitute.For<IBackgroundTaskQueue>()) { Question = "anything" };

        await vm.AskCommand.ExecuteAsync(null);

        Assert.Null(vm.Answer);
        Assert.Equal("Setup needed", vm.NoticeTitle);
    }
}

public sealed class ShopViewModelTests
{
    private static ShopViewModel Create(IShopGateway? shop = null) =>
        new(shop ?? Substitute.For<IShopGateway>(), Substitute.For<IVehicleGateway>(), Substitute.For<IDialogService>(), new InMemorySettingsStore());

    [Fact]
    public void Estimate_totals_update_live_and_tax_applies_only_to_taxable_lines()
    {
        var vm = Create();
        vm.NewEstimateCommand.Execute(null);
        var labor = vm.EstimateLines[0];
        labor.KindIndex = 0;
        labor.Description = "Diagnose misfire";
        labor.QuantityText = "1.5";
        labor.UnitPriceText = "100";
        labor.Taxable = false;
        vm.AddLineCommand.Execute(null);
        var part = vm.EstimateLines[1];
        part.Description = "Ignition coil";
        part.UnitPriceText = "80";
        vm.TaxRatePercent = "10";

        Assert.Equal(230m, vm.Subtotal);
        Assert.Equal(8m, vm.Tax);
        Assert.Null(vm.ValidateEstimate(out var rate, out var lines));
        Assert.Equal(0.10m, rate);
        Assert.Equal(2, lines.Count);
        Assert.Equal(EstimateLineKind.Labor, lines[0].Kind);
    }

    [Theory]
    [InlineData("75", "Tax rate")]
    [InlineData("abc", "Tax rate")]
    public void Estimate_rejects_bad_tax_rates(string rate, string fragment)
    {
        var vm = Create();
        vm.NewEstimateCommand.Execute(null);
        vm.EstimateLines[0].Description = "Oil change";
        vm.TaxRatePercent = rate;

        Assert.Contains(fragment, vm.ValidateEstimate(out _, out _));
    }

    [Fact]
    public void Estimate_rejects_bad_prices_and_empty_lines()
    {
        var vm = Create();
        vm.NewEstimateCommand.Execute(null);
        Assert.Equal("Add at least one line with a description.", vm.ValidateEstimate(out _, out _));
        vm.EstimateLines[0].Description = "Brake pads";
        vm.EstimateLines[0].UnitPriceText = "-5";
        Assert.Contains("price", vm.ValidateEstimate(out _, out _));
    }

    [Fact]
    public async Task Customer_validation_blocks_saving()
    {
        var shop = Substitute.For<IShopGateway>();
        var vm = Create(shop);
        vm.NewCustomerCommand.Execute(null);
        await vm.SaveCustomerCommand.ExecuteAsync(null);
        Assert.Equal("Enter a name or a company.", vm.CustomerError);

        vm.CustomerFirstName = "Dana";
        vm.CustomerEmail = "not an email";
        await vm.SaveCustomerCommand.ExecuteAsync(null);
        Assert.Equal("The email address is not valid.", vm.CustomerError);
        await shop.DidNotReceiveWithAnyArgs().SaveCustomerAsync(default!, default);
    }
}

public sealed class SettingsViewModelTests
{
    private readonly InMemorySettingsStore _settings = new();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();
    private readonly ISystemGateway _system = Substitute.For<ISystemGateway>();

    public SettingsViewModelTests()
    {
        _secrets.ListNamesAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    private SettingsViewModel Create()
    {
        var paths = Substitute.For<IAppPaths>();
        paths.DataRoot.Returns("/data");
        return new SettingsViewModel(_settings, _secrets, _system, Substitute.For<IDialogService>(), Substitute.For<ILauncher>(), paths, new ImmediateDispatcher());
    }

    [Fact]
    public async Task Invalid_urls_and_cloud_without_consent_are_rejected()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        vm.OllamaBaseUrl = "localhost:11434";
        vm.AiModeIndex = (int)Application.Settings.AiMode.Hybrid;
        vm.AllowCloudAi = false;

        var errors = vm.Validate();

        Assert.Contains(errors, e => e.StartsWith("Ollama URL", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Allow sending data to cloud AI", StringComparison.Ordinal));

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, _settings.SaveCount);
        Assert.Equal(NoticeSeverity.Warning, vm.NoticeSeverity);
    }

    [Fact]
    public async Task Save_persists_settings_and_stores_only_entered_keys()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        vm.AiModeIndex = (int)Application.Settings.AiMode.Cloud;
        vm.AllowCloudAi = true;
        vm.CloudProviderIndex = 0;
        vm.AnthropicApiKey = "  sk-ant-test  ";
        vm.TechnicianName = " Sam ";
        vm.ThemeIndex = (int)Application.Settings.AppTheme.Light;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Application.Settings.AiMode.Cloud, _settings.Current.Ai.Mode);
        Assert.True(_settings.Current.Privacy.AllowCloudAi);
        Assert.Equal("Sam", _settings.Current.Diagnostics.TechnicianName);
        Assert.Equal(Application.Settings.AppTheme.Light, _settings.Current.Appearance.Theme);
        await _secrets.Received(1).SetAsync(SecretNames.AnthropicApiKey, "sk-ant-test", Arg.Any<CancellationToken>());
        await _secrets.DidNotReceive().SetAsync(SecretNames.OpenAiApiKey, Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(string.Empty, vm.AnthropicApiKey);
        _system.Received(1).InvalidateAiRouting();
        Assert.Equal(NoticeSeverity.Success, vm.NoticeSeverity);
    }

    [Fact]
    public async Task Require_sign_in_is_not_saved_without_a_password()
    {
        _system.HasPasswordAsync(Arg.Any<CancellationToken>()).Returns(false);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        vm.RequireSignIn = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(_settings.Current.Security.RequireSignIn);
        Assert.False(vm.RequireSignIn);
        Assert.Equal(NoticeSeverity.Warning, vm.NoticeSeverity);
    }

    [Fact]
    public async Task Mismatched_passwords_are_rejected_locally()
    {
        var vm = Create();
        vm.NewPassword = "longpassword1";
        vm.ConfirmPassword = "longpassword2";

        await vm.SetPasswordCommand.ExecuteAsync(null);

        await _system.DidNotReceiveWithAnyArgs().SetPasswordAsync(default, default!, default);
        Assert.Contains("do not match", vm.NoticeMessage);
    }
}

public sealed class AssistantViewModelTests
{
    [Fact]
    public async Task Send_creates_a_conversation_streams_the_reply_and_adds_a_footer()
    {
        var gateway = Substitute.For<IAssistantGateway>();
        var conversationId = Guid.NewGuid();
        gateway.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.CreateAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(conversationId);
        var streamed = new List<string>();
        ChatMessageItem? reply = null;
        AssistantViewModel? vm = null;
        gateway.SendAsync(conversationId, "What does P0171 mean?", Arg.Any<IReadOnlyList<ChatImage>>(), Arg.Any<Func<AgentEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var onEvent = call.ArgAt<Func<AgentEvent, Task>?>(3)!;
                await onEvent(new AgentToolStarted("1", "search_dtc", "{}"));
                reply = vm!.Messages[^1];
                streamed.Add(reply.Activity ?? string.Empty);
                await onEvent(new AgentTextDelta("System too "));
                await onEvent(new AgentTextDelta("lean, bank 1"));
                streamed.Add(reply.Text);
                return Result.Success(new AssistantReply(conversationId, Guid.NewGuid(), "System too lean, bank 1 [S1]",
                    [new Domain.ValueObjects.SourceCitation { Label = "S1", Title = "DTC database" }], [], [], "ollama", "qwen3", 1500));
            });
        vm = new AssistantViewModel(gateway, new ImmediateDispatcher(), Substitute.For<IDialogService>(), Substitute.For<IFilePicker>(),
            Substitute.For<IClipboard>(), new InMemorySettingsStore(), new FixedClock(DateTime.UtcNow));
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Draft = "What does P0171 mean?";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["Using search_dtc…", "System too lean, bank 1"], streamed);
        Assert.Equal(2, vm.Messages.Count);
        Assert.True(vm.Messages[0].IsUser);
        Assert.Equal("System too lean, bank 1 [S1]", vm.Messages[1].Text);
        Assert.Contains("[S1] DTC database", vm.Messages[1].Footer);
        Assert.Null(vm.Messages[1].Activity);
        Assert.False(vm.IsSending);
        Assert.Equal(string.Empty, vm.Draft);
    }

    [Fact]
    public async Task Send_failure_shows_the_error_in_the_reply_and_as_a_notice()
    {
        var gateway = Substitute.For<IAssistantGateway>();
        var id = Guid.NewGuid();
        gateway.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        gateway.CreateAsync(Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(id);
        gateway.SendAsync(default, default!, default!, default, default)
            .ReturnsForAnyArgs(Result.Failure<AssistantReply>(Error.NotConfigured("No AI model is available. Configure one in Settings.")));
        var vm = new AssistantViewModel(gateway, new ImmediateDispatcher(), Substitute.For<IDialogService>(), Substitute.For<IFilePicker>(),
            Substitute.For<IClipboard>(), new InMemorySettingsStore(), new FixedClock(DateTime.UtcNow)) { Draft = "hi" };

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("No AI model is available. Configure one in Settings.", vm.Messages[^1].Text);
        Assert.Equal("Setup needed", vm.NoticeTitle);
    }
}

public sealed class ApprenticeViewModelTests
{
    [Fact]
    public async Task Full_case_flow_scores_turns_and_grades()
    {
        var gateway = Substitute.For<IApprenticeGateway>();
        var attempt = Guid.NewGuid();
        gateway.ListScenariosAsync(Arg.Any<CancellationToken>()).Returns([new TrainingScenario { Key = "lean", Title = "Lean condition", VehicleDescription = "2015 Civic" }]);
        gateway.StartAsync("lean", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new ScenarioBriefing(attempt, "lean", "Lean condition",
            "2015 Civic", 90000, "Check engine light", null, ["P0171"], null, TrainingLevel.Beginner, "Fictional")));
        gateway.GetTestMenuAsync(attempt, Arg.Any<CancellationToken>()).Returns([("smoke", "Smoke test intake")]);
        gateway.CheckAsync(attempt, "Smoke test intake", "smoke", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ApprenticeTurn("Smoke test intake", "smoke", "Smoke test intake", "Smoke at PCV hose", 10, "Good choice", DateTime.UtcNow)));
        gateway.SubmitDiagnosisAsync(attempt, "Cracked PCV hose", Arg.Any<CancellationToken>()).Returns(Result.Success(new ScenarioGrade(true, 90, 100, true,
            "Cracked PCV hose", ["Smoke test"], "Well done", [], [])));
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(true);
        var vm = new ApprenticeViewModel(gateway, dialogs, new InMemorySettingsStore());

        await vm.OnNavigatedToAsync(null);
        vm.SelectedScenario = vm.Scenarios[0];
        await vm.StartScenarioCommand.ExecuteAsync(null);
        Assert.True(vm.IsInProgress);
        Assert.Equal("P0171", vm.Codes);

        vm.SelectedTest = vm.TestMenu[0];
        await vm.PerformTestCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Score);
        Assert.Single(vm.Turns);

        Assert.False(vm.SubmitDiagnosisCommand.CanExecute(null));
        vm.Diagnosis = "Cracked PCV hose";
        await vm.SubmitDiagnosisCommand.ExecuteAsync(null);

        Assert.True(vm.IsGraded);
        Assert.False(vm.IsInProgress);
        Assert.True(vm.Passed);
        Assert.Equal("Cracked PCV hose", vm.RootCause);
    }
}
