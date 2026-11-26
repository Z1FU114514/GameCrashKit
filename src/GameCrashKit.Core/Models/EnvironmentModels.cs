namespace GameCrashKit.Core.Models;

public enum EnvironmentNoticeLevel
{
    Info = 0,
    Attention,
    Unavailable
}

public sealed record GpuAdapterFact(
    string Name,
    string? DriverVersion,
    string? DriverReleaseDateIso,
    bool IsLikelyVirtual);

public sealed record TargetPathFact(
    bool Checked,
    bool HasNonAscii,
    bool ExceedsMaxPath260,
    int? PathLength,
    string? DisambiguatedSuffix);

public sealed record OneDriveDocsFact(
    bool Checked,
    bool IsInOneDrive,
    bool HasReparsePoint,
    EnvironmentNoticeLevel NoticeLevel,
    string StatusText);

public sealed record VcRuntimeFact(
    bool X64Installed,
    string? X64Version,
    bool X86Installed,
    string? X86Version,
    EnvironmentNoticeLevel NoticeLevel);

public sealed record PageFileFact(
    bool Checked,
    bool IsDisabled,
    int TotalAllocatedMb,
    int PageFileCount,
    bool IsSystemManaged,
    EnvironmentNoticeLevel NoticeLevel,
    string StatusText);

public sealed record RunningOverlaysFact(
    bool Checked,
    IReadOnlyList<string> DetectedOverlayNames,
    string Note);

public sealed record SystemLocaleAndSecurityFact(
    string OsDisplayLabel,
    int BuildNumber,
    int TotalPhysicalMemoryMb,
    int AcpCodePage,
    bool IsBetaUtf8Enabled,
    bool? HvciEnabled);

public sealed record EnvironmentSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<GpuAdapterFact> GpuAdapters,
    TargetPathFact TargetPath,
    OneDriveDocsFact OneDriveDocs,
    VcRuntimeFact VcRuntime,
    PageFileFact PageFile,
    RunningOverlaysFact RunningOverlays,
    SystemLocaleAndSecurityFact SystemInfo,
    IReadOnlyList<string> ProbeWarnings);
