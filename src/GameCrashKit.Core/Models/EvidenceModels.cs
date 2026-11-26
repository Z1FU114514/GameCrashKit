namespace GameCrashKit.Core.Models;

public enum EvidenceSourceKind
{
    WindowsApplicationCrash1000 = 0,
    WindowsApplicationHang1002,
    WindowsWerReport1001,
    WindowsKernelPower41,
    WindowsDisplay4101,
    UnityPlayerLog,
    UnrealCrashContext,
    ManualLogFile
}

public enum AttributionStrength
{
    Direct = 0,
    Probable,
    UserSelected,
    NearbyOnly,
    Unlinked
}

public enum EvidenceStrength
{
    ExplicitRecord = 0,
    LimitedClue,
    Insufficient
}

public static class StrengthDisplayExtensions
{
    public static string ToDisplayText(this AttributionStrength strength) => strength switch
    {
        AttributionStrength.Direct => "直接",
        AttributionStrength.Probable => "可能",
        AttributionStrength.UserSelected => "手选",
        AttributionStrength.NearbyOnly => "仅邻近",
        AttributionStrength.Unlinked => "未关联",
        _ => "未关联"
    };

    public static string ToDisplayText(this EvidenceStrength strength) => strength switch
    {
        EvidenceStrength.ExplicitRecord => "明确记录",
        EvidenceStrength.LimitedClue => "有限线索",
        EvidenceStrength.Insufficient => "证据不足",
        _ => "证据不足"
    };
}

public sealed record EvidenceItem(
    string EvidenceId,
    EvidenceSourceKind SourceKind,
    DateTimeOffset? TimestampUtc,
    string? TimestampPrecision,
    string SourceLocation,
    string ParserVersion,
    IReadOnlyDictionary<string, string> AllowedFields,
    bool HasSchemaWarning);
