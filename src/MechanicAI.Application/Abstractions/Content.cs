using MechanicAI.Application.Content;

namespace MechanicAI.Application.Abstractions;

/// <summary>
/// Curated reference content shipped with the application (DTC definitions, diagnostic
/// playbooks, training courses, apprentice scenarios, lesson diagrams, sample data).
/// Additional providers (e.g. a licensed OEM data feed) can supply the same shapes.
/// </summary>
public interface IReferenceContentProvider
{
    /// <summary>Hash of all shipped content; used to reseed reference tables when content changes.</summary>
    string ContentVersion { get; }

    Task<IReadOnlyList<DtcContentFile>> LoadDtcFilesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaybookDefinition>> LoadPlaybooksAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CourseContent>> LoadCoursesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ScenarioContent>> LoadScenariosAsync(CancellationToken cancellationToken);

    Task<string?> GetDiagramSvgAsync(string key, CancellationToken cancellationToken);

    Task<SampleDataFile?> LoadSampleDataAsync(CancellationToken cancellationToken);

    /// <summary>Problems found while loading content (invalid regex, unknown category...). Never fatal.</summary>
    IReadOnlyList<string> ValidationWarnings { get; }
}
