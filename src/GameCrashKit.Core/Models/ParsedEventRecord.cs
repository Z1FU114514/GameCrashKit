namespace GameCrashKit.Core.Models;

/// <summary>
/// Immutable structured representation of a raw Windows Event XML record.
/// </summary>
public sealed record ParsedEventRecord(
    string Channel,
    string ProviderName,
    int EventId,
    int? Version,
    int? Level,
    long? RecordId,
    DateTimeOffset? TimeCreatedUtc,
    IReadOnlyDictionary<string, string> NamedFields,
    IReadOnlyList<string> PositionalFields,
    bool HasUnnamedData);

/// <summary>
/// Normalized Application Error (Event ID 1000) record.
/// </summary>
public sealed record NormalizedAppCrashEvent(
    ParsedEventRecord Source,
    string? AppName,
    string? AppVersion,
    string? ModuleName,
    string? ModuleVersion,
    string? ExceptionCode,
    string? FaultingOffset,
    string? FaultingProcessIdHex,
    string? ProcessCreationTimeHex,
    string? AppPath,
    string? ModulePath,
    string? IntegratorReportId,
    bool UsedPositionalFallback,
    bool IsSupportedVersion,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Normalized Application Hang (Event ID 1002) record.
/// </summary>
public sealed record NormalizedAppHangEvent(
    ParsedEventRecord Source,
    string? AppName,
    string? AppVersion,
    string? FaultingProcessIdHex,
    string? StartTimeHex,
    string? TerminationTimeMs,
    string? ExeFileName,
    string? ReportId,
    string? HangType,
    bool UsedPositionalFallback,
    bool IsSupportedVersion,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Normalized Windows Error Reporting (Event ID 1001) record.
/// Only whitelisted crash/hang report types are marked supported.
/// </summary>
public sealed record NormalizedWerReportEvent(
    ParsedEventRecord Source,
    string? EventName,
    string? Bucket,
    string? ReportId,
    string? P1,
    string? P2,
    string? P3,
    string? P4,
    string? P5,
    string? P6,
    string? P7,
    string? P8,
    bool IsSupportedReportType,
    bool IsSupportedVersion,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Normalized Kernel-Power (Event ID 41) record.
/// Always treated strictly as system timeline background.
/// </summary>
public sealed record NormalizedKernelPowerEvent(
    ParsedEventRecord Source,
    int? BugcheckCode,
    int? SleepInProgress,
    string? PowerButtonTimestamp,
    bool IsSystemBackgroundOnly,
    bool IsSupportedVersion,
    IReadOnlyList<string> Warnings);
