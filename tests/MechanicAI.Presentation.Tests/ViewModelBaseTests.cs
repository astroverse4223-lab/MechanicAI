using MechanicAI.Application.Common;
using MechanicAI.Presentation.Models;
using MechanicAI.Presentation.ViewModels;

namespace MechanicAI.Presentation.Tests;

public sealed class ViewModelBaseTests
{
    private sealed class ProbeViewModel : ViewModelBase
    {
        public Task<bool> Run(Func<CancellationToken, Task> work) => RunAsync(work);

        public bool CheckResult(Result result) => Check(result);
    }

    [Theory]
    [InlineData(ErrorKind.Validation, NoticeSeverity.Warning)]
    [InlineData(ErrorKind.NotConfigured, NoticeSeverity.Warning)]
    [InlineData(ErrorKind.Offline, NoticeSeverity.Warning)]
    [InlineData(ErrorKind.Unavailable, NoticeSeverity.Error)]
    [InlineData(ErrorKind.Unexpected, NoticeSeverity.Error)]
    public void ShowError_maps_kind_to_severity_and_opens_notice(ErrorKind kind, NoticeSeverity expected)
    {
        var vm = new ProbeViewModel();

        vm.ShowError(new Error(kind, "Message for the user", "internal detail"));

        Assert.True(vm.IsNoticeOpen);
        Assert.True(vm.HasNotice);
        Assert.Equal("Message for the user", vm.NoticeMessage);
        Assert.Equal(expected, vm.NoticeSeverity);
        Assert.Equal(ErrorPresentation.Title(kind), vm.NoticeTitle);
        Assert.DoesNotContain("internal detail", vm.NoticeMessage);
    }

    [Fact]
    public void Cancelled_errors_do_not_open_a_notice()
    {
        var vm = new ProbeViewModel();

        vm.ShowError(Error.Cancelled());

        Assert.False(vm.IsNoticeOpen);
        Assert.Null(vm.LastError);
    }

    [Fact]
    public void Check_returns_false_and_shows_the_failure()
    {
        var vm = new ProbeViewModel();

        var ok = vm.CheckResult(Result.Failure(Error.NotFound("Vehicle")));

        Assert.False(ok);
        Assert.Equal("Vehicle was not found.", vm.NoticeMessage);
        Assert.Equal(ErrorKind.NotFound, vm.LastError!.Kind);
    }

    [Fact]
    public async Task RunAsync_sets_busy_while_running_and_clears_it_afterwards()
    {
        var vm = new ProbeViewModel();
        var gate = new TaskCompletionSource();
        var running = vm.Run(_ => gate.Task);

        Assert.True(vm.IsBusy);
        Assert.False(vm.IsNotBusy);
        gate.SetResult();
        Assert.True(await running);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RunAsync_turns_exceptions_into_a_safe_error_notice()
    {
        var vm = new ProbeViewModel();

        var ok = await vm.Run(_ => throw new InvalidOperationException("secret stack detail"));

        Assert.False(ok);
        Assert.False(vm.IsBusy);
        Assert.Equal(NoticeSeverity.Error, vm.NoticeSeverity);
        Assert.DoesNotContain("secret stack detail", vm.NoticeMessage);
    }

    [Fact]
    public async Task A_new_operation_cancels_the_previous_one()
    {
        var vm = new ProbeViewModel();
        var firstCancelled = false;
        var first = vm.Run(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                firstCancelled = true;
                throw;
            }
        });

        await vm.Run(_ => Task.CompletedTask);

        Assert.False(await first);
        Assert.True(firstCancelled);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsNoticeOpen);
    }

    [Fact]
    public void DismissNotice_closes_and_clears()
    {
        var vm = new ProbeViewModel();
        vm.ShowError(Error.Validation("Bad input"));

        vm.DismissNoticeCommand.Execute(null);

        Assert.False(vm.IsNoticeOpen);
        Assert.False(vm.HasNotice);
        Assert.Null(vm.LastError);
    }
}
