using MechanicAI.Presentation.Navigation;

namespace MechanicAI.Presentation.Abstractions;

/// <summary>Navigates the main content frame. Implemented by the desktop shell.</summary>
public interface INavigationService
{
    /// <summary>The page currently displayed, or null before the first navigation.</summary>
    PageKey? CurrentPage { get; }

    bool CanGoBack { get; }

    /// <summary>Raised after every successful navigation (including back navigation).</summary>
    event EventHandler<PageKey>? Navigated;

    /// <summary>Navigates to <paramref name="page"/>, passing <paramref name="parameter"/> to its view model.</summary>
    bool NavigateTo(PageKey page, object? parameter = null);

    bool GoBack();
}

/// <summary>Implemented by view models that want to know when their page is shown or hidden.</summary>
public interface INavigationAware
{
    /// <summary>Called after the page is shown. <paramref name="parameter"/> is the navigation parameter, if any.</summary>
    Task OnNavigatedToAsync(object? parameter);

    /// <summary>Called before the page is hidden.</summary>
    void OnNavigatedFrom();
}

/// <summary>Modal dialogs. Implemented with ContentDialog on the desktop.</summary>
public interface IDialogService
{
    /// <summary>Asks a yes/no question. Returns true when the primary (confirm) button was chosen.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool destructive = false);

    /// <summary>Shows a message with a single close button.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string? initialText = null, string confirmText = "OK");
}

/// <summary>Marshals work onto the UI thread.</summary>
public interface IDispatcher
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already there).</summary>
    void Run(Action action);
}

/// <summary>A file chosen by the user. The stream is opened lazily so view models stay testable.</summary>
public sealed record PickedFile(string FileName, string? FullPath, long? SizeBytes, Func<CancellationToken, Task<Stream>> OpenReadAsync);

/// <summary>Operating-system file pickers.</summary>
public interface IFilePicker
{
    /// <summary>Lets the user choose one file. <paramref name="extensions"/> are like ".pdf"; empty means any file.</summary>
    Task<PickedFile?> PickFileAsync(IReadOnlyList<string> extensions);

    /// <summary>Lets the user choose several files.</summary>
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(IReadOnlyList<string> extensions);
}

/// <summary>Clipboard access.</summary>
public interface IClipboard
{
    void SetText(string text);

    Task<string?> GetTextAsync();
}

/// <summary>Opens URLs, files and folders with the default handler.</summary>
public interface ILauncher
{
    Task<bool> OpenUriAsync(Uri uri);

    Task<bool> OpenFileAsync(string path);

    Task<bool> OpenFolderAsync(string path);
}
