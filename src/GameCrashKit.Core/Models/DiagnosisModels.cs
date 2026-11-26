namespace GameCrashKit.Core.Models;

public sealed record DiagnosisFinding(
    string RuleId,
    string RuleVersion,
    string Title,
    string Statement,
    AttributionStrength Attribution,
    EvidenceStrength EvidenceStrength,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> AmbiguityNotes,
    string NextActionTemplateId,
    string NextActionText,
    string? OfficialReferenceLabel,
    string? OfficialReferenceUrl,
    int SpecificityRank);

public enum ReportSubjectKind
{
    WindowsIncident = 0,
    StandaloneLogOnly,
    InsufficientEvidence
}

public sealed record DiagnosisReport(
    string SchemaVersion,
    string AppVersion,
    string RulesetVersion,
    DateTimeOffset GeneratedAtUtc,
    ScanRequest ScanRequest,
    CrashIncident? SelectedIncident,
    IReadOnlyList<DiagnosisFinding> PrimaryFindings,
    IReadOnlyList<BackgroundEventRecord> SystemBackgroundEvents,
    IReadOnlyList<SourceCoverage> Coverage,
    EnvironmentSnapshot? Environment,
    IReadOnlyList<EvidenceItem> AllEvidence,
    IReadOnlyList<DiagnosisFinding>? AdditionalHints = null,
    ReportSubjectKind SubjectKind = ReportSubjectKind.InsufficientEvidence,
    UserConfirmedEngineLog? StandaloneLog = null,
    string? WindowsScanStateLabel = null);
