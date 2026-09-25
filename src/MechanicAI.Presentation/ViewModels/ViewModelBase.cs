using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechanicAI.Application.Common;
using MechanicAI.Presentation.Abstractions;
using MechanicAI.Presentation.Models;

namespace MechanicAI.Presentation.ViewModels;

/// <summary>
/// Common state for page view models: a busy flag, a user-facing notice (mapped from
/// <see cref="Error"/>), and helpers that turn <see cref="Result"/> failures and unexpected
/// exceptions into that notice instead of crashing the page.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject, INavigationAware
{
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    /// <summary>Optional progress/status text shown while busy or after an operation.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial string NoticeTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeMessage { get; set; }

    [ObservableProperty]
    public partial NoticeSeverity NoticeSeverity { get; set; }

    /// <summary>Bound two-way to the InfoBar's IsOpen so the user can dismiss it.</summary>
    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    public bool HasNotice => !string.IsNullOrEmpty(NoticeMessage);

    /// <summary>The last error shown (for tests and diagnostics).</summary>
    public Error? LastError { get; private set; }

    public virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;

    /// <summary>
    /// Called when the page is hidden. View models are singletons, so by default work keeps
    /// running (an AI answer keeps streaming while the technician checks another page).
    /// </summary>
    public virtual void OnNavigatedFrom()
    {
    }

    /// <summary>Shows an error as a notice. Cancellations are silent.</summary>
    public void ShowError(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Kind == ErrorKind.Cancelled)
        {
            StatusMessage = error.Message;
            return;
        }

        LastError = error;
        ShowNotice(ErrorPresentation.Title(error.Kind), error.Message, ErrorPresentation.Severity(error.Kind));
    }

    public void ShowNotice(string title, string message, NoticeSeverity severity)
    {
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    public void ShowSuccess(string message) => ShowNotice("Done", message, NoticeSeverity.Success);

    [RelayCommand]
    public void DismissNotice()
    {
        IsNoticeOpen = false;
        NoticeMessage = null;
        LastError = null;
    }

    /// <summary>Returns true on success; otherwise shows the error and returns false.</summary>
    protected bool Check(Result result)
    {
        if (result.IsSuccess) return true;
        ShowError(result.Error ?? new Error(ErrorKind.Unexpected, "The operation failed."));
        return false;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with <see cref="IsBusy"/> set, converting exceptions into a
    /// notice. A new call cancels the previous operation started through this method.
    /// Returns false when the work threw.
    /// </summary>
    protected async Task<bool> RunAsync(Func<CancellationToken, Task> work, string? busyMessage = null)
    {
        CancelOperation();
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        IsBusy = true;
        if (busyMessage is not null) StatusMessage = busyMessage;
        try
        {
            await work(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ShowError(Error.FromException(ex));
            OnUnexpectedException(ex);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
            {
                _operationCts = null;
                IsBusy = false;
                if (busyMessage is not null && StatusMessage == busyMessage) StatusMessage = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Hook for logging unexpected exceptions (the shell installs a logger-backed handler).</summary>
    protected virtual void OnUnexpectedException(Exception exception) => UnhandledViewModelException?.Invoke(this, exception);

    /// <summary>Raised when an operation throws; the desktop app logs these.</summary>
    public static event EventHandler<Exception>? UnhandledViewModelException;

    [RelayCommand]
    public void CancelOperation()
    {
        var cts = _operationCts;
        _operationCts = null;
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        IsBusy = false;
    }

    protected static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
