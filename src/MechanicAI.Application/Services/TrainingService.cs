using System.Text.Json;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Abstractions.Ai;
using MechanicAI.Application.Common;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.Training;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace MechanicAI.Application.Services;

public sealed record CourseSummary(Guid Id, string Key, TrainingCategory Category, string Title, string Summary, TrainingLevel Level,
    int Lessons, int LessonsCompleted, int Flashcards, int FlashcardsDue, double? BestQuizPercent);

public sealed record LessonView(TrainingLesson Lesson, TrainingCourse Course, string? DiagramSvg, IReadOnlyList<SafetyWarning> Safety,
    Guid? PreviousLessonId, Guid? NextLessonId);

public sealed record QuizQuestionResult(int Index, bool Correct, int Selected, int CorrectIndex, string? Explanation);

public sealed record QuizResult(Guid AttemptId, int Correct, int Total, double Percent, bool Passed, IReadOnlyList<QuizQuestionResult> Questions);

/// <summary>Courses, lessons, quizzes, flashcards (Leitner spaced repetition), and practice exercises.</summary>
public sealed class TrainingService(IAppDbContextFactory dbFactory, IReferenceContentProvider content, IAiRouter router)
{
    private static readonly TimeSpan[] LeitnerIntervals =
        [TimeSpan.FromMinutes(10), TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7), TimeSpan.FromDays(16), TimeSpan.FromDays(35)];

    public async Task<IReadOnlyList<CourseSummary>> ListCoursesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var now = DateTime.UtcNow;
        var courses = await db.TrainingCourses.AsNoTracking().Include(c => c.Lessons).Include(c => c.Flashcards).AsSplitQuery()
            .OrderBy(c => c.SortOrder).ToListAsync(ct);
        var attempts = await db.TrainingAttempts.AsNoTracking().Where(a => a.Kind == TrainingAttemptKind.Quiz && a.MaxScore > 0).ToListAsync(ct);
        var quizzes = await db.TrainingQuizzes.AsNoTracking().Select(q => new { q.Key, q.CourseId }).ToListAsync(ct);
        return courses.Select(c =>
        {
            var quizKeys = quizzes.Where(q => q.CourseId == c.Id).Select(q => q.Key).ToHashSet();
            var best = attempts.Where(a => quizKeys.Contains(a.ReferenceKey)).Select(a => a.Score / a.MaxScore * 100).DefaultIfEmpty(-1).Max();
            return new CourseSummary(c.Id, c.Key, c.Category, c.Title, c.Summary, c.Level, c.Lessons.Count, c.Lessons.Count(l => l.CompletedUtc is not null),
                c.Flashcards.Count, c.Flashcards.Count(f => f.DueUtc <= now), best < 0 ? null : Math.Round(best, 0));
        }).ToList();
    }

    public async Task<TrainingCourse?> GetCourseAsync(Guid courseId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var course = await db.TrainingCourses.AsNoTracking().Include(c => c.Lessons).Include(c => c.Quizzes).Include(c => c.Flashcards)
            .AsSplitQuery().FirstOrDefaultAsync(c => c.Id == courseId, ct);
        if (course is not null) course.Lessons = course.Lessons.OrderBy(l => l.SortOrder).ToList();
        return course;
    }

    public async Task<LessonView?> GetLessonAsync(Guid lessonId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var lesson = await db.TrainingLessons.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lessonId, ct);
        if (lesson is null) return null;
        var course = await db.TrainingCourses.AsNoTracking().Include(c => c.Lessons).FirstAsync(c => c.Id == lesson.CourseId, ct);
        var ordered = course.Lessons.OrderBy(l => l.SortOrder).ToList();
        var index = ordered.FindIndex(l => l.Id == lessonId);
        var svg = lesson.DiagramKey is null ? null : await content.GetDiagramSvgAsync(lesson.DiagramKey, ct);
        return new LessonView(lesson, course, svg, SafetyAdvisor.ForTags(lesson.SafetyTags),
            index > 0 ? ordered[index - 1].Id : null, index >= 0 && index < ordered.Count - 1 ? ordered[index + 1].Id : null);
    }

    public async Task MarkLessonCompleteAsync(Guid lessonId, bool completed = true, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var lesson = await db.TrainingLessons.FirstOrDefaultAsync(l => l.Id == lessonId, ct);
        if (lesson is null) return;
        lesson.CompletedUtc = completed ? DateTime.UtcNow : null;
        if (completed)
        {
            db.TrainingAttempts.Add(new TrainingAttempt
            {
                Kind = TrainingAttemptKind.Lesson,
                ReferenceKey = lesson.Key,
                Title = lesson.Title,
                CompletedUtc = DateTime.UtcNow,
                Score = 1,
                MaxScore = 1,
                Passed = true,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<Result<QuizResult>> SubmitQuizAsync(Guid quizId, IReadOnlyList<int> answers, string? trainee, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var quiz = await db.TrainingQuizzes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == quizId, ct);
        if (quiz is null) return Error.NotFound("Quiz");
        if (answers.Count != quiz.Questions.Count) return Error.Validation("Answer every question before submitting.");

        var results = quiz.Questions.Select((q, i) => new QuizQuestionResult(i, answers[i] == q.AnswerIndex, answers[i], q.AnswerIndex, q.Explanation)).ToList();
        var correct = results.Count(r => r.Correct);
        var percent = quiz.Questions.Count == 0 ? 0 : correct * 100.0 / quiz.Questions.Count;
        var attempt = new TrainingAttempt
        {
            Kind = TrainingAttemptKind.Quiz,
            ReferenceKey = quiz.Key,
            Title = quiz.Title,
            TraineeName = trainee,
            CompletedUtc = DateTime.UtcNow,
            Score = correct,
            MaxScore = quiz.Questions.Count,
            Passed = percent >= 80,
            DetailsJson = Json.Serialize(new { answers, quiz.IsAiGenerated }),
        };
        db.TrainingAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return new QuizResult(attempt.Id, correct, quiz.Questions.Count, Math.Round(percent, 0), percent >= 80, results);
    }

    public async Task<IReadOnlyList<Flashcard>> GetDueFlashcardsAsync(Guid? courseId, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var now = DateTime.UtcNow;
        var query = db.Flashcards.AsNoTracking().Where(f => f.DueUtc <= now);
        if (courseId is { } c) query = query.Where(f => f.CourseId == c);
        var due = await query.OrderBy(f => f.Box).ThenBy(f => f.DueUtc).Take(take).ToListAsync(ct);
        if (due.Count > 0 || courseId is null) return due;
        // Nothing due: offer the cards with the nearest due date for extra practice.
        return await db.Flashcards.AsNoTracking().Where(f => f.CourseId == courseId).OrderBy(f => f.DueUtc).Take(take).ToListAsync(ct);
    }

    /// <summary>Leitner review: correct moves the card up a box (longer interval); incorrect sends it back to box 1.</summary>
    public async Task ReviewFlashcardAsync(Guid flashcardId, bool correct, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var card = await db.Flashcards.FirstOrDefaultAsync(f => f.Id == flashcardId, ct);
        if (card is null) return;
        card.Box = correct ? Math.Min(5, card.Box + 1) : 1;
        if (correct) card.CorrectCount++;
        else card.IncorrectCount++;
        card.LastReviewedUtc = DateTime.UtcNow;
        card.DueUtc = DateTime.UtcNow + LeitnerIntervals[Math.Clamp(correct ? card.Box : 0, 0, LeitnerIntervals.Length - 1)];
        await db.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<Exercise> GenerateExercises(IEnumerable<string> types, int count = 5, int? seed = null) =>
        ExerciseGenerator.Generate(types, count, seed ?? Environment.TickCount);

    public async Task RecordExerciseSetAsync(string courseKey, int correct, int total, string? trainee, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        db.TrainingAttempts.Add(new TrainingAttempt
        {
            Kind = TrainingAttemptKind.Exercise,
            ReferenceKey = courseKey,
            Title = "Practice exercises",
            TraineeName = trainee,
            CompletedUtc = DateTime.UtcNow,
            Score = correct,
            MaxScore = total,
            Passed = total > 0 && correct * 100 / total >= 80,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TrainingAttempt>> RecentAttemptsAsync(int take = 30, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.TrainingAttempts.AsNoTracking().Where(a => a.CompletedUtc != null).OrderByDescending(a => a.CompletedUtc).Take(take).ToListAsync(ct);
    }

    /// <summary>Generates an extra practice quiz from a lesson with AI. It is stored and shown as AI-generated.</summary>
    public async Task<Result<Guid>> GenerateAiQuizAsync(Guid lessonId, int questions = 5, CancellationToken ct = default)
    {
        var view = await GetLessonAsync(lessonId, ct);
        if (view is null) return Error.NotFound("Lesson");
        var route = await router.ResolveChatAsync(AiTask.Training, DataSensitivity.General, cancellationToken: ct);
        if (!route.IsAvailable) return Error.NotConfigured(route.UnavailableReason!);

        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                questions = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            q = new { type = "string" },
                            choices = new { type = "array", items = new { type = "string" } },
                            answer = new { type = "integer" },
                            explanation = new { type = "string" },
                        },
                        required = new[] { "q", "choices", "answer", "explanation" },
                    },
                },
            },
            required = new[] { "questions" },
        });

        var completion = await route.Model!.CompleteAsync(new ChatRequest
        {
            SystemPrompt = "You write multiple-choice quiz questions for automotive technicians. Use ONLY facts stated in the lesson text provided. " +
                           "Each question has exactly 4 choices and one correct answer (zero-based index). Test reasoning, not trivia. Never include vehicle-specific specifications. Reply with JSON only.",
            Messages = [ChatMessage.User($"Write {Math.Clamp(questions, 3, 10)} questions about this lesson.\n\nTitle: {view.Lesson.Title}\n\n{view.Lesson.BodyMarkdown}")],
            Format = ResponseFormat.Json,
            JsonSchema = schema,
            Temperature = 0.4,
        }, ct);

        var json = Json.ExtractJson(completion.Text);
        var parsed = json is null ? null : Json.Deserialize<GeneratedQuiz>(json);
        var valid = parsed?.Questions.Where(q => !string.IsNullOrWhiteSpace(q.Q) && q.Choices.Count == 4 && q.Answer is >= 0 and < 4).ToList() ?? [];
        if (valid.Count == 0) return Error.Validation("The AI did not return a usable quiz. Try again.");

        await using var db = await dbFactory.CreateAsync(ct);
        var quiz = new TrainingQuiz
        {
            CourseId = view.Course.Id,
            Key = $"{view.Lesson.Key}-ai-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Title = $"AI practice quiz: {view.Lesson.Title}",
            IsAiGenerated = true,
            Questions = valid.Select(q => new QuizQuestion { Question = q.Q, Choices = q.Choices, AnswerIndex = q.Answer, Explanation = q.Explanation }).ToList(),
        };
        db.TrainingQuizzes.Add(quiz);
        await db.SaveChangesAsync(ct);
        return quiz.Id;
    }

    private sealed class GeneratedQuiz
    {
        public List<GeneratedQuestion> Questions { get; set; } = [];
    }

    private sealed class GeneratedQuestion
    {
        public string Q { get; set; } = string.Empty;

        public List<string> Choices { get; set; } = [];

        public int Answer { get; set; }

        public string? Explanation { get; set; }
    }
}
