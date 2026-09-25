using MechanicAI.Domain.Common;
using MechanicAI.Domain.Enums;

namespace MechanicAI.Domain.Entities;

public class TrainingCourse : Entity
{
    public string Key { get; set; } = string.Empty;

    public TrainingCategory Category { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public TrainingLevel Level { get; set; }

    public int SortOrder { get; set; }

    public List<string> Exercises { get; set; } = [];

    public string ContentVersion { get; set; } = string.Empty;

    public List<TrainingLesson> Lessons { get; set; } = [];

    public List<TrainingQuiz> Quizzes { get; set; } = [];

    public List<Flashcard> Flashcards { get; set; } = [];
}

public class TrainingLesson : Entity
{
    public Guid CourseId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string BodyMarkdown { get; set; } = string.Empty;

    public string? DiagramKey { get; set; }

    public int EstimatedMinutes { get; set; }

    public int SortOrder { get; set; }

    public List<string> SafetyTags { get; set; } = [];

    public DateTime? CompletedUtc { get; set; }
}

public class TrainingQuiz : Entity
{
    public Guid CourseId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<QuizQuestion> Questions { get; set; } = [];

    /// <summary>AI-generated quizzes are labeled and never mixed with curated ones silently.</summary>
    public bool IsAiGenerated { get; set; }
}

public class QuizQuestion
{
    public string Question { get; set; } = string.Empty;

    public List<string> Choices { get; set; } = [];

    public int AnswerIndex { get; set; }

    public string? Explanation { get; set; }
}

public class Flashcard : Entity
{
    public Guid CourseId { get; set; }

    public string Front { get; set; } = string.Empty;

    public string Back { get; set; } = string.Empty;

    /// <summary>Leitner box 1–5; higher boxes are reviewed less often.</summary>
    public int Box { get; set; } = 1;

    public DateTime DueUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastReviewedUtc { get; set; }

    public int CorrectCount { get; set; }

    public int IncorrectCount { get; set; }
}

/// <summary>A completed (or in-progress) quiz, exercise set, flashcard session, or apprentice scenario.</summary>
public class TrainingAttempt : Entity
{
    public TrainingAttemptKind Kind { get; set; }

    /// <summary>Quiz key, scenario key, course key, etc.</summary>
    public string ReferenceKey { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? TraineeName { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    public double Score { get; set; }

    public double MaxScore { get; set; }

    public bool? Passed { get; set; }

    /// <summary>Answers, chosen tests, transcript, and feedback.</summary>
    public string? DetailsJson { get; set; }
}

/// <summary>Apprentice-mode scenario (fictional training case, seeded from content).</summary>
public class TrainingScenario : Entity
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public TrainingCategory Category { get; set; }

    public TrainingLevel Difficulty { get; set; }

    public string VehicleDescription { get; set; } = string.Empty;

    public string Complaint { get; set; } = string.Empty;

    public List<string> Codes { get; set; } = [];

    /// <summary>Full scenario definition (including the hidden root cause) as JSON.</summary>
    public string DefinitionJson { get; set; } = string.Empty;

    public string ContentVersion { get; set; } = string.Empty;
}
