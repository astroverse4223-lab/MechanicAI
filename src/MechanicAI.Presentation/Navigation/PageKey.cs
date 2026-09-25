namespace MechanicAI.Presentation.Navigation;

/// <summary>Every top-level page of the workstation. The desktop shell maps each key to a Page type.</summary>
public enum PageKey
{
    Home,
    Vehicles,
    Diagnostics,
    Dtc,
    LiveData,
    KnowledgeBase,
    Wiring,
    Research,
    Assistant,
    Training,
    Apprentice,
    Shop,
    History,
    Settings,
}

/// <summary>Navigation parameters understood by the page view models.</summary>
public static class NavigationParameters
{
    /// <summary>Open the diagnostics page and build a new session from free text ("2017 Silverado P0171 lean").</summary>
    public sealed record StartDiagnosisFromText(string Text, Guid? VehicleId = null);

    /// <summary>Open the diagnostics page with the new-session form prefilled for a vehicle.</summary>
    public sealed record NewDiagnosisForVehicle(Guid VehicleId);

    /// <summary>Open the vehicles page and decode a VIN.</summary>
    public sealed record DecodeVin(string Vin);

    /// <summary>Open the vehicles page with the add-vehicle form prefilled from free text.</summary>
    public sealed record AddVehicle(string Description);

    /// <summary>Open a knowledge-base document (optionally at a page).</summary>
    public sealed record OpenDocument(Guid DocumentId, int? PageNumber = null);

    /// <summary>Ask the assistant a question.</summary>
    public sealed record AskAssistant(string Text);

    /// <summary>Run a web research query.</summary>
    public sealed record SearchWeb(string Query);
}

/// <summary>Navigation metadata for the shell menu.</summary>
public sealed record NavigationItem(PageKey Key, string Title, string Glyph, string Group, string? Tooltip = null);

public static class NavigationCatalog
{
    /// <summary>Menu entries in display order. Glyphs are Segoe Fluent Icons code points.</summary>
    public static readonly IReadOnlyList<NavigationItem> Items =
    [
        new(PageKey.Home, "Home", "", "Workspace", "Dashboard: recent vehicles, open sessions, system status"),
        new(PageKey.Vehicles, "Vehicles", "", "Workspace", "VIN decode, vehicle records, recalls"),
        new(PageKey.Diagnostics, "Diagnostics", "", "Workspace", "Guided probabilistic diagnostic sessions"),
        new(PageKey.Dtc, "Trouble codes", "", "Workspace", "DTC reference lookup"),
        new(PageKey.LiveData, "Live data", "", "Workspace", "OBD-II live data via ELM327"),
        new(PageKey.KnowledgeBase, "Knowledge base", "", "Information", "Service documents with cited answers"),
        new(PageKey.Wiring, "Wiring", "", "Information", "Wiring diagrams and circuit questions"),
        new(PageKey.Research, "Research", "", "Information", "Web research with cited summaries"),
        new(PageKey.Assistant, "Assistant", "", "Information", "AI assistant with tool calling"),
        new(PageKey.Training, "Training", "", "Learning", "Courses, quizzes, flashcards, exercises"),
        new(PageKey.Apprentice, "Apprentice", "", "Learning", "Practice diagnosing fictional cases"),
        new(PageKey.Shop, "Shop", "", "Business", "Customers, estimates, inspections"),
        new(PageKey.History, "History", "", "Business", "Vehicle service history"),
    ];

    public static NavigationItem Get(PageKey key) =>
        key == PageKey.Settings
            ? new NavigationItem(PageKey.Settings, "Settings", "", "System")
            : Items.First(i => i.Key == key);
}
