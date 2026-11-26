namespace GameCrashKit.Core.Models;

public enum ScanWindowPreset
{
    Last2Hours,
    Last24Hours,
    Last7Days
}

public static class ScanWindowPresetExtensions
{
    public static TimeSpan ToTimeSpan(this ScanWindowPreset preset) => preset switch
    {
        ScanWindowPreset.Last2Hours => TimeSpan.FromHours(2),
        ScanWindowPreset.Last24Hours => TimeSpan.FromHours(24),
        ScanWindowPreset.Last7Days => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(2)
    };

    public static string ToDisplayText(this ScanWindowPreset preset) => preset switch
    {
        ScanWindowPreset.Last2Hours => "最近 2 小时",
        ScanWindowPreset.Last24Hours => "最近 24 小时",
        ScanWindowPreset.Last7Days => "最近 7 天",
        _ => "最近 2 小时"
    };
}

public sealed record ScanRequest(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    bool IncludeAllApplications = false,
    int MaxEventsPerChannel = 1_000);

public enum SourceCoverageStatus
{
    Ok = 0,
    PartialTimeout,
    Truncated,
    AccessDenied,
    NotFound,
    Failed,
    Skipped,
    Cancelled
}

public sealed record SourceCoverage(
    string SourceName,
    DateTimeOffset RequestedStartUtc,
    DateTimeOffset RequestedEndUtc,
    int RecordsRead,
    bool Truncated,
    SourceCoverageStatus Status,
    string? Detail);
