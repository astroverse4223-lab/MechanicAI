using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Common;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MechanicAI.Infrastructure.Content;

/// <summary>
/// Seeds reference tables (DTC definitions, training courses, apprentice scenarios) from the
/// content provider. Runs only when the content version changes. User data — technician-defined
/// DTCs, lesson completion, flashcard progress, attempts — is preserved.
/// </summary>
public sealed class ReferenceDataSeeder(
    IAppDbContextFactory dbFactory,
    IReferenceContentProvider content,
    IKeywordIndex? keywordIndex,
    ILogger<ReferenceDataSeeder> logger)
{
    private const string VersionKey = "meta:content-version";

    public async Task SeedAsync(bool force = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var stored = await db.CachedResponses.FirstOrDefaultAsync(c => c.Key == VersionKey, ct);
        var dtcCount = await db.Dtcs.CountAsync(ct);
        if (!force && stored?.Payload == content.ContentVersion && dtcCount > 0)
        {
            return;
        }

        logger.LogInformation("Seeding reference content version {Version}", content.ContentVersion);
        await SeedDtcsAsync(ct);
        await SeedTrainingAsync(ct);
        await SeedScenariosAsync(ct);

        if (stored is null)
        {
            db.CachedResponses.Add(new CachedResponse
            {
                Key = VersionKey,
                Provider = "system",
                Payload = content.ContentVersion,
                ExpiresUtc = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            });
        }
        else
        {
            stored.Payload = content.ContentVersion;
            stored.RetrievedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedDtcsAsync(CancellationToken ct)
    {
        var files = await content.LoadDtcFilesAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var old = await db.Dtcs.Where(d => !d.IsUserDefined).ToListAsync(ct);
        db.Dtcs.RemoveRange(old);
        await db.SaveChangesAsync(ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<DtcDefinition>();
        foreach (var file in files)
        {
            foreach (var entry in file.Codes)
            {
                if (!DtcCode.TryParse(entry.Code, out var code) || !seen.Add(code.Value)) continue;
                var definition = new DtcDefinition
                {
                    Code = code.Value,
                    Manufacturer = null,
                    System = code.System,
                    Subsystem = string.IsNullOrWhiteSpace(entry.Subsystem) ? code.SubsystemDescription : entry.Subsystem,
                    Description = entry.Description.Trim(),
                    IsGeneric = code.IsGeneric,
                    Symptoms = entry.Symptoms ?? [],
                    Causes = entry.Causes ?? [],
                    RelatedCodes = (entry.Related ?? []).Where(r => DtcCode.TryParse(r, out _)).ToList(),
                    SafetyTags = (entry.Safety ?? []).Where(SafetyTags.IsValid).ToList(),
                    Notes = entry.Notes,
                    Source = string.IsNullOrWhiteSpace(file.Source) ? "Built-in generic DTC reference" : file.Source,
                    ContentVersion = content.ContentVersion,
                };
                added.Add(definition);
            }
        }

        foreach (var batch in added.Chunk(500))
        {
            db.Dtcs.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }

        if (keywordIndex is not null)
        {
            var all = await db.Dtcs.AsNoTracking().ToListAsync(ct);
            await keywordIndex.IndexDtcsAsync(all, ct);
        }

        logger.LogInformation("Seeded {Count} DTC definitions", added.Count);
    }

    private async Task SeedTrainingAsync(CancellationToken ct)
    {
        var courses = await content.LoadCoursesAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var existing = await db.TrainingCourses
            .Include(c => c.Lessons)
            .Include(c => c.Quizzes)
            .Include(c => c.Flashcards)
            .AsSplitQuery()
            .ToListAsync(ct);

        var order = 0;
        foreach (var source in courses)
        {
            order++;
            var category = Enum.Parse<TrainingCategory>(source.Category, ignoreCase: true);
            var level = Enum.TryParse<TrainingLevel>(source.Level, true, out var l) ? l : TrainingLevel.Beginner;
            var course = existing.FirstOrDefault(c => c.Key == source.Key);
            if (course is null)
            {
                course = new TrainingCourse { Key = source.Key };
                db.TrainingCourses.Add(course);
            }

            course.Category = category;
            course.Title = source.Title;
            course.Summary = source.Summary;
            course.Level = level;
            course.SortOrder = order;
            course.Exercises = source.Exercises.ToList();
            course.ContentVersion = content.ContentVersion;

            // Lessons: upsert by key, keep completion.
            var lessonOrder = 0;
            foreach (var lessonSource in source.Lessons)
            {
                lessonOrder++;
                var lesson = course.Lessons.FirstOrDefault(x => x.Key == lessonSource.Key);
                if (lesson is null)
                {
                    lesson = new TrainingLesson { CourseId = course.Id, Key = lessonSource.Key };
                    course.Lessons.Add(lesson);
                }

                lesson.Title = lessonSource.Title;
                lesson.BodyMarkdown = lessonSource.Body;
                lesson.DiagramKey = lessonSource.Diagram;
                lesson.EstimatedMinutes = lessonSource.Minutes;
                lesson.SortOrder = lessonOrder;
                lesson.SafetyTags = (lessonSource.Safety ?? []).Where(SafetyTags.IsValid).ToList();
            }

            foreach (var stale in course.Lessons.Where(x => source.Lessons.All(s => s.Key != x.Key)).ToList())
            {
                course.Lessons.Remove(stale);
            }

            // Curated quiz: replace; AI-generated quizzes are left alone.
            var curated = course.Quizzes.FirstOrDefault(q => !q.IsAiGenerated);
            if (source.Quiz is { Questions.Count: > 0 } quiz)
            {
                if (curated is null)
                {
                    curated = new TrainingQuiz { CourseId = course.Id, Key = source.Key + "-quiz" };
                    course.Quizzes.Add(curated);
                }

                curated.Title = quiz.Title;
                curated.Questions = quiz.Questions.Select(q => new QuizQuestion
                {
                    Question = q.Q,
                    Choices = q.Choices.ToList(),
                    AnswerIndex = q.Answer,
                    Explanation = q.Explanation,
                }).ToList();
            }
            else if (curated is not null)
            {
                course.Quizzes.Remove(curated);
            }

            // Flashcards: keep Leitner progress for cards whose front text is unchanged.
            var cards = course.Flashcards.ToDictionary(f => f.Front, StringComparer.Ordinal);
            foreach (var cardSource in source.Flashcards)
            {
                if (cards.TryGetValue(cardSource.Front, out var card))
                {
                    card.Back = cardSource.Back;
                    cards.Remove(cardSource.Front);
                }
                else
                {
                    course.Flashcards.Add(new Flashcard { CourseId = course.Id, Front = cardSource.Front, Back = cardSource.Back });
                }
            }

            foreach (var stale in cards.Values) course.Flashcards.Remove(stale);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} training courses", courses.Count);
    }

    private async Task SeedScenariosAsync(CancellationToken ct)
    {
        var scenarios = await content.LoadScenariosAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var existing = await db.TrainingScenarios.ToListAsync(ct);
        foreach (var source in scenarios)
        {
            var entity = existing.FirstOrDefault(s => s.Key == source.Key);
            if (entity is null)
            {
                entity = new TrainingScenario { Key = source.Key };
                db.TrainingScenarios.Add(entity);
            }

            entity.Title = source.Title;
            entity.Category = Enum.TryParse<TrainingCategory>(source.Category, true, out var c) ? c : TrainingCategory.Diagnostics;
            entity.Difficulty = Enum.TryParse<TrainingLevel>(source.Difficulty, true, out var d) ? d : TrainingLevel.Intermediate;
            entity.VehicleDescription = source.Vehicle.ToString();
            entity.Complaint = source.Complaint;
            entity.Codes = source.Codes.ToList();
            entity.DefinitionJson = Json.Serialize(source);
            entity.ContentVersion = content.ContentVersion;
        }

        foreach (var stale in existing.Where(e => scenarios.All(s => s.Key != e.Key)))
        {
            db.TrainingScenarios.Remove(stale);
        }

        await db.SaveChangesAsync(ct);
    }
}
