using System.Diagnostics;
using MechanicAI.Presentation.Abstractions;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Desktop.Services;

/// <summary>Opens URLs, files and folders with the user's default handlers.</summary>
public sealed class LauncherService(ILogger<LauncherService> logger) : ILauncher
{
    public async Task<bool> OpenUriAsync(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeMailto) return false;
        return await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    public Task<bool> OpenFileAsync(string path) => Task.FromResult(File.Exists(path) && Start(path));

    public Task<bool> OpenFolderAsync(string path) =>
        Task.FromResult(Directory.Exists(path) && Start("explorer.exe", $"\"{path}\""));

    private bool Start(string fileName, string? arguments = null)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = true };
            if (arguments is not null) info.Arguments = arguments;
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Target}", fileName);
            return false;
        }
    }
}
