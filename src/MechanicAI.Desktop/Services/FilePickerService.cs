using MechanicAI.Presentation.Abstractions;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MechanicAI.Desktop.Services;

/// <summary>File open pickers bound to the main window (required for desktop WinUI apps).</summary>
public sealed class FilePickerService : IFilePicker
{
    public async Task<PickedFile?> PickFileAsync(IReadOnlyList<string> extensions)
    {
        var picker = CreatePicker(extensions);
        var file = await picker.PickSingleFileAsync();
        return file is null ? null : await ToPickedFileAsync(file);
    }

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(IReadOnlyList<string> extensions)
    {
        var picker = CreatePicker(extensions);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return [];
        var result = new List<PickedFile>(files.Count);
        foreach (var file in files) result.Add(await ToPickedFileAsync(file));
        return result;
    }

    private static FileOpenPicker CreatePicker(IReadOnlyList<string> extensions)
    {
        var window = App.MainWindow ?? throw new InvalidOperationException("The main window is not ready.");
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        if (extensions.Count == 0)
        {
            picker.FileTypeFilter.Add("*");
        }
        else
        {
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        }

        // Desktop apps must associate the picker with a window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return picker;
    }

    private static async Task<PickedFile> ToPickedFileAsync(StorageFile file)
    {
        var properties = await file.GetBasicPropertiesAsync();
        var path = file.Path;
        return new PickedFile(file.Name, path, (long)properties.Size, _ =>
            Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)));
    }
}
