using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Application.Content;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Content;

/// <summary>
/// Loads the curated content embedded in this assembly, plus optional additional content
/// dropped into <c>&lt;data&gt;/content/&lt;Dtc|Playbooks|Training|Scenarios&gt;/*.json</c>
/// (the extension point for licensed or shop-authored data). Content is validated on load;
/// problems are reported as warnings and the offending items are skipped, never crashing the app.
/// </summary>
public sealed class EmbeddedContentProvider : IReferenceContentProvider
{
    private static readonly Assembly ContentAssembly = typeof(EmbeddedContentProvider).Assembly;

    /// <summary>
    /// Resource names keyed by a normalized form ("Content/Dtc/x.json"). MSBuild's
    /// %(RecursiveDir) yields a backslash on Windows, so names are normalized to forward slashes.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Resources = ContentAssembly.GetManifestResourceNames()
        .GroupBy(n => n.Replace('\\', '/'), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static Stream? OpenResource(string normalizedName) =>
        Resources.TryGetValue(normalizedName, out var actual) ? ContentAssembly.GetManifestResourceStream(actual) : null;

    private readonly ILogger<EmbeddedContentProvider> _logger;
    private readonly string? _externalRoot;
    private readonly List<string> _warnings = [];
    private readonly Lazy<string> _version;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<PlaybookDefinition>? _playbooks;

    public EmbeddedContentProvider(ILogger<EmbeddedContentProvider> logger, IAppPaths? paths = null)
    {
        _logger = logger;
        _externalRoot = paths is null ? null : Path.Combine(paths.DataRoot, "content");
        _version = new Lazy<string>(ComputeVersion);
    }

    public string ContentVersion => _version.Value;

    public IReadOnlyList<string> ValidationWarnings
    {
        get
        {
            lock (_warnings) return _warnings.ToList();
        }
    }

    public async Task<IReadOnlyList<DtcContentFile>> LoadDtcFilesAsync(CancellationToken cancellationToken)
    {
        var files = await LoadAllAsync<DtcContentFile>("Dtc", cancellationToken);
        foreach (var (name, file) in files)
        {
            var invalid = file.Codes.Where(c => !DtcCode.TryParse(c.Code, out _) || string.IsNullOrWhiteSpace(c.Description)).ToList();
            foreach (var bad in invalid) Warn($"{name}: invalid DTC entry '{bad.Code}' skipped");
            file.Codes.RemoveAll(c => invalid.Contains(c));
        }

        return files.Select(f => f.Content).ToList();
    }

    public async Task<IReadOnlyList<PlaybookDefinition>> LoadPlaybooksAsync(CancellationToken cancellationToken)
    {
        if (_playbooks is not null) return _playbooks;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_playbooks is not null) return _playbooks;
            var files = await LoadAllAsync<PlaybookFile>("Playbooks", cancellationToken);
            var list = new List<PlaybookDefinition>();
            foreach (var (name, file) in files)
            {
                foreach (var playbook in file.Playbooks)
                {
                    playbook.SourceFile = name;
                    if (ValidatePlaybook(playbook, name)) list.Add(playbook);
                }
            }

            _logger.LogInformation("Loaded {Count} diagnostic playbooks", list.Count);
            _playbooks = list;
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CourseContent>> LoadCoursesAsync(CancellationToken cancellationToken)
    {
        var files = await LoadAllAsync<CourseFile>("Training", cancellationToken);
        var courses = new List<CourseContent>();
        foreach (var (name, file) in files)
        {
            foreach (var course in file.Courses)
            {
                if (!Enum.TryParse<TrainingCategory>(course.Category, true, out _))
                {
                    Warn($"{name}: course '{course.Key}' has unknown category '{course.Category}'");
                    continue;
                }

                if (course.Quiz is not null)
                {
                    course.Quiz.Questions.RemoveAll(q => q.Choices.Count < 2 || q.Answer < 0 || q.Answer >= q.Choices.Count);
                }

                courses.Add(course);
            }
        }

        return courses;
    }

    public async Task<IReadOnlyList<ScenarioContent>> LoadScenariosAsync(CancellationToken cancellationToken)
    {
        var files = await LoadAllAsync<ScenarioFile>("Scenarios", cancellationToken);
        var scenarios = new List<ScenarioContent>();
        foreach (var (name, file) in files)
        {
            foreach (var scenario in file.Scenarios)
            {
                if (string.IsNullOrWhiteSpace(scenario.Key) || scenario.Tests.Count == 0)
                {
                    Warn($"{name}: scenario '{scenario.Key}' is incomplete and was skipped");
                    continue;
                }

                scenarios.Add(scenario);
            }
        }

        return scenarios;
    }

    public async Task<string?> GetDiagramSvgAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        await using var stream = OpenResource($"Content/Diagrams/{key}.svg");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<SampleDataFile?> LoadSampleDataAsync(CancellationToken cancellationToken)
    {
        var files = await LoadAllAsync<SampleDataFile>("Samples", cancellationToken);
        return files.Select(f => f.Content).FirstOrDefault();
    }

    private bool ValidatePlaybook(PlaybookDefinition playbook, string file)
    {
        if (string.IsNullOrWhiteSpace(playbook.Key) || playbook.Causes.Count == 0)
        {
            Warn($"{file}: playbook '{playbook.Key}' has no causes and was skipped");
            return false;
        }

        foreach (var pattern in playbook.Triggers.DtcPatterns.Concat(playbook.Triggers.SymptomKeywords).ToList())
        {
            if (!IsValidRegex(pattern))
            {
                Warn($"{file}/{playbook.Key}: invalid trigger pattern '{pattern}' removed");
                playbook.Triggers.DtcPatterns.Remove(pattern);
                playbook.Triggers.SymptomKeywords.Remove(pattern);
            }
        }

        var causeKeys = playbook.Causes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var cause in playbook.Causes)
        {
            if (!Enum.TryParse<DiagnosticCategory>(cause.Category, true, out _))
            {
                Warn($"{file}/{playbook.Key}: cause '{cause.Key}' has unknown category '{cause.Category}' (using Other)");
                cause.Category = nameof(DiagnosticCategory.Other);
            }

            cause.Modifiers.RemoveAll(m => m.Match is not null && !IsValidRegex(m.Match));
            cause.Safety.RemoveAll(s => !SafetyTags.IsValid(s));
        }

        foreach (var test in playbook.Tests.ToList())
        {
            var decisive = test.Outcomes.Count(o => !o.Inconclusive);
            if (decisive < 2)
            {
                Warn($"{file}/{playbook.Key}: test '{test.Key}' needs at least two decisive outcomes; skipped");
                playbook.Tests.Remove(test);
                continue;
            }

            test.Safety.RemoveAll(s => !SafetyTags.IsValid(s));
            foreach (var outcome in test.Outcomes)
            {
                if (outcome.Likelihoods is null) continue;
                foreach (var key in outcome.Likelihoods.Keys.ToList())
                {
                    var value = outcome.Likelihoods[key];
                    if (key != DiagnosticMath.DefaultLikelihoodKey && !causeKeys.Contains(key))
                    {
                        // Keys for causes defined in other playbooks are legal (merged at runtime);
                        // they simply have no effect unless that playbook is also active.
                    }

                    if (double.IsNaN(value) || value <= 0 || value > 1)
                    {
                        outcome.Likelihoods[key] = Math.Clamp(double.IsNaN(value) ? 0.5 : value, 0.01, 1.0);
                        Warn($"{file}/{playbook.Key}/{test.Key}: likelihood for '{key}' out of range; clamped");
                    }
                }
            }
        }

        return true;
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<List<(string Name, T Content)>> LoadAllAsync<T>(string folder, CancellationToken ct)
        where T : class
    {
        var results = new List<(string, T)>();
        var prefix = $"Content/{folder}/";
        foreach (var resource in Resources.Keys
                     .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            await using var stream = OpenResource(resource);
            if (stream is null) continue;
            var parsed = await TryParseAsync<T>(stream, resource, ct);
            if (parsed is not null) results.Add((resource, parsed));
        }

        if (_externalRoot is not null)
        {
            var dir = Path.Combine(_externalRoot, folder);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                {
                    await using var stream = File.OpenRead(file);
                    var parsed = await TryParseAsync<T>(stream, file, ct);
                    if (parsed is not null) results.Add((file, parsed));
                }
            }
        }

        return results;
    }

    private async Task<T?> TryParseAsync<T>(Stream stream, string name, CancellationToken ct)
        where T : class
    {
        try
        {
            return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(stream, Json.Lenient, ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            Warn($"{name}: invalid JSON ({ex.Message})");
            return null;
        }
    }

    private void Warn(string message)
    {
        _logger.LogWarning("Content validation: {Message}", message);
        lock (_warnings) _warnings.Add(message);
    }

    private static string ComputeVersion()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in Resources.Keys.Where(n => n.StartsWith("Content/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = OpenResource(name);
            if (stream is null) continue;
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            hash.AppendData(ms.ToArray());
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }
}
