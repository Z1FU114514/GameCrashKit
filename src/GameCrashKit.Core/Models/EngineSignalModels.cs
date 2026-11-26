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

public enum LogSignalId
{
    GpuDeviceLost = 0,
    OutOfMemory
}

public sealed record LogSignalMatch(
    LogSignalId SignalId,
    string MatchedToken);

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
    LogFatalityKind Fatality = LogFatalityKind.Unknown,
    IReadOnlyList<LogSignalMatch>? Signals = null,
    string? FormatDisplayLabel = null)
{
    public string EffectiveFormatLabel =>
        !string.IsNullOrWhiteSpace(FormatDisplayLabel) ? FormatDisplayLabel! : GetFormatDisplayLabel(Kind);

    public IReadOnlyList<LogSignalMatch> EffectiveSignals
    {
        get
        {
            if (Signals is { Count: > 0 })
            {
                return Signals;
            }

            if (Signals is not null && !HasGpuDeviceLostSignal && !HasOutOfMemorySignal)
            {
                return Signals;
            }

            var list = new List<LogSignalMatch>(2);
            if (HasGpuDeviceLostSignal)
            {
                list.Add(new LogSignalMatch(LogSignalId.GpuDeviceLost, GpuLostToken ?? "GPU_DEVICE_LOST"));
            }

            if (HasOutOfMemorySignal)
            {
                list.Add(new LogSignalMatch(LogSignalId.OutOfMemory, OomToken ?? "OUT_OF_MEMORY"));
            }

            return list;
        }
    }

    public static string GetFormatDisplayLabel(EngineLogKind kind) => kind switch
    {
        EngineLogKind.Unity => "Unity 引擎日志",
        EngineLogKind.Unreal => "Unreal 崩溃上下文",
        _ => "通用文本日志"
    };
}
