namespace MechanicAI.Domain.Enums;

/// <summary>
/// How trustworthy a piece of information is. Every diagnostic statement the app
/// shows carries one of these so speculation is never presented as fact.
/// </summary>
public enum EvidenceClass
{
    /// <summary>Confirmed by an authoritative system of record (e.g. NHTSA VIN decode) or a confirmed technician test.</summary>
    Verified = 0,
    /// <summary>Taken from a cited source (document, web page, reference database).</summary>
    SourceDerived = 1,
    /// <summary>Observed or measured by the technician.</summary>
    TechnicianObservation = 2,
    /// <summary>Reasoned by the AI from the available evidence.</summary>
    AiInference = 3,
    /// <summary>A plausible possibility that has not been tested.</summary>
    UnconfirmedPossibility = 4,
}

/// <summary>Source authority, in priority order (lower value = more authoritative).</summary>
public enum SourceType
{
    Manufacturer = 1,
    Government = 2,
    ProfessionalDatabase = 3,
    TechnicalPublication = 4,
    TechnicalCommunity = 5,
    Forum = 6,
    SocialMedia = 7,
    Unknown = 8,
    /// <summary>The technician's own uploaded document or knowledge base.</summary>
    PrivateDocument = 9,
    /// <summary>Built-in curated reference content shipped with the app.</summary>
    BuiltInReference = 10,
}

public enum DiagnosticCategory
{
    Ignition, Fuel, AirIntake, Vacuum, Mechanical, Electrical, Sensor, Emissions, Exhaust,
    Cooling, Lubrication, Transmission, Drivetrain, Network, Module, Software, Charging,
    Starting, Brakes, Suspension, Steering, Hvac, Body, Restraints, HighVoltage, Other,
}

/// <summary>Workflow stage of a diagnostic session (complaint → verification).</summary>
public enum DiagnosticSessionStatus
{
    Intake = 0,
    Analysis = 1,
    Testing = 2,
    Isolated = 3,
    Repair = 4,
    Verification = 5,
    Completed = 6,
    Abandoned = 7,
}

public enum DiagnosticNodeKind { Root, Category, Cause }

public enum CauseStatus
{
    Open = 0,
    /// <summary>Evidence points toward this cause but it is not yet confirmed.</summary>
    Suspected = 1,
    /// <summary>Posterior probability is high enough that the fault appears isolated.</summary>
    Likely = 2,
    /// <summary>The technician confirmed the diagnosis.</summary>
    Confirmed = 3,
    RuledOut = 4,
}

public enum TestStatus { Available, Recommended, Completed, Skipped }

public enum TestResult { Pass, Fail, Inconclusive }

public enum DiagnosticStepKind
{
    SessionCreated, ComplaintRecorded, SymptomAdded, DtcAdded, DtcRemoved, ObservationAdded,
    ClarifyingQuestionAnswered, TreeGenerated, AiAnalysis, ResearchAttached, TestRecommended,
    TestResultRecorded, TestResultReverted, CauseStatusChanged, DiagnosisConfirmed,
    RepairRecorded, VerificationRecorded, SessionCompleted, SessionReopened, NoteAdded,
    ReportExported, TestAdded, CauseAdded,
}

public enum ActorKind { Technician, Ai, System }

public enum DtcStatus { Current, Pending, Permanent, History }

public enum DtcSystem { Powertrain, Body, Chassis, Network }

public enum DocumentKind
{
    ServiceManual, RepairManual, WiringDiagram, TechnicalBulletin, Notes, TrainingMaterial,
    DiagnosticDocument, Photo, Other,
}

public enum DocumentStatus
{
    Pending,
    Processing,
    /// <summary>Text extracted, chunked, and embedded for semantic search.</summary>
    Ready,
    /// <summary>Text extracted and keyword-indexed; no embedding model was available.</summary>
    ReadyKeywordOnly,
    /// <summary>No text layer found and OCR was unavailable.</summary>
    NeedsOcr,
    Failed,
}

public enum UserRole { Owner, Admin, Technician, Apprentice, ServiceAdvisor }

public enum ConversationKind { Assistant, Diagnostic, Document, Wiring, Training, ImageAnalysis, Research, LiveData }

public enum MessageRole { System, User, Assistant, Tool }

public enum TrainingCategory
{
    Fundamentals, Engine, FuelSystems, Ignition, Electrical, Diagnostics, Cooling, Hvac,
    Brakes, Suspension, Steering, Transmission, CanBus, Adas, Hybrid, Ev,
}

public enum TrainingLevel { Beginner, Intermediate, Advanced }

public enum TrainingAttemptKind { Quiz, Scenario, Exercise, Flashcards, Lesson }

public enum RepairKind { Repair, Maintenance, Inspection, Recall, Diagnostic }

public enum RepairStatus { Planned, InProgress, Completed, Declined }

public enum EstimateStatus { Draft, Presented, Approved, Declined, Converted }

public enum EstimateLineKind { Labor, Part, Fee, Sublet }

public enum InspectionRating { NotInspected, Good, NeedsAttention, Urgent }

public enum InspectionStatus { InProgress, Completed }

public enum AttachmentKind { Photo, Document, Video, Audio, Other }

public enum BookmarkKind { WebSource, Document, DiagnosticSession, Dtc, Lesson, Vehicle, SearchResult }

public enum SearchIntent { Dtc, Vin, Vehicle, Diagnostic, Repair, Web, Document, Wiring, Training, Component }

public enum RecallStatus { Unknown, Open, Completed, NotApplicable }

public enum VehicleDataSource { Manual, NhtsaVpic, ObdScan, Imported, Sample }

public enum VerificationLevel { Verified, TechnicianEntered, Unverified }

public enum DistanceUnit { Miles, Kilometers }

public enum NoteKind { General, Diagnostic, Customer, ShopTip, Internal }
