using MechanicAI.Presentation.Abstractions;
using Windows.ApplicationModel.DataTransfer;

namespace MechanicAI.Desktop.Services;

public sealed class ClipboardService : IClipboard
{
    public void SetText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public async Task<string?> GetTextAsync()
    {
        var content = Clipboard.GetContent();
        return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
    }
}
