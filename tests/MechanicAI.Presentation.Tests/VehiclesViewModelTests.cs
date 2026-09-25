using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.DTOs;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Gateways;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.Navigation;
using MechanicAI.Presentation.ViewModels;

namespace MechanicAI.Presentation.Tests;

public sealed class VehiclesViewModelTests
{
    private const string ValidVin = "1HGCM82633A004352";

    private readonly IVehicleGateway _vehicles = Substitute.For<IVehicleGateway>();
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly InMemorySettingsStore _settings = new();

    public VehiclesViewModelTests()
    {
        _vehicles.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    private VehiclesViewModel Create() => new(_vehicles, _navigation, _dialogs, _settings, Substitute.For<IClipboard>());

    private static VehicleSummary Summary(Guid id, string name = "2003 Honda Accord") =>
        new(id, name, "2.4L", ValidVin, 120000, DistanceUnit.Miles, null, null, 0, 0, false, false, VehicleDataSource.NhtsaVpic);

    [Theory]
    [InlineData("", false)]
    [InlineData("1HGCM8263", false)]
    [InlineData(ValidVin, true)]
    [InlineData("1hgcm8263 3a004352", true)]
    public void Decode_is_enabled_only_for_17_characters(string vin, bool expected)
    {
        var vm = Create();

        vm.VinInput = vin;

        Assert.Equal(expected, vm.DecodeVinCommand.CanExecute(null));
    }

    [Fact]
    public async Task Decode_shows_local_validation_errors_without_calling_the_service()
    {
        var vm = Create();
        vm.VinInput = "1HGCM82633A00435O"; // letter O is never valid

        await vm.DecodeVinCommand.ExecuteAsync(null);

        Assert.Contains("I, O, or Q", vm.VinValidationMessage);
        await _vehicles.DidNotReceiveWithAnyArgs().DecodeVinAsync(default!, default);
    }

    [Fact]
    public async Task Decode_failure_from_the_service_is_shown_as_a_notice()
    {
        _vehicles.DecodeVinAsync(ValidVin, Arg.Any<CancellationToken>()).Returns(Result.Failure<VinLookup>(Error.Offline("VIN decoding")));
        var vm = Create();
        vm.VinInput = ValidVin;

        await vm.DecodeVinCommand.ExecuteAsync(null);

        Assert.True(vm.IsNoticeOpen);
        Assert.Equal(NoticeSeverity.Warning, vm.NoticeSeverity);
        Assert.Equal("Offline", vm.NoticeTitle);
        Assert.False(vm.CanSaveDecoded);
    }

    [Fact]
    public async Task Successful_decode_enables_saving_and_lists_fields()
    {
        var decode = new VinDecodeResult
        {
            Vin = ValidVin, ModelYear = 2003, Make = "HONDA", Model = "Accord", Trim = "EX",
            Fields = [new DecodedField("Engine", "Displacement (L)", "2.4", "DisplacementL")],
        };
        _vehicles.DecodeVinAsync(ValidVin, Arg.Any<CancellationToken>()).Returns(Result.Success(new VinLookup(ValidVin, true, true, 2003, "North America",
            new Sourced<VinDecodeResult>(decode, "NHTSA vPIC", null, DateTime.UtcNow, false), null)));
        var vm = Create();
        vm.VinInput = ValidVin;

        await vm.DecodeVinCommand.ExecuteAsync(null);

        Assert.Equal("2003 HONDA Accord EX", vm.DecodeSummary);
        Assert.True(vm.CanSaveDecoded);
        Assert.True(vm.SaveDecodedVehicleCommand.CanExecute(null));
        Assert.Single(vm.DecodedFields);
    }

    [Fact]
    public void Editor_validation_requires_make_and_model_and_a_sane_year()
    {
        var vm = Create();
        vm.NewVehicleCommand.Execute(null);

        Assert.Equal("Make is required.", vm.ValidateEditor(out _));
        vm.EditMake = "Ford";
        Assert.Equal("Model is required.", vm.ValidateEditor(out _));
        vm.EditModel = "F-150";
        vm.EditYear = "1890";
        Assert.StartsWith("Enter a model year", vm.ValidateEditor(out _));
        vm.EditYear = "2019";
        vm.EditMileage = "lots";
        Assert.Equal("Mileage must be a whole number.", vm.ValidateEditor(out _));
        vm.EditMileage = "84,000";
        vm.EditVin = "ABC";
        Assert.Contains("17 characters", vm.ValidateEditor(out _));
        vm.EditVin = null;

        Assert.Null(vm.ValidateEditor(out var input));
        Assert.Equal("Ford", input!.Make);
        Assert.Equal(2019, input.Year);
        Assert.Equal(84000, input.Mileage);
    }

    [Fact]
    public async Task Save_with_invalid_input_shows_the_editor_error_and_does_not_call_the_service()
    {
        var vm = Create();
        vm.NewVehicleCommand.Execute(null);

        await vm.SaveVehicleCommand.ExecuteAsync(null);

        Assert.Equal("Make is required.", vm.EditorError);
        Assert.True(vm.IsEditorOpen);
        await _vehicles.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task Save_failure_from_the_service_keeps_the_editor_open()
    {
        _vehicles.SaveAsync(Arg.Any<VehicleInput>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<Guid>(Error.Validation("Duplicate VIN.")));
        var vm = Create();
        vm.NewVehicleCommand.Execute(null);
        vm.EditMake = "Ford";
        vm.EditModel = "F-150";

        await vm.SaveVehicleCommand.ExecuteAsync(null);

        Assert.Equal("Duplicate VIN.", vm.EditorError);
        Assert.True(vm.IsEditorOpen);
    }

    [Fact]
    public async Task Save_success_closes_the_editor_and_selects_the_vehicle()
    {
        var id = Guid.NewGuid();
        _vehicles.SaveAsync(Arg.Any<VehicleInput>(), Arg.Any<CancellationToken>()).Returns(Result.Success(id));
        _vehicles.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([Summary(id, "2019 Ford F-150")]);
        _vehicles.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new Vehicle { Id = id, Year = 2019, Make = "Ford", Model = "F-150" });
        var vm = Create();
        vm.NewVehicleCommand.Execute(null);
        vm.EditMake = "Ford";
        vm.EditModel = "F-150";

        await vm.SaveVehicleCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditorOpen);
        Assert.Equal(id, vm.SelectedVehicle?.Id);
        await _vehicles.Received(1).SaveAsync(Arg.Is<VehicleInput>(i => i.Make == "Ford" && i.Model == "F-150" && i.Id == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        var id = Guid.NewGuid();
        _vehicles.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([Summary(id)]);
        var vm = Create();
        await vm.LoadAsync();
        vm.SelectedVehicle = vm.Vehicles[0];
        _dialogs.ConfirmAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(false);

        await vm.DeleteVehicleCommand.ExecuteAsync(null);

        await _vehicles.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    [Fact]
    public async Task Delete_of_the_active_vehicle_clears_it()
    {
        var id = Guid.NewGuid();
        _settings.Current.ActiveVehicleId = id;
        _vehicles.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([Summary(id)]);
        _vehicles.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _dialogs.ConfirmAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(true);
        var vm = Create();
        await vm.LoadAsync();
        vm.SelectedVehicle = vm.Vehicles[0];

        await vm.DeleteVehicleCommand.ExecuteAsync(null);

        await _vehicles.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
        Assert.Null(_settings.Current.ActiveVehicleId);
    }

    [Fact]
    public async Task Start_diagnosis_navigates_with_the_selected_vehicle()
    {
        var id = Guid.NewGuid();
        _vehicles.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns([Summary(id)]);
        var vm = Create();
        await vm.LoadAsync();
        Assert.False(vm.StartDiagnosisCommand.CanExecute(null));
        vm.SelectedVehicle = vm.Vehicles[0];

        vm.StartDiagnosisCommand.Execute(null);

        _navigation.Received(1).NavigateTo(PageKey.Diagnostics, Arg.Is<object?>(new NavigationParameters.NewDiagnosisForVehicle(id)));
    }

    [Fact]
    public async Task Add_vehicle_parameter_prefills_the_editor_from_text()
    {
        var vm = Create();

        await vm.OnNavigatedToAsync(new NavigationParameters.AddVehicle("2017 Chevrolet Silverado 5.3"));

        Assert.True(vm.IsEditorOpen);
        Assert.Equal("2017", vm.EditYear);
        Assert.Equal("Chevrolet", vm.EditMake);
        Assert.Equal("Silverado", vm.EditModel);
    }
}
