using System.Globalization;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Parsers;

namespace GameCrashKit.Core.Correlation;

public sealed record CorrelatorOptions(
    bool IncludeVerifiedDisplay4101 = false,
    int WerTimeWindowSeconds = 120,
    int NearbyGraphicWindowSeconds = 120);

public sealed record CorrelateResult(
    IReadOnlyList<CrashIncident> Candidates,
    int FilteredNoiseCount,
    IReadOnlyList<BackgroundEventRecord> SystemBackgroundEvents,
    IReadOnlyList<EvidenceItem> AllEvidence);

/// <summary>
/// Deterministic, zero-guessing incident correlator, WER linker, noise filter, and window aggregator.
/// </summary>
public static class IncidentCorrelator
{
    private const string ParserVersion = "1.0.0";
    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    private static readonly HashSet<string> WerCrashCompatibleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "APPCRASH",
        "BEX",
        "BEX64",
        "MoAppCrash",
        "CLR20r3"
    };

    private static readonly HashSet<string> WerHangCompatibleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppHangB1",
        "AppHangTransient",
        "MoAppHang"
    };

    public static CorrelateResult Correlate(
        ScanRequest request,
        IReadOnlyList<ParsedEventRecord> rawEvents,
        CorrelatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rawEvents);

        options ??= new CorrelatorOptions();

        // 1. Deduplicate strictly by (Channel, RecordId) within the snapshot.
        var deduplicated = DeduplicateByChannelAndRecordId(rawEvents);

        var anchors = new List<AnchorBuilder>();
        var werEntries = new List<WerBuilder>();
        var kernelPowerBackground = new List<BackgroundEventRecord>();
        var display4101Events = new List<ParsedEventRecord>();
        var allEvidence = new List<EvidenceItem>();

        var evidenceSeq = 0;
        string NextEvidenceId(string prefix) => $"{prefix}-{++evidenceSeq}";

        // 2. Normalize and classify each event by channel, provider, and event ID.
        foreach (var ev in deduplicated)
        {
            if (IsApplicationChannel(ev) &&
                string.Equals(ev.ProviderName, "Application Error", StringComparison.OrdinalIgnoreCase) &&
                ev.EventId == 1000)
            {
                var norm = WindowsEventNormalizer.NormalizeAppCrash1000(ev);
                var evidence = BuildEvidence(NextEvidenceId("ev-1000"), EvidenceSourceKind.WindowsApplicationCrash1000, ev, BuildCrashAllowedFields(norm), !norm.IsSupportedVersion || norm.Warnings.Count > 0);
                allEvidence.Add(evidence);
                anchors.Add(AnchorBuilder.FromCrash(norm, evidence));
            }
            else if (IsApplicationChannel(ev) &&
                string.Equals(ev.ProviderName, "Application Hang", StringComparison.OrdinalIgnoreCase) &&
                ev.EventId == 1002)
            {
                var norm = WindowsEventNormalizer.NormalizeAppHang1002(ev);
                var evidence = BuildEvidence(NextEvidenceId("ev-1002"), EvidenceSourceKind.WindowsApplicationHang1002, ev, BuildHangAllowedFields(norm), !norm.IsSupportedVersion || norm.Warnings.Count > 0);
                allEvidence.Add(evidence);
                anchors.Add(AnchorBuilder.FromHang(norm, evidence));
            }
            else if (IsApplicationChannel(ev) &&
                string.Equals(ev.ProviderName, "Windows Error Reporting", StringComparison.OrdinalIgnoreCase) &&
                ev.EventId == 1001)
            {
                var norm = WindowsEventNormalizer.NormalizeWerReport1001(ev);
                if (!norm.IsSupportedReportType)
                {
                    continue;
                }

                var evidence = BuildEvidence(NextEvidenceId("ev-1001"), EvidenceSourceKind.WindowsWerReport1001, ev, BuildWerAllowedFields(norm), !norm.IsSupportedVersion || norm.Warnings.Count > 0);
                allEvidence.Add(evidence);
                werEntries.Add(new WerBuilder(norm, evidence));
            }
            else if (IsSystemChannel(ev) &&
                string.Equals(ev.ProviderName, "Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) &&
                ev.EventId == 41)
            {
                var norm = WindowsEventNormalizer.NormalizeKernelPower41(ev);
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Provider"] = ev.ProviderName,
                    ["EventId"] = "41"
                };
                if (norm.BugcheckCode.HasValue)
                {
                    fields["BugcheckCode"] = norm.BugcheckCode.Value.ToString(CultureInfo.InvariantCulture);
                }

                var evidence = BuildEvidence(NextEvidenceId("ev-kp41"), EvidenceSourceKind.WindowsKernelPower41, ev, fields, !norm.IsSupportedVersion || norm.Warnings.Count > 0);
                allEvidence.Add(evidence);
                kernelPowerBackground.Add(new BackgroundEventRecord(evidence, AttributionStrength.NearbyOnly, "KernelPower41", norm.BugcheckCode));
            }
            else if (options.IncludeVerifiedDisplay4101 &&
                IsSystemChannel(ev) &&
                string.Equals(ev.ProviderName, "Display", StringComparison.OrdinalIgnoreCase) &&
                ev.EventId == 4101)
            {
                display4101Events.Add(ev);
            }
        }

        var displayBackground = new List<BackgroundEventRecord>();
        foreach (var disp in display4101Events)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Provider"] = disp.ProviderName,
                ["EventId"] = "4101"
            };
            var evidence = BuildEvidence(NextEvidenceId("ev-disp4101"), EvidenceSourceKind.WindowsDisplay4101, disp, fields, HasSchemaWarning: false);
            allEvidence.Add(evidence);
            displayBackground.Add(new BackgroundEventRecord(evidence, AttributionStrength.NearbyOnly, "Display4101"));
        }

        // 3. Link supported 1001 reports to compatible anchors using strict priority rules.
        var standaloneWerCandidates = new List<CrashIncident>();
        foreach (var wer in werEntries)
        {
            if (TryLinkWerToAnchor(wer, anchors, options.WerTimeWindowSeconds, out var ambiguityNote))
            {
                continue;
            }

            standaloneWerCandidates.Add(wer.ToStandaloneIncident(ambiguityNote));
        }

        // 4. Attach conditional nearby Display 4101 background to anchors within ±NearbyGraphicWindowSeconds.
        if (displayBackground.Count > 0)
        {
            foreach (var anchor in anchors)
            {
                if (!anchor.OccurredAtUtc.HasValue)
                {
                    continue;
                }

                foreach (var bg in displayBackground)
                {
                    if (!bg.Evidence.TimestampUtc.HasValue)
                    {
                        continue;
                    }

                    var deltaSec = Math.Abs((bg.Evidence.TimestampUtc.Value - anchor.OccurredAtUtc.Value).TotalSeconds);
                    if (deltaSec <= options.NearbyGraphicWindowSeconds)
                    {
                        anchor.NearbyBackground.Add(bg);
                    }
                }
            }
        }

        // 5. Build unaggregated anchor incidents, then aggregate exact repeats within the scan window.
        var allIncidents = new List<CrashIncident>(anchors.Count + standaloneWerCandidates.Count);
        allIncidents.AddRange(anchors.Select(a => a.ToIncident()));
        allIncidents.AddRange(standaloneWerCandidates);

        var aggregated = AggregateRepeatedIncidents(allIncidents);

        // 6. Noise filtering & deterministic ranking.
        var filteredNoiseCount = 0;
        var visible = new List<CrashIncident>(aggregated.Count);
        foreach (var incident in aggregated)
        {
            if (!request.IncludeAllApplications && incident.IsSystemNoise)
            {
                filteredNoiseCount++;
                continue;
            }

            visible.Add(incident);
        }

        visible.Sort(CompareIncidents);

        var allBackground = new List<BackgroundEventRecord>(kernelPowerBackground.Count + displayBackground.Count);
        allBackground.AddRange(kernelPowerBackground);
        allBackground.AddRange(displayBackground);
        allBackground.Sort(static (a, b) => Nullable.Compare(b.Evidence.TimestampUtc, a.Evidence.TimestampUtc));

        return new CorrelateResult(
            Candidates: visible,
            FilteredNoiseCount: filteredNoiseCount,
            SystemBackgroundEvents: allBackground,
            AllEvidence: allEvidence);
    }

    private static bool TryLinkWerToAnchor(
        WerBuilder wer,
        List<AnchorBuilder> anchors,
        int werTimeWindowSeconds,
        out string? ambiguityNote)
    {
        ambiguityNote = null;

        // Priority 1: Direct ReportId match on compatible, unlinked, non-conflicting unique anchor.
        var werReportId = NormalizeGuid(wer.Report.ReportId);
        if (werReportId is not null)
        {
            var p1Matches = anchors.Where(a =>
                    a.LinkedWerEvidence is null &&
                    IsCompatibleWerAnchor(wer.Report.EventName, a.AnchorKind) &&
                    string.Equals(NormalizeGuid(a.ReportId), werReportId, StringComparison.OrdinalIgnoreCase) &&
                    !HasAppNameConflict(wer.AppNameHint, a.AppName))
                .ToList();

            if (p1Matches.Count == 1)
            {
                p1Matches[0].AttachWer(wer.Evidence, AttributionStrength.Direct);
                return true;
            }

            if (p1Matches.Count > 1)
            {
                ambiguityNote = "存在多个 ReportId 相同的候选事件，WER 报告保持独立未自动合并。";
                return false;
            }
        }

        // Priority 2: Probable match ONLY via unique (NormalizedFullPath OR (FaultingPID + ProcessCreationTime)) within ±120s.
        var werTime = wer.Report.Source.TimeCreatedUtc;
        if (!werTime.HasValue)
        {
            return false;
        }

        if (wer.NormalizedRootedPath is not null)
        {
            var pathMatches = anchors.Where(a =>
                    a.LinkedWerEvidence is null &&
                    IsCompatibleWerAnchor(wer.Report.EventName, a.AnchorKind) &&
                    a.OccurredAtUtc.HasValue &&
                    Math.Abs((werTime.Value - a.OccurredAtUtc.Value).TotalSeconds) <= werTimeWindowSeconds &&
                    string.Equals(a.NormalizedAppPath, wer.NormalizedRootedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pathMatches.Count == 1)
            {
                pathMatches[0].AttachWer(wer.Evidence, AttributionStrength.Probable);
                return true;
            }

            if (pathMatches.Count > 1)
            {
                ambiguityNote = "时间窗内存在多个同路径候选事故，WER 报告保持独立以防误并。";
                return false;
            }

            return false;
        }

        // When rooted path is unavailable on WER, require BOTH EventData PID and ProcessCreationTime/StartTime.
        if (wer.FaultingPidHex is not null && wer.ProcessCreationTimeHex is not null)
        {
            var pidTimeMatches = anchors.Where(a =>
                    a.LinkedWerEvidence is null &&
                    IsCompatibleWerAnchor(wer.Report.EventName, a.AnchorKind) &&
                    a.OccurredAtUtc.HasValue &&
                    Math.Abs((werTime.Value - a.OccurredAtUtc.Value).TotalSeconds) <= werTimeWindowSeconds &&
                    !HasAppNameConflict(wer.AppNameHint, a.AppName) &&
                    string.Equals(a.FaultingProcessIdHex, wer.FaultingPidHex, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.ProcessCreationTimeHex, wer.ProcessCreationTimeHex, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (pidTimeMatches.Count == 1)
            {
                pidTimeMatches[0].AttachWer(wer.Evidence, AttributionStrength.Probable);
                return true;
            }

            if (pidTimeMatches.Count > 1)
            {
                ambiguityNote = "存在多个进程标识相近的候选事故，WER 报告保持独立未自动合并。";
                return false;
            }
        }

        return false;
    }

    private static List<CrashIncident> AggregateRepeatedIncidents(List<CrashIncident> incidents)
    {
        var groups = incidents.GroupBy(static i => new AggregationKey(
            i.AnchorKind,
            (i.AppName ?? string.Empty).ToLowerInvariant(),
            (i.NormalizedAppPath ?? string.Empty).ToLowerInvariant(),
            (i.ModuleName ?? string.Empty).ToLowerInvariant(),
            (i.ExceptionCode ?? string.Empty).ToLowerInvariant()));

        var result = new List<CrashIncident>();
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(static i => i.OccurredAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(static i => i.PrimaryEvidence.SourceLocation, StringComparer.Ordinal)
                .ToList();

            var representative = ordered[0];
            if (ordered.Count == 1)
            {
                result.Add(representative);
                continue;
            }

            var timestamps = ordered
                .Where(static i => i.OccurredAtUtc.HasValue)
                .Select(static i => i.OccurredAtUtc!.Value)
                .ToList();

            result.Add(representative with
            {
                OccurrenceCountInWindow = ordered.Count,
                OccurrenceTimestampsUtc = timestamps
            });
        }

        return result;
    }

    private static int CompareIncidents(CrashIncident x, CrashIncident y)
    {
        var gameCmp = y.IsLikelyGame.CompareTo(x.IsLikelyGame);
        if (gameCmp != 0)
        {
            return gameCmp;
        }

        var kindRankX = x.AnchorKind == IncidentAnchorKind.WerReportOnly ? 1 : 0;
        var kindRankY = y.AnchorKind == IncidentAnchorKind.WerReportOnly ? 1 : 0;
        var kindCmp = kindRankX.CompareTo(kindRankY);
        if (kindCmp != 0)
        {
            return kindCmp;
        }

        var timeCmp = Nullable.Compare(y.OccurredAtUtc, x.OccurredAtUtc);
        if (timeCmp != 0)
        {
            return timeCmp;
        }

        return string.CompareOrdinal(x.PrimaryEvidence.SourceLocation, y.PrimaryEvidence.SourceLocation);
    }

    private static List<ParsedEventRecord> DeduplicateByChannelAndRecordId(IReadOnlyList<ParsedEventRecord> rawEvents)
    {
        var seen = new HashSet<(string Channel, long RecordId)>();
        var list = new List<ParsedEventRecord>(rawEvents.Count);

        foreach (var ev in rawEvents)
        {
            if (ev.RecordId.HasValue)
            {
                var key = (ev.Channel.ToLowerInvariant(), ev.RecordId.Value);
                if (!seen.Add(key))
                {
                    continue;
                }
            }

            list.Add(ev);
        }

        return list;
    }

    private static bool IsApplicationChannel(ParsedEventRecord ev) =>
        string.IsNullOrEmpty(ev.Channel) || string.Equals(ev.Channel, "Application", StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemChannel(ParsedEventRecord ev) =>
        string.IsNullOrEmpty(ev.Channel) || string.Equals(ev.Channel, "System", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompatibleWerAnchor(string? werEventName, IncidentAnchorKind anchorKind)
    {
        if (string.IsNullOrWhiteSpace(werEventName))
        {
            return false;
        }

        return anchorKind switch
        {
            IncidentAnchorKind.ApplicationCrash => WerCrashCompatibleNames.Contains(werEventName),
            IncidentAnchorKind.ApplicationHang => WerHangCompatibleNames.Contains(werEventName),
            _ => false
        };
    }

    private static bool HasAppNameConflict(string? werAppName, string? anchorAppName)
    {
        if (string.IsNullOrWhiteSpace(werAppName) || string.IsNullOrWhiteSpace(anchorAppName))
        {
            return false;
        }

        var left = Path.GetFileName(werAppName.Trim());
        var right = Path.GetFileName(anchorAppName.Trim());
        return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParse(raw.Trim(), out var parsed))
        {
            return null;
        }

        var canonical = parsed.ToString("D");
        return string.Equals(canonical, ZeroGuid, StringComparison.Ordinal) ? null : canonical;
    }

    private static EvidenceItem BuildEvidence(
        string evidenceId,
        EvidenceSourceKind kind,
        ParsedEventRecord ev,
        IReadOnlyDictionary<string, string> allowedFields,
        bool HasSchemaWarning)
    {
        var location = ev.RecordId.HasValue
            ? $"{ev.Channel} / {ev.ProviderName} / ID {ev.EventId} (v{ev.Version?.ToString(CultureInfo.InvariantCulture) ?? "?"}) / RecordId {ev.RecordId.Value}"
            : $"{ev.Channel} / {ev.ProviderName} / ID {ev.EventId} (v{ev.Version?.ToString(CultureInfo.InvariantCulture) ?? "?"})";

        return new EvidenceItem(
            EvidenceId: evidenceId,
            SourceKind: kind,
            TimestampUtc: ev.TimeCreatedUtc,
            TimestampPrecision: ev.TimeCreatedUtc.HasValue ? "EventSystemTimeUtc" : "Unknown",
            SourceLocation: location,
            ParserVersion: ParserVersion,
            AllowedFields: allowedFields,
            HasSchemaWarning: HasSchemaWarning);
    }

    private static Dictionary<string, string> BuildCrashAllowedFields(NormalizedAppCrashEvent c)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfNotEmpty(d, "AppName", c.AppName);
        AddIfNotEmpty(d, "AppVersion", c.AppVersion);
        AddIfNotEmpty(d, "ModuleName", c.ModuleName);
        AddIfNotEmpty(d, "ModuleVersion", c.ModuleVersion);
        AddIfNotEmpty(d, "ExceptionCode", c.ExceptionCode);
        AddIfNotEmpty(d, "FaultingOffset", c.FaultingOffset);
        AddIfNotEmpty(d, "PathSuffix", PathClassifier.BuildDisambiguatedPathSuffix(c.AppPath));
        return d;
    }

    private static Dictionary<string, string> BuildHangAllowedFields(NormalizedAppHangEvent h)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfNotEmpty(d, "AppName", h.AppName);
        AddIfNotEmpty(d, "AppVersion", h.AppVersion);
        AddIfNotEmpty(d, "HangType", h.HangType);
        AddIfNotEmpty(d, "TerminationTimeMs", h.TerminationTimeMs);
        AddIfNotEmpty(d, "PathSuffix", PathClassifier.BuildDisambiguatedPathSuffix(h.ExeFileName));
        return d;
    }

    private static Dictionary<string, string> BuildWerAllowedFields(NormalizedWerReportEvent w)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIfNotEmpty(d, "EventName", w.EventName);
        AddIfNotEmpty(d, "AppName", Path.GetFileName(w.P1));
        AddIfNotEmpty(d, "Bucket", w.Bucket);
        return d;
    }

    private static void AddIfNotEmpty(Dictionary<string, string> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            dict[key] = value;
        }
    }

    private readonly record struct AggregationKey(
        IncidentAnchorKind AnchorKind,
        string AppNameLower,
        string NormalizedAppPathLower,
        string ModuleNameLower,
        string ExceptionCodeLower);

    private sealed class AnchorBuilder
    {
        public string IncidentId { get; }
        public IncidentAnchorKind AnchorKind { get; }
        public DateTimeOffset? OccurredAtUtc { get; }
        public string? AppName { get; }
        public string? NormalizedAppPath { get; }
        public string? ModuleName { get; }
        public string? ModulePath { get; }
        public string? ExceptionCode { get; }
        public string? FaultingProcessIdHex { get; }
        public string? ProcessCreationTimeHex { get; }
        public string? ReportId { get; }
        public string? HangType { get; }
        public bool IsSupportedSchemaVersion { get; }
        public EvidenceItem PrimaryEvidence { get; }
        public EvidenceItem? LinkedWerEvidence { get; private set; }
        public AttributionStrength WerLinkAttribution { get; private set; } = AttributionStrength.Unlinked;
        public List<BackgroundEventRecord> NearbyBackground { get; } = [];
        public List<string> AmbiguityNotes { get; } = [];

        private AnchorBuilder(
            string incidentId,
            IncidentAnchorKind anchorKind,
            DateTimeOffset? occurredAtUtc,
            string? appName,
            string? normalizedAppPath,
            string? moduleName,
            string? modulePath,
            string? exceptionCode,
            string? pidHex,
            string? procCreationHex,
            string? reportId,
            string? hangType,
            bool isSupportedSchemaVersion,
            EvidenceItem primaryEvidence)
        {
            IncidentId = incidentId;
            AnchorKind = anchorKind;
            OccurredAtUtc = occurredAtUtc;
            AppName = appName;
            NormalizedAppPath = normalizedAppPath;
            ModuleName = moduleName;
            ModulePath = modulePath;
            ExceptionCode = exceptionCode;
            FaultingProcessIdHex = pidHex;
            ProcessCreationTimeHex = procCreationHex;
            ReportId = reportId;
            HangType = hangType;
            IsSupportedSchemaVersion = isSupportedSchemaVersion;
            PrimaryEvidence = primaryEvidence;
        }

        public static AnchorBuilder FromCrash(NormalizedAppCrashEvent c, EvidenceItem evidence)
        {
            var normAppPath = PathClassifier.NormalizePath(c.AppPath);
            var normModPath = PathClassifier.NormalizePath(c.ModulePath);
            var id = $"inc-1000-{c.Source.RecordId?.ToString(CultureInfo.InvariantCulture) ?? evidence.EvidenceId}";
            return new AnchorBuilder(
                id,
                IncidentAnchorKind.ApplicationCrash,
                c.Source.TimeCreatedUtc,
                c.AppName,
                normAppPath,
                c.ModuleName,
                normModPath,
                c.ExceptionCode,
                c.FaultingProcessIdHex,
                c.ProcessCreationTimeHex,
                c.IntegratorReportId,
                null,
                c.IsSupportedVersion,
                evidence);
        }

        public static AnchorBuilder FromHang(NormalizedAppHangEvent h, EvidenceItem evidence)
        {
            var normAppPath = PathClassifier.NormalizePath(h.ExeFileName);
            var id = $"inc-1002-{h.Source.RecordId?.ToString(CultureInfo.InvariantCulture) ?? evidence.EvidenceId}";
            return new AnchorBuilder(
                id,
                IncidentAnchorKind.ApplicationHang,
                h.Source.TimeCreatedUtc,
                h.AppName,
                normAppPath,
                null,
                null,
                null,
                h.FaultingProcessIdHex,
                h.StartTimeHex,
                h.ReportId,
                h.HangType,
                h.IsSupportedVersion,
                evidence);
        }

        public void AttachWer(EvidenceItem werEvidence, AttributionStrength attribution)
        {
            LinkedWerEvidence = werEvidence;
            WerLinkAttribution = attribution;
        }

        public CrashIncident ToIncident()
        {
            var timestamps = OccurredAtUtc.HasValue
                ? (IReadOnlyList<DateTimeOffset>)[OccurredAtUtc.Value]
                : [];

            return new CrashIncident(
                IncidentId: IncidentId,
                AnchorKind: AnchorKind,
                OccurredAtUtc: OccurredAtUtc,
                AppName: AppName,
                NormalizedAppPath: NormalizedAppPath,
                DisambiguatedPathSuffix: PathClassifier.BuildDisambiguatedPathSuffix(NormalizedAppPath),
                ModuleName: ModuleName,
                ModulePath: ModulePath,
                ExceptionCode: ExceptionCode,
                FaultingProcessIdHex: FaultingProcessIdHex,
                ProcessCreationTimeHex: ProcessCreationTimeHex,
                ReportId: ReportId,
                HangType: HangType,
                IsLikelyGame: PathClassifier.IsLikelyGamePath(NormalizedAppPath),
                IsSystemNoise: PathClassifier.IsSystemNoiseProcess(AppName),
                IsSupportedSchemaVersion: IsSupportedSchemaVersion,
                OccurrenceCountInWindow: 1,
                OccurrenceTimestampsUtc: timestamps,
                WerLinkAttribution: WerLinkAttribution,
                PrimaryEvidence: PrimaryEvidence,
                LinkedWerEvidence: LinkedWerEvidence,
                NearbySystemBackground: NearbyBackground,
                ConfirmedEngineLog: null,
                AmbiguityNotes: AmbiguityNotes);
        }
    }

    private sealed class WerBuilder
    {
        public NormalizedWerReportEvent Report { get; }
        public EvidenceItem Evidence { get; }
        public string? AppNameHint { get; }
        public string? NormalizedRootedPath { get; }
        public string? FaultingPidHex { get; }
        public string? ProcessCreationTimeHex { get; }

        public WerBuilder(NormalizedWerReportEvent report, EvidenceItem evidence)
        {
            Report = report;
            Evidence = evidence;

            var named = report.Source.NamedFields;
            named.TryGetValue("AppPath", out var namedAppPath);
            named.TryGetValue("ExeFileName", out var namedExePath);

            var normNamedAppPath = PathClassifier.NormalizePath(namedAppPath);
            var normNamedExePath = PathClassifier.NormalizePath(namedExePath);
            var normP1Path = PathClassifier.NormalizePath(report.P1);

            NormalizedRootedPath = PathClassifier.IsRootedWindowsPath(normNamedAppPath) ? normNamedAppPath
                : PathClassifier.IsRootedWindowsPath(normNamedExePath) ? normNamedExePath
                : PathClassifier.IsRootedWindowsPath(normP1Path) ? normP1Path
                : null;
            AppNameHint = NormalizedRootedPath is not null
                ? Path.GetFileName(NormalizedRootedPath)
                : (!string.IsNullOrWhiteSpace(report.P1) ? Path.GetFileName(report.P1.Trim()) : null);

            named.TryGetValue("ProcessId", out var pid1);
            named.TryGetValue("FaultingProcessId", out var pid2);
            FaultingPidHex = !string.IsNullOrWhiteSpace(pid1) ? pid1.Trim()
                : !string.IsNullOrWhiteSpace(pid2) ? pid2.Trim()
                : null;

            named.TryGetValue("ProcessCreationTime", out var pct1);
            named.TryGetValue("StartTime", out var pct2);
            ProcessCreationTimeHex = !string.IsNullOrWhiteSpace(pct1) ? pct1.Trim()
                : !string.IsNullOrWhiteSpace(pct2) ? pct2.Trim()
                : null;
        }

        public CrashIncident ToStandaloneIncident(string? ambiguityNote)
        {
            var notes = ambiguityNote is null ? Array.Empty<string>() : new[] { ambiguityNote };
            var timestamps = Report.Source.TimeCreatedUtc.HasValue
                ? (IReadOnlyList<DateTimeOffset>)[Report.Source.TimeCreatedUtc.Value]
                : [];

            return new CrashIncident(
                IncidentId: $"inc-1001-{Report.Source.RecordId?.ToString(CultureInfo.InvariantCulture) ?? Evidence.EvidenceId}",
                AnchorKind: IncidentAnchorKind.WerReportOnly,
                OccurredAtUtc: Report.Source.TimeCreatedUtc,
                AppName: AppNameHint,
                NormalizedAppPath: NormalizedRootedPath,
                DisambiguatedPathSuffix: PathClassifier.BuildDisambiguatedPathSuffix(NormalizedRootedPath),
                ModuleName: !string.IsNullOrWhiteSpace(Report.P4) ? Report.P4 : null,
                ModulePath: null,
                ExceptionCode: WindowsEventNormalizer.NormalizeHexCode(Report.P7),
                FaultingProcessIdHex: FaultingPidHex,
                ProcessCreationTimeHex: ProcessCreationTimeHex,
                ReportId: Report.ReportId,
                HangType: null,
                IsLikelyGame: PathClassifier.IsLikelyGamePath(NormalizedRootedPath),
                IsSystemNoise: PathClassifier.IsSystemNoiseProcess(AppNameHint),
                IsSupportedSchemaVersion: Report.IsSupportedVersion,
                OccurrenceCountInWindow: 1,
                OccurrenceTimestampsUtc: timestamps,
                WerLinkAttribution: AttributionStrength.Unlinked,
                PrimaryEvidence: Evidence,
                LinkedWerEvidence: null,
                NearbySystemBackground: [],
                ConfirmedEngineLog: null,
                AmbiguityNotes: notes);
        }
    }
}
