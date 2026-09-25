using MechanicAI.Application.Abstractions;

namespace MechanicAI.Infrastructure.Platform;

/// <summary>
/// Local data layout under %LOCALAPPDATA%\MechanicAI (override with MECHANICAI_DATA_DIR for
/// portable installs and tests). Directories are created on first access.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        var root = rootOverride
                   ?? Environment.GetEnvironmentVariable("MECHANICAI_DATA_DIR")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MechanicAI");
        DataRoot = Directory.CreateDirectory(root).FullName;
    }

    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "mechanicai.db");

    public string DocumentsDirectory => Ensure("documents");

    public string MediaDirectory => Ensure("media");

    public string ExportsDirectory => Ensure("exports");

    public string LogsDirectory => Ensure("logs");

    public string CacheDirectory => Ensure("cache");

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string SecretsFile => Path.Combine(DataRoot, "secrets.dat");

    private string Ensure(string name) => Directory.CreateDirectory(Path.Combine(DataRoot, name)).FullName;
}
