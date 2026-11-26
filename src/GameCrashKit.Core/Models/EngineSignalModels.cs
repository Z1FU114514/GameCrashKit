namespace GameCrashKit.Core.Models;

public enum EngineLogKind
{
    Unity = 0,
    Unreal,
    GenericManual
}

public enum LogFatalityKind
{
    Unknown = 0,
    Fatal,
    NonFatalEnsure
}

public sealed record UserConfirmedEngineLog(
    EngineLogKind Kind,
    string DisplayLabel,
    string SourceLocation,
    DateTimeOffset? FileLastWriteUtc,
    bool IsTruncated,
    bool MayBeOverwritten,
    string? EngineVersion,
    string? RhiBackend,
    bool HasGpuDeviceLostSignal,
    string? GpuLostToken,
    bool HasOutOfMemorySignal,
    string? OomToken,
    IReadOnlyList<string> RawErrorExcerptLines,
    LogFatalityKind Fatality = LogFatalityKind.Unknown);
