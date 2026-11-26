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
    IReadOnlyList<EvidenceItem> AllEvidence);
