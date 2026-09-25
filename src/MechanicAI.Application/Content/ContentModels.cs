namespace MechanicAI.Application.Content;

// These classes mirror docs/CONTENT-SCHEMAS.md. Property names bind case-insensitively
// to the camelCase JSON used in Content/*.json.

public sealed class DtcContentFile
{
    public string Version { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public List<DtcContentEntry> Codes { get; set; } = [];
}

public sealed class DtcContentEntry
{
    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Subsystem { get; set; } = string.Empty;

    public List<string>? Symptoms { get; set; }

    public List<string>? Causes { get; set; }

    public string? Notes { get; set; }

    public List<string>? Related { get; set; }

    public List<string>? Safety { get; set; }
}

public sealed class PlaybookFile
{
    public string Version { get; set; } = string.Empty;

    public List<PlaybookDefinition> Playbooks { get; set; } = [];
}

public sealed class PlaybookDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public PlaybookTriggers Triggers { get; set; } = new();

    public List<string> ClarifyingQuestions { get; set; } = [];

    public List<CauseDefinition> Causes { get; set; } = [];

    public List<TestDefinition> Tests { get; set; } = [];

    public List<string> Verification { get; set; } = [];

    /// <summary>Content file this playbook was loaded from (set by the loader).</summary>
    public string? SourceFile { get; set; }
}

public sealed class PlaybookTriggers
{
    public List<string> DtcPatterns { get; set; } = [];

    public List<string> SymptomKeywords { get; set; } = [];
}

public sealed class CauseDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = "Other";

    public double Prior { get; set; } = 0.1;

    public string? Description { get; set; }

    public List<CauseModifier> Modifiers { get; set; } = [];

    public List<string> Safety { get; set; } = [];
}

public sealed class CauseModifier
{
    /// <summary>"dtc", "symptom", or "mileage-over".</summary>
    public string When { get; set; } = string.Empty;

    public string? Match { get; set; }

    public double? Value { get; set; }

    public double Factor { get; set; } = 1.0;

    public string? Reason { get; set; }
}

public sealed class TestDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Purpose { get; set; }

    public List<string> Procedure { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public string? Expected { get; set; }

    public string? SpecNote { get; set; }

    public int Minutes { get; set; } = 15;

    public int Difficulty { get; set; } = 1;

    public int Invasiveness { get; set; } = 1;

    public List<string> Safety { get; set; } = [];

    public List<string> RequiresAnyCause { get; set; } = [];

    public List<OutcomeDefinition> Outcomes { get; set; } = [];
}

public sealed class OutcomeDefinition
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool? Normal { get; set; }

    public bool Inconclusive { get; set; }

    public Dictionary<string, double>? Likelihoods { get; set; }

    public string? Interpretation { get; set; }
}

public sealed class CourseFile
{
    public string Version { get; set; } = string.Empty;

    public List<CourseContent> Courses { get; set; } = [];
}

public sealed class CourseContent
{
    public string Key { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Level { get; set; } = "Beginner";

    public List<LessonContent> Lessons { get; set; } = [];

    public QuizContent? Quiz { get; set; }

    public List<FlashcardContent> Flashcards { get; set; } = [];

    public List<string> Exercises { get; set; } = [];
}

public sealed class LessonContent
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int Minutes { get; set; }

    public string? Diagram { get; set; }

    public List<string>? Safety { get; set; }

    public string Body { get; set; } = string.Empty;
}

public sealed class QuizContent
{
    public string Title { get; set; } = string.Empty;

    public List<QuizQuestionContent> Questions { get; set; } = [];
}

public sealed class QuizQuestionContent
{
    public string Q { get; set; } = string.Empty;

    public List<string> Choices { get; set; } = [];

    public int Answer { get; set; }

    public string? Explanation { get; set; }
}

public sealed class FlashcardContent
{
    public string Front { get; set; } = string.Empty;

    public string Back { get; set; } = string.Empty;
}

public sealed class ScenarioFile
{
    public string Version { get; set; } = string.Empty;

    public List<ScenarioContent> Scenarios { get; set; } = [];
}

public sealed class ScenarioContent
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Difficulty { get; set; } = "Beginner";

    public ScenarioVehicle Vehicle { get; set; } = new();

    public int? Mileage { get; set; }

    public string Complaint { get; set; } = string.Empty;

    public string? CustomerStatement { get; set; }

    public List<string> Codes { get; set; } = [];

    public Dictionary<string, string>? FreezeFrame { get; set; }

    public string RootCause { get; set; } = string.Empty;

    public List<string> RootCauseKeywords { get; set; } = [];

    public List<ScenarioTest> Tests { get; set; } = [];

    public List<string> IdealPath { get; set; } = [];

    public string Debrief { get; set; } = string.Empty;

    public List<string> CommonMistakes { get; set; } = [];
}

public sealed class ScenarioVehicle
{
    public int Year { get; set; }

    public string Make { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Engine { get; set; }

    public override string ToString() => $"{Year} {Make} {Model} {Engine}".Trim();
}

public sealed class ScenarioTest
{
    public string Key { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<string> Keywords { get; set; } = [];

    public string Result { get; set; } = string.Empty;

    /// <summary>high, medium, low, or parts-cannon.</summary>
    public string Value { get; set; } = "medium";

    public int Points { get; set; }

    public string? Teaches { get; set; }
}

/// <summary>Development sample data (clearly labeled in the UI).</summary>
public sealed class SampleDataFile
{
    public string Version { get; set; } = string.Empty;

    public string Notice { get; set; } = string.Empty;

    public List<SampleCustomer> Customers { get; set; } = [];

    public List<SampleVehicle> Vehicles { get; set; } = [];

    public List<SampleSession> Sessions { get; set; } = [];
}

public sealed class SampleCustomer
{
    public string Key { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? Phone { get; set; }

    public string? Email { get; set; }
}

public sealed class SampleVehicle
{
    public string Key { get; set; } = string.Empty;

    public string? CustomerKey { get; set; }

    public int Year { get; set; }

    public string Make { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Trim { get; set; }

    public string? Engine { get; set; }

    public decimal? DisplacementLiters { get; set; }

    public int? Cylinders { get; set; }

    public string? FuelType { get; set; }

    public string? Drivetrain { get; set; }

    public string? Transmission { get; set; }

    public int? Mileage { get; set; }

    public string? Notes { get; set; }
}

public sealed class SampleSession
{
    public string VehicleKey { get; set; } = string.Empty;

    public string Complaint { get; set; } = string.Empty;

    public List<string> Codes { get; set; } = [];

    public List<string> Symptoms { get; set; } = [];

    public int? Mileage { get; set; }
}
