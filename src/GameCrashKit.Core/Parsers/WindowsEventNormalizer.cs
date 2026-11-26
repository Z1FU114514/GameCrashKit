using System.Globalization;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Parsers;

/// <summary>
/// Normalizes parsed Windows Event records into strongly typed models without guessing unknown versions.
/// </summary>
public static class WindowsEventNormalizer
{
    private static readonly HashSet<string> SupportedWerEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "APPCRASH",
        "BEX",
        "BEX64",
        "AppHangB1",
        "AppHangTransient",
        "MoAppCrash",
        "MoAppHang",
        "CLR20r3"
    };

    /// <summary>
    /// Normalizes an Application Error (Event ID 1000) record.
    /// </summary>
    public static NormalizedAppCrashEvent NormalizeAppCrash1000(ParsedEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var warnings = new List<string>();
        var isSupportedVersion = record.Version is null or 0;
        if (!isSupportedVersion)
        {
            warnings.Add($"Unsupported Event 1000 version: {record.Version}.");
        }

        string? appName = GetNamedOrNull(record.NamedFields, "AppName");
        string? appVersion = GetNamedOrNull(record.NamedFields, "AppVersion");
        string? moduleName = GetNamedOrNull(record.NamedFields, "ModuleName");
        string? moduleVersion = GetNamedOrNull(record.NamedFields, "ModuleVersion");
        string? exceptionCode = NormalizeHexCode(GetNamedOrNull(record.NamedFields, "ExceptionCode"));
        string? faultingOffset = GetNamedOrNull(record.NamedFields, "FaultingOffset");
        string? pidHex = GetNamedOrNull(record.NamedFields, "ProcessId");
        string? procCreateHex = GetNamedOrNull(record.NamedFields, "ProcessCreationTime");
        string? appPath = GetNamedOrNull(record.NamedFields, "AppPath");
        string? modulePath = GetNamedOrNull(record.NamedFields, "ModulePath");
        string? reportId = GetNamedOrNull(record.NamedFields, "IntegratorReportId")
                           ?? GetNamedOrNull(record.NamedFields, "ReportId");

        var usedPositionalFallback = false;

        // Only apply documented positional mapping for verified Version 0 unnamed EventData.
        if (record.HasUnnamedData &&
            record.NamedFields.Count == 0 &&
            isSupportedVersion &&
            record.PositionalFields.Count >= 13)
        {
            usedPositionalFallback = true;
            appName = NonEmptyOrNull(record.PositionalFields[0]);
            appVersion = NonEmptyOrNull(record.PositionalFields[1]);
            moduleName = NonEmptyOrNull(record.PositionalFields[3]);
            moduleVersion = NonEmptyOrNull(record.PositionalFields[4]);
            exceptionCode = NormalizeHexCode(NonEmptyOrNull(record.PositionalFields[6]));
            faultingOffset = NonEmptyOrNull(record.PositionalFields[7]);
            pidHex = NonEmptyOrNull(record.PositionalFields[8]);
            procCreateHex = NonEmptyOrNull(record.PositionalFields[9]);
            appPath = NonEmptyOrNull(record.PositionalFields[10]);
            modulePath = NonEmptyOrNull(record.PositionalFields[11]);
            reportId = NonEmptyOrNull(record.PositionalFields[12]);
        }
        else if (record.HasUnnamedData && record.NamedFields.Count == 0)
        {
            warnings.Add("Unnamed EventData on Event 1000 could not be safely mapped (unsupported version or insufficient fields).");
        }

        return new NormalizedAppCrashEvent(
            Source: record,
            AppName: appName,
            AppVersion: appVersion,
            ModuleName: moduleName,
            ModuleVersion: moduleVersion,
            ExceptionCode: exceptionCode,
            FaultingOffset: faultingOffset,
            FaultingProcessIdHex: pidHex,
            ProcessCreationTimeHex: procCreateHex,
            AppPath: appPath,
            ModulePath: modulePath,
            IntegratorReportId: reportId,
            UsedPositionalFallback: usedPositionalFallback,
            IsSupportedVersion: isSupportedVersion,
            Warnings: warnings);
    }

    /// <summary>
    /// Normalizes an Application Hang (Event ID 1002) record.
    /// </summary>
    public static NormalizedAppHangEvent NormalizeAppHang1002(ParsedEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var warnings = new List<string>();
        var isSupportedVersion = record.Version is null or 0;
        if (!isSupportedVersion)
        {
            warnings.Add($"Unsupported Event 1002 version: {record.Version}.");
        }

        string? appName = GetNamedOrNull(record.NamedFields, "AppName");
        string? appVersion = GetNamedOrNull(record.NamedFields, "AppVersion");
        string? pidHex = GetNamedOrNull(record.NamedFields, "ProcessId");
        string? startTimeHex = GetNamedOrNull(record.NamedFields, "StartTime");
        string? terminationTimeMs = GetNamedOrNull(record.NamedFields, "TerminationTime");
        string? exeFileName = GetNamedOrNull(record.NamedFields, "ExeFileName");
        string? reportId = GetNamedOrNull(record.NamedFields, "ReportId");
        string? hangType = GetNamedOrNull(record.NamedFields, "HangType");

        var usedPositionalFallback = false;

        if (record.HasUnnamedData &&
            record.NamedFields.Count == 0 &&
            isSupportedVersion &&
            record.PositionalFields.Count >= 7)
        {
            usedPositionalFallback = true;
            appName = NonEmptyOrNull(record.PositionalFields[0]);
            appVersion = NonEmptyOrNull(record.PositionalFields[1]);
            pidHex = NonEmptyOrNull(record.PositionalFields[2]);
            startTimeHex = NonEmptyOrNull(record.PositionalFields[3]);
            terminationTimeMs = NonEmptyOrNull(record.PositionalFields[4]);
            exeFileName = NonEmptyOrNull(record.PositionalFields[5]);
            reportId = NonEmptyOrNull(record.PositionalFields[6]);
            hangType = record.PositionalFields.Count >= 10 ? NonEmptyOrNull(record.PositionalFields[9]) : null;
        }
        else if (record.HasUnnamedData && record.NamedFields.Count == 0)
        {
            warnings.Add("Unnamed EventData on Event 1002 could not be safely mapped.");
        }

        return new NormalizedAppHangEvent(
            Source: record,
            AppName: appName,
            AppVersion: appVersion,
            FaultingProcessIdHex: pidHex,
            StartTimeHex: startTimeHex,
            TerminationTimeMs: terminationTimeMs,
            ExeFileName: exeFileName,
            ReportId: reportId,
            HangType: hangType,
            UsedPositionalFallback: usedPositionalFallback,
            IsSupportedVersion: isSupportedVersion,
            Warnings: warnings);
    }

    /// <summary>
    /// Normalizes a Windows Error Reporting (Event ID 1001) record.
    /// Rejects noise types like LiveKernelEvent, StoreAgentScanForUpdatesFailure0, crashpad_log, etc.
    /// </summary>
    public static NormalizedWerReportEvent NormalizeWerReport1001(ParsedEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var warnings = new List<string>();
        var isSupportedVersion = record.Version is null or 0;
        if (!isSupportedVersion)
        {
            warnings.Add($"Unsupported Event 1001 version: {record.Version}.");
        }

        var eventName = GetNamedOrNull(record.NamedFields, "EventName");
        var isSupportedReportType = !string.IsNullOrWhiteSpace(eventName) && SupportedWerEventNames.Contains(eventName);

        return new NormalizedWerReportEvent(
            Source: record,
            EventName: eventName,
            Bucket: GetNamedOrNull(record.NamedFields, "Bucket"),
            ReportId: GetNamedOrNull(record.NamedFields, "ReportId"),
            P1: GetNamedOrNull(record.NamedFields, "P1"),
            P2: GetNamedOrNull(record.NamedFields, "P2"),
            P3: GetNamedOrNull(record.NamedFields, "P3"),
            P4: GetNamedOrNull(record.NamedFields, "P4"),
            P5: GetNamedOrNull(record.NamedFields, "P5"),
            P6: GetNamedOrNull(record.NamedFields, "P6"),
            P7: GetNamedOrNull(record.NamedFields, "P7"),
            P8: GetNamedOrNull(record.NamedFields, "P8"),
            IsSupportedReportType: isSupportedReportType,
            IsSupportedVersion: isSupportedVersion,
            Warnings: warnings);
    }

    /// <summary>
    /// Normalizes a Kernel-Power (Event ID 41) record. Always flagged as system background only.
    /// </summary>
    public static NormalizedKernelPowerEvent NormalizeKernelPower41(ParsedEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var warnings = new List<string>();
        var isSupportedVersion = record.Version is null or 10 or 9 or 8 or 6;
        if (!isSupportedVersion)
        {
            warnings.Add($"Unverified Kernel-Power 41 version: {record.Version}.");
        }

        int? bugcheckCode = int.TryParse(GetNamedOrNull(record.NamedFields, "BugcheckCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bc)
            ? bc
            : null;

        int? sleepInProgress = int.TryParse(GetNamedOrNull(record.NamedFields, "SleepInProgress"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sip)
            ? sip
            : null;

        var powerButtonTs = GetNamedOrNull(record.NamedFields, "PowerButtonTimestamp");

        return new NormalizedKernelPowerEvent(
            Source: record,
            BugcheckCode: bugcheckCode,
            SleepInProgress: sleepInProgress,
            PowerButtonTimestamp: powerButtonTs,
            IsSystemBackgroundOnly: true,
            IsSupportedVersion: isSupportedVersion,
            Warnings: warnings);
    }

    /// <summary>
    /// Normalizes hex exception codes (e.g. "c0000005" or "0xC0000005") into canonical "0xc0000005".
    /// </summary>
    public static string? NormalizeHexCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var hexPart = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? trimmed[2..]
            : trimmed;

        if (ulong.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return hexPart.Length <= 8
                ? $"0x{value:x8}"
                : $"0x{value:x}";
        }

        return trimmed;
    }

    private static string? GetNamedOrNull(IReadOnlyDictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out var val) ? NonEmptyOrNull(val) : null;
    }

    private static string? NonEmptyOrNull(string? val)
    {
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }
}
