namespace GameCrashKit.Core.Models;

public enum IncidentAnchorKind
{
    ApplicationCrash = 0,
    ApplicationHang,
    WerReportOnly
}

public sealed record BackgroundEventRecord(
    EvidenceItem Evidence,
    AttributionStrength Attribution,
    string SummaryKind,
    int? BugcheckCode = null);

public sealed record CrashIncident(
    string IncidentId,
    IncidentAnchorKind AnchorKind,
    DateTimeOffset? OccurredAtUtc,
    string? AppName,
    string? NormalizedAppPath,
    string? DisambiguatedPathSuffix,
    string? ModuleName,
    string? ModulePath,
    string? ExceptionCode,
    string? FaultingProcessIdHex,
    string? ProcessCreationTimeHex,
    string? ReportId,
    string? HangType,
    bool IsLikelyGame,
    bool IsSystemNoise,
    bool IsSupportedSchemaVersion,
    int OccurrenceCountInWindow,
    IReadOnlyList<DateTimeOffset> OccurrenceTimestampsUtc,
    AttributionStrength WerLinkAttribution,
    EvidenceItem PrimaryEvidence,
    EvidenceItem? LinkedWerEvidence,
    IReadOnlyList<BackgroundEventRecord> NearbySystemBackground,
    UserConfirmedEngineLog? ConfirmedEngineLog,
    IReadOnlyList<string> AmbiguityNotes);
