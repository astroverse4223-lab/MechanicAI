using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MechanicAI.Application.Abstractions;
using MechanicAI.Application.Diagnostics;
using MechanicAI.Application.DTOs;
using MechanicAI.Application.Services;
using MechanicAI.Domain.Entities;
using MechanicAI.Domain.Enums;
using MechanicAI.Domain.ValueObjects;

namespace MechanicAI.Presentation.Models;

// Display models bound by the desktop pages. They carry preformatted text so XAML stays
// free of converters, and override ToString() so they display sensibly in plain ComboBoxes.

public sealed record VehicleItem(Guid Id, string DisplayName, string Subtitle, string? Vin, bool IsFavorite, bool IsSample, int OpenRecalls, int SessionCount)
{
    public bool HasOpenRecalls => OpenRecalls > 0;

    public string RecallText => OpenRecalls == 1 ? "1 open recall" : $"{OpenRecalls} open recalls";

    public string FavoriteGlyph => IsFavorite ? "" : "";

    public static VehicleItem From(VehicleSummary v)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(v.Engine)) parts.Add(v.Engine);
        if (v.Mileage is { } m) parts.Add(v.MileageUnit == DistanceUnit.Kilometers ? $"{m:N0} km" : $"{m:N0} mi");
        if (!string.IsNullOrWhiteSpace(v.CustomerName)) parts.Add(v.CustomerName);
        if (v.IsSample) parts.Add("Sample");
        return new VehicleItem(v.Id, v.DisplayName, string.Join(" · ", parts), v.Vin, v.IsFavorite, v.IsSample, v.OpenRecalls, v.SessionCount);
    }

    public override string ToString() => DisplayName;
}

public sealed record LabeledValue(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

public sealed record RecallItem(Guid Id, string Campaign, string Component, string Summary, string Remedy, string StatusText, bool ParkIt)
{
    public static RecallItem From(Recall r) => new(
        r.Id,
        r.CampaignNumber,
        r.Component ?? "—",
        r.Summary ?? string.Empty,
        r.Remedy ?? string.Empty,
        r.Status.ToString(),
        r.ParkIt);
}

public sealed record SessionItem(
    Guid Id,
    string Title,
    string Vehicle,
    string StatusText,
    string Codes,
    string LeadingCause,
    string Updated,
    bool IsClosed,
    bool IsSample)
{
    public static SessionItem From(DiagnosticSessionSummary s, DateTime nowUtc)
    {
        var leading = s.LeadingCause is null
            ? "No leading cause yet"
            : s.LeadingProbability is { } p ? $"{s.LeadingCause} ({Format.Percent(p)})" : s.LeadingCause;
        return new SessionItem(
            s.Id,
            s.Title,
            string.IsNullOrWhiteSpace(s.VehicleDescription) ? "No vehicle" : s.VehicleDescription,
            DiagnosticStages.Label(s.Status),
            Format.Join(s.Dtcs, "No codes"),
            leading,
            Format.Relative(s.UpdatedUtc, nowUtc),
            s.Status is DiagnosticSessionStatus.Completed or DiagnosticSessionStatus.Abandoned,
            s.IsSample);
    }

    public override string ToString() => Title;
}

public sealed record CauseItem(
    Guid Id,
    string Key,
    string Title,
    string? Description,
    string Category,
    double Probability,
    string ProbabilityText,
    CauseStatus Status,
    string StatusText,
    string EvidenceText,
    string OriginText,
    bool IsLeading)
{
    /// <summary>0–100 for ProgressBar.Value.</summary>
    public double ProbabilityPercent => Math.Round(Probability * 100, 1);

    public bool IsRuledOut => Status == CauseStatus.RuledOut;

    public bool IsConfirmed => Status == CauseStatus.Confirmed;

    public static CauseItem From(DiagnosticNode n, bool leading) => new(
        n.Id,
        n.Key,
        n.Title,
        n.Description,
        n.Category.ToString(),
        n.Probability,
        Format.Percent(n.Probability),
        n.Status,
        n.Status switch
        {
            CauseStatus.RuledOut => "Ruled out",
            CauseStatus.Likely => "Likely — confirm",
            CauseStatus.Confirmed => "Confirmed",
            CauseStatus.Suspected => "Suspected",
            _ => "Open",
        },
        Format.Evidence(n.Evidence),
        Format.Actor(n.Origin),
        leading);
}

public sealed record OutcomeOption(string Key, string Label, string ResultText, string? Interpretation)
{
    public static OutcomeOption From(TestOutcomeDefinition o) => new(
        o.Key,
        o.Label,
        o.Inconclusive ? "Inconclusive" : o.Normal == false ? "Abnormal (FAIL)" : o.Normal == true ? "Normal (PASS)" : "Result",
        o.Interpretation);

    public override string ToString() => $"{Label} — {ResultText}";
}

public sealed record TestItem(
    Guid Id,
    string Title,
    string Purpose,
    IReadOnlyList<string> Procedure,
    string Tools,
    string Expected,
    string? SpecificationNote,
    string Effort,
    string InformationGainText,
    string OriginText,
    TestStatus Status,
    string ResultText,
    string? ActualResult,
    IReadOnlyList<OutcomeOption> Outcomes,
    IReadOnlyList<string> SafetyTags)
{
    public bool IsCompleted => Status == TestStatus.Completed;

    public bool IsSkipped => Status == TestStatus.Skipped;

    public bool HasResult => Status is TestStatus.Completed or TestStatus.Skipped;

    public bool HasSpecificationNote => !string.IsNullOrWhiteSpace(SpecificationNote);

    public string ProcedureText => string.Join(Environment.NewLine, Procedure.Select((p, i) => $"{i + 1}. {p}"));

    public static TestItem From(DiagnosticTest t, double? informationGain = null)
    {
        var result = t.Status switch
        {
            TestStatus.Completed => t.Result switch
            {
                TestResult.Pass => "PASS",
                TestResult.Fail => "FAIL",
                _ => "INCONCLUSIVE",
            } + (t.SelectedOutcome is { } o ? $" — {o.Label}" : string.Empty),
            TestStatus.Skipped => "Skipped",
            _ => "Not performed",
        };
        var gain = informationGain ?? t.InformationGain;
        return new TestItem(
            t.Id,
            t.Title,
            t.Purpose ?? string.Empty,
            t.Procedure,
            Format.Join(t.Tools, "No special tools"),
            t.ExpectedResult ?? "See procedure",
            t.SpecificationNote,
            string.Create(CultureInfo.CurrentCulture, $"~{t.EstimatedMinutes} min · difficulty {t.Difficulty}/5 · invasiveness {t.Invasiveness}/5"),
            gain is { } g ? string.Create(CultureInfo.CurrentCulture, $"{g:0.00} bits expected information") : string.Empty,
            Format.Actor(t.Origin),
            t.Status,
            result,
            t.ActualResult,
            t.Outcomes.Select(OutcomeOption.From).ToList(),
            t.SafetyTags);
    }

    public override string ToString() => Title;
}

public sealed record StepItem(string When, string Title, string? Detail, string Actor, string? Evidence, bool IsReverted)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public static StepItem From(DiagnosticStep s) => new(
        Format.Local(s.TimestampUtc),
        s.Title,
        s.Detail,
        s.ActorName ?? Format.Actor(s.Actor),
        s.Evidence is { } e ? Format.Evidence(e) : null,
        s.IsReverted);
}

public sealed record WarningItem(string Title, string Message, bool IsCritical)
{
    public static WarningItem From(SafetyWarning w) => new(w.Title, w.Message, w.Severity >= SafetySeverity.Danger);
}

public sealed partial class CheckItem(string text, bool isDone) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    public partial bool IsDone { get; set; } = isDone;
}

public sealed partial class QuestionItem(int index, string question, string? answer) : ObservableObject
{
    public int Index { get; } = index;

    public string Question { get; } = question;

    public bool IsAnswered => !string.IsNullOrWhiteSpace(SavedAnswer);

    public string? SavedAnswer { get; } = answer;

    [ObservableProperty]
    public partial string? Answer { get; set; } = answer;
}

public sealed record CitationItem(string Label, string Title, string? Url, string Detail)
{
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    public static CitationItem From(SourceCitation c)
    {
        var detail = new List<string> { c.Type.ToString() };
        if (c.PageNumber is { } p) detail.Add($"page {p}");
        if (!string.IsNullOrWhiteSpace(c.Publisher)) detail.Add(c.Publisher);
        if (c.FromCache) detail.Add("cached");
        return new CitationItem(c.Label, c.Title, c.Url, string.Join(" · ", detail));
    }

    public override string ToString() => $"[{Label}] {Title}";
}

public sealed record DtcResultItem(string Code, string Description, string Subsystem, string Scope, bool HasPlaybook, string MatchReason)
{
    public static DtcResultItem From(DtcSearchResult r) => new(
        r.Code,
        r.Description,
        r.Subsystem,
        r.IsGeneric ? "Generic (SAE)" : $"Manufacturer-specific{(r.Manufacturer is null ? string.Empty : " — " + r.Manufacturer)}",
        r.HasPlaybook,
        r.MatchReason);

    public override string ToString() => $"{Code} — {Description}";
}

public sealed record DefinitionItem(string Scope, string Description, string Source, string? Notes)
{
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
}

public sealed record PlaybookItem(string Title, string Summary, IReadOnlyList<string> Causes, IReadOnlyList<string> Tests, IReadOnlyList<string> Verification)
{
    public static PlaybookItem From(PlaybookSummary p) => new(
        p.Title,
        p.Summary,
        p.Causes.Select(c => $"{c.Title} ({c.Category}, relative likelihood {c.RelativeLikelihood:0.##})").ToList(),
        p.Tests.Select(t => $"{t.Title} (~{t.Minutes} min)").ToList(),
        p.Verification);
}

public sealed record AdapterItem(ObdAdapterInfo Adapter)
{
    public string DisplayName => Adapter.IsSimulator ? $"{Adapter.DisplayName} (SIMULATOR)" : Adapter.DisplayName;

    public override string ToString() => DisplayName;
}

public sealed partial class PidValueItem(string key, string name, string unit, double min, double max) : ObservableObject
{
    public string Key { get; } = key;

    public string Name { get; } = name;

    public string Unit { get; } = unit;

    public double Min { get; } = min;

    public double Max { get; } = max;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsSupported { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    public partial double? Value { get; set; }

    public string ValueText => Value is { } v ? string.Create(CultureInfo.CurrentCulture, $"{v:0.##} {Unit}") : "—";

    /// <summary>The value clamped to [Min, Max] for gauges/progress bars.</summary>
    public double GaugeValue => Value is { } v ? Math.Clamp(v, Min, Max) : Min;

    partial void OnValueChanged(double? value) => OnPropertyChanged(nameof(GaugeValue));
}

public sealed record RecordingItem(Guid Id, string Title, string Started, string Details, bool IsSimulated)
{
    public static RecordingItem From(LiveDataSession s)
    {
        var duration = s.EndedUtc is { } end ? end - s.StartedUtc : TimeSpan.Zero;
        var details = string.Create(CultureInfo.CurrentCulture, $"{s.SampleCount:N0} samples · {duration:mm\\:ss} · {s.AdapterDescription}");
        return new RecordingItem(s.Id, string.IsNullOrWhiteSpace(s.Title) ? "Recording" : s.Title, Format.Local(s.StartedUtc),
            s.IsSimulated ? details + " · SIMULATED" : details, s.IsSimulated);
    }

    public override string ToString() => $"{Title} ({Started})";
}

public sealed record PidStatItem(string Name, string Unit, string Min, string Max, string Mean, string StdDev, int Samples)
{
    public static PidStatItem From(PidStatistics p) => new(
        p.Name,
        p.Unit,
        p.Min.ToString("0.##", CultureInfo.CurrentCulture),
        p.Max.ToString("0.##", CultureInfo.CurrentCulture),
        p.Mean.ToString("0.##", CultureInfo.CurrentCulture),
        p.StdDev.ToString("0.##", CultureInfo.CurrentCulture),
        p.Samples);
}

public sealed record DocumentItem(
    Guid Id,
    string Title,
    string FileName,
    string KindText,
    DocumentKind Kind,
    string StatusText,
    string? StatusMessage,
    string Details,
    bool IsReady,
    int PageCount)
{
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public static DocumentItem From(Document d) => new(
        d.Id,
        d.Title,
        d.FileName,
        d.Kind.ToString(),
        d.Kind,
        d.Status switch
        {
            DocumentStatus.Ready => "Ready",
            DocumentStatus.ReadyKeywordOnly => "Ready (keyword search only)",
            DocumentStatus.NeedsOcr => "Needs OCR",
            DocumentStatus.Processing => "Indexing…",
            DocumentStatus.Pending => "Queued",
            _ => "Failed",
        },
        d.StatusMessage,
        string.Create(CultureInfo.CurrentCulture, $"{d.PageCount} pages · {Format.Size(d.SizeBytes)}{(d.UsedOcr ? " · OCR" : string.Empty)}"),
        d.Status is DocumentStatus.Ready or DocumentStatus.ReadyKeywordOnly,
        d.PageCount);

    public override string ToString() => Title;
}

public sealed record PassageItem(Guid DocumentId, string Document, int PageNumber, string? Heading, string Text, string MatchType)
{
    public string Location => $"{Document} — page {PageNumber}";

    public static PassageItem From(KnowledgeHit h) => new(h.DocumentId, h.DocumentTitle, h.PageNumber, h.Heading, h.Text, h.MatchType);
}

public sealed record WiringLabelItem(string Text, string KindText, int PageNumber)
{
    public string PageText => PageNumber.ToString(CultureInfo.CurrentCulture);

    public static WiringLabelItem From(WiringLabel l) => new(l.Text, l.Kind.ToString(), l.PageNumber);
}

public sealed partial class SourceItem(ResearchSource source) : ObservableObject
{
    public ResearchSource Source { get; } = source;

    public string Title => Source.Title;

    public string Url => Source.Url;

    public string Domain => Source.Domain;

    public string Meta
    {
        get
        {
            var parts = new List<string> { Source.TypeLabel };
            if (!string.IsNullOrWhiteSpace(Source.AgeText)) parts.Add(Source.AgeText);
            if (Source.FromCache) parts.Add("cached");
            return string.Join(" · ", parts);
        }
    }

    public string Snippet => Source.Snippet ?? string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}

public sealed partial class ChatMessageItem : ObservableObject
{
    public ChatMessageItem(bool isUser, string text)
    {
        IsUser = isUser;
        Text = text;
    }

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    public string Author => IsUser ? "You" : "Assistant";

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    public partial string? Activity { get; set; }

    public bool HasActivity => !string.IsNullOrWhiteSpace(Activity);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    public partial string? Footer { get; set; }

    public bool HasFooter => !string.IsNullOrWhiteSpace(Footer);
}

public sealed record ConversationItem(Guid Id, string Title, string Updated, bool IsPinned)
{
    public static ConversationItem From(ConversationSummary c, DateTime nowUtc) =>
        new(c.Id, c.Title, $"{Format.Relative(c.UpdatedUtc, nowUtc)} · {c.MessageCount} messages", c.IsPinned);

    public override string ToString() => Title;
}

public sealed record CourseItem(Guid Id, string Key, string Title, string Summary, string Meta, double ProgressPercent)
{
    public static CourseItem From(CourseSummary c) => new(
        c.Id,
        c.Key,
        c.Title,
        c.Summary,
        $"{c.Category} · {c.Level} · {c.LessonsCompleted}/{c.Lessons} lessons · {c.FlashcardsDue} cards due" +
        (c.BestQuizPercent is { } best ? $" · best quiz {best:0}%" : string.Empty),
        c.Lessons == 0 ? 0 : Math.Round(100.0 * c.LessonsCompleted / c.Lessons));

    public override string ToString() => Title;
}

public sealed record LessonItem(Guid Id, string Title, string Meta, bool IsCompleted)
{
    public string CompletedGlyph => IsCompleted ? "" : string.Empty;

    public override string ToString() => Title;
}

public sealed record QuizItem(Guid Id, string Title, int QuestionCount, bool IsAiGenerated)
{
    public string Meta => IsAiGenerated ? $"{QuestionCount} questions · AI-generated" : $"{QuestionCount} questions";

    public override string ToString() => Title;
}

public sealed partial class QuizQuestionItem(int index, string question, IReadOnlyList<string> choices) : ObservableObject
{
    public int Index { get; } = index;

    public string Question { get; } = question;

    public string Header => $"{Index + 1}. {Question}";

    public IReadOnlyList<string> Choices { get; } = choices;

    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? Feedback { get; set; }

    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
}

public sealed partial class ExerciseItem(MechanicAI.Application.Training.Exercise exercise) : ObservableObject
{
    public MechanicAI.Application.Training.Exercise Exercise { get; } = exercise;

    public string Prompt => Exercise.Prompt;

    public string Unit => Exercise.Unit ?? string.Empty;

    public bool IsMultipleChoice => Exercise.IsMultipleChoice;

    public IReadOnlyList<string> Choices => Exercise.Choices;

    [ObservableProperty]
    public partial string? Response { get; set; }

    [ObservableProperty]
    public partial int SelectedChoice { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? Feedback { get; set; }

    [ObservableProperty]
    public partial bool? IsCorrect { get; set; }

    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
}

public sealed record ScenarioItem(string Key, string Title, string Meta, string Vehicle, string Complaint)
{
    public static ScenarioItem From(TrainingScenario s) => new(
        s.Key,
        s.Title,
        $"{s.Category} · {s.Difficulty}{(s.Codes.Count > 0 ? " · " + string.Join(", ", s.Codes) : string.Empty)}",
        s.VehicleDescription,
        s.Complaint);

    public override string ToString() => Title;
}

public sealed record ApprenticeTestOption(string Key, string Title)
{
    public override string ToString() => Title;
}

public sealed record TurnItem(string Input, string Result, string Feedback, int Points)
{
    public string PointsText => Points > 0 ? $"+{Points}" : Points.ToString(CultureInfo.CurrentCulture);

    public static TurnItem From(ApprenticeTurn t) => new(t.TestTitle ?? t.Input, t.Result ?? string.Empty, t.Feedback, t.Points);
}

public sealed record CustomerItem(Guid Id, string DisplayName, string Contact, Customer Customer)
{
    public static CustomerItem From(Customer c) => new(c.Id, c.DisplayName, Format.Join(new[] { c.Phone ?? string.Empty, c.Email ?? string.Empty }, string.Empty), c);

    public override string ToString() => DisplayName;
}

public sealed record EstimateItem(Guid Id, string Number, string Customer, string Vehicle, string StatusText, string TotalText)
{
    public static EstimateItem From(Estimate e) => new(
        e.Id,
        e.Number,
        e.Customer?.DisplayName ?? "No customer",
        e.Vehicle?.DisplayName ?? "No vehicle",
        e.Status.ToString(),
        Format.Money(e.Total));
}

public sealed partial class EstimateLineItem : ObservableObject
{
    public static readonly IReadOnlyList<string> KindLabels = new[] { "Labor", "Part", "Fee", "Sublet" };

    public IReadOnlyList<string> Kinds => KindLabels;

    [ObservableProperty]
    public partial int KindIndex { get; set; } = 1;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? PartNumber { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    public partial string QuantityText { get; set; } = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    public partial string UnitPriceText { get; set; } = "0";

    [ObservableProperty]
    public partial bool Taxable { get; set; } = true;

    public EstimateLineKind Kind => (EstimateLineKind)Math.Clamp(KindIndex, 0, KindLabels.Count - 1);

    public decimal? Quantity => Format.ParseDecimal(QuantityText);

    public decimal? UnitPrice => Format.ParseDecimal(UnitPriceText);

    public decimal Total => Quantity is { } q && UnitPrice is { } p ? decimal.Round(q * p, 2, MidpointRounding.AwayFromZero) : 0m;

    public string TotalText => Quantity is null || UnitPrice is null ? "invalid" : Format.Money(Total);
}

public sealed record InspectionSummaryItem(Guid Id, string Title, string Vehicle, string StatusText, string? Summary, bool IsCompleted)
{
    public static InspectionSummaryItem From(Inspection i) => new(
        i.Id,
        $"{i.TemplateName} — {Format.Local(i.CreatedUtc)}",
        i.Vehicle?.DisplayName ?? "Vehicle",
        i.Status == InspectionStatus.Completed ? "Completed" : "In progress",
        i.Summary,
        i.Status == InspectionStatus.Completed);

    public override string ToString() => Title;
}

public sealed partial class InspectionPointItem(InspectionItem item) : ObservableObject
{
    public static readonly IReadOnlyList<string> RatingLabels = new[] { "Not inspected", "Good", "Needs attention", "Urgent" };

    public Guid Id { get; } = item.Id;

    public string Section { get; } = item.Section;

    public string Name { get; } = item.Name;

    public IReadOnlyList<string> Ratings => RatingLabels;

    [ObservableProperty]
    public partial int RatingIndex { get; set; } = (int)item.Rating;

    [ObservableProperty]
    public partial string? Measurement { get; set; } = item.Measurement;

    [ObservableProperty]
    public partial string? Notes { get; set; } = item.Notes;

    public InspectionRating Rating => (InspectionRating)Math.Clamp(RatingIndex, 0, RatingLabels.Count - 1);
}

public sealed record HistoryEntryItem(string When, string Kind, string Title, string? Detail, Guid? ReferenceId, string? Code)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public static HistoryEntryItem From(VehicleHistoryItem i) => new(Format.Local(i.WhenUtc), i.Kind, i.Title, i.Detail, i.ReferenceId, i.Code);
}

public sealed record DtcOccurrenceItem(string Code, int Occurrences, string LastSeen)
{
    public string OccurrencesText => Occurrences == 1 ? "once" : $"{Occurrences} times";
}

public sealed record ConfirmedDiagnosisItem(Guid SessionId, string Vehicle, string Diagnosis, string Complaint, string Codes, string When);

public sealed record SearchSuggestion(string Title, string? Subtitle, string Glyph, MechanicAI.Application.Services.SearchAction Action)
{
    public override string ToString() => Title;
}

public sealed record ModelItem(string Name, string Details)
{
    public override string ToString() => Name;
}
