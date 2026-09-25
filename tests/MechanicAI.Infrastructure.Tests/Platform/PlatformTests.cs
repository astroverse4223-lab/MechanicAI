using MechanicAI.Application.Settings;
using MechanicAI.Infrastructure.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace MechanicAI.Infrastructure.Tests.Platform;

public class AppPathsTests
{
    [Fact]
    public void RootOverride_PlacesEverythingUnderTheRoot()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("data");

        var paths = new AppPaths(root);

        Assert.Equal(Path.GetFullPath(root), paths.DataRoot);
        Assert.True(Directory.Exists(paths.DataRoot));
        Assert.Equal(Path.Combine(paths.DataRoot, "mechanicai.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.DataRoot, "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(paths.DataRoot, "secrets.dat"), paths.SecretsFile);
        foreach (var dir in new[] { paths.DocumentsDirectory, paths.MediaDirectory, paths.ExportsDirectory, paths.LogsDirectory, paths.CacheDirectory })
        {
            Assert.True(Directory.Exists(dir), dir);
            Assert.Equal(paths.DataRoot, Path.GetDirectoryName(dir));
        }
    }
}

public class JsonSettingsStoreTests
{
    private static JsonSettingsStore Store(AppPaths paths) => new(paths, NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public void Current_DefaultsWhenNoFileExists()
    {
        using var temp = new TempDirectory();

        var store = Store(new AppPaths(temp.Path));

        Assert.False(store.Current.FirstRunCompleted);
        Assert.Equal(0.80, store.Current.Diagnostics.IsolationThreshold);
    }

    [Fact]
    public async Task UpdateAsync_PersistsAcrossInstancesAndRaisesChanged()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = Store(paths);
        var original = store.Current;
        AppSettings? changed = null;
        store.Changed += (_, s) => changed = s;
        var sessionId = Guid.NewGuid();

        await store.UpdateAsync(s =>
        {
            s.FirstRunCompleted = true;
            s.ActiveSessionId = sessionId;
            s.Diagnostics.TechnicianName = "Sam";
            s.Diagnostics.IsolationThreshold = 0.9;
        });

        Assert.NotNull(changed);
        Assert.Same(store.Current, changed);
        Assert.NotSame(original, store.Current); // updates work on a copy
        Assert.False(original.FirstRunCompleted);
        Assert.True(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.SettingsFile + ".tmp"));

        var reloaded = Store(paths).Current;
        Assert.True(reloaded.FirstRunCompleted);
        Assert.Equal(sessionId, reloaded.ActiveSessionId);
        Assert.Equal("Sam", reloaded.Diagnostics.TechnicianName);
        Assert.Equal(0.9, reloaded.Diagnostics.IsolationThreshold);
    }

    [Fact]
    public async Task SaveAsync_ReplacesSettings()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = Store(paths);
        var settings = new AppSettings { SampleDataInstalled = true };
        settings.Security.RequireSignIn = true;

        await store.SaveAsync(settings);

        Assert.Same(settings, store.Current);
        var reloaded = Store(paths).Current;
        Assert.True(reloaded.SampleDataInstalled);
        Assert.True(reloaded.Security.RequireSignIn);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsAndKeepsABackup()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");

        var store = Store(paths);

        Assert.False(store.Current.FirstRunCompleted);
        Assert.Single(Directory.GetFiles(paths.DataRoot, "settings.json.corrupt-*"));
    }

    [Fact]
    public void AppSettingsClone_IsDeep()
    {
        var settings = new AppSettings();
        settings.Diagnostics.TechnicianName = "Alex";

        var clone = settings.Clone();
        clone.Diagnostics.TechnicianName = "Changed";

        Assert.Equal("Alex", settings.Diagnostics.TechnicianName);
        Assert.NotSame(settings.Diagnostics, clone.Diagnostics);
    }
}
