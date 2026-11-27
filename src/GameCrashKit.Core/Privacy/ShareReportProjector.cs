using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Privacy;

/// <summary>
/// Projects a <see cref="DiagnosisReport"/> into an immutable, strictly whitelisted <see cref="ShareReportModel"/>.
/// </summary>
public static partial class ShareReportProjector
{
    [GeneratedRegex(@"(?<ver>\d+\.\d+(?:\.\d+)?(?:[fpab]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 150)]
    private static partial Regex SafeNumericVersionRegex();
    public static ShareReportModel Project(
        DiagnosisReport report,
        ShareOptions options,
        ScrubContext scrubContext,
        TimeZoneInfo? displayTimeZone = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scrubContext);

        var tz = displayTimeZone ?? TimeZoneInfo.Local;
        var inc = report.SelectedIncident;
        var activeLog = inc?.ConfirmedEngineLog ?? report.StandaloneLog;
        var isStandaloneLog = report.SubjectKind == ReportSubjectKind.StandaloneLogOnly || (inc is null && report.StandaloneLog is not null);

        string incidentKindLabel;
        string appDisplayName;
        string installSource;
        string moduleName;
        string exceptionCode;
        string occurrenceSummary;
        string timestampDisplay;

        if (isStandaloneLog && activeLog is not null)
        {
            incidentKindLabel = "来源：手选日志（独立诊断）";
            appDisplayName = options.HideAppName ? "<已隐藏>" : activeLog.EffectiveFormatLabel;
            var fatalityText = activeLog.Fatality switch
            {
                LogFatalityKind.Fatal => "致命崩溃/报错",
                LogFatalityKind.NonFatalEnsure => "非致命 Ensure",
                LogFatalityKind.ChildProcessExit => "子进程异常退出（主进程退出未证实）",
                _ => "进程退出未证实"
            };
            var winState = string.IsNullOrWhiteSpace(report.WindowsScanStateLabel) ? "未扫描" : report.WindowsScanStateLabel;
            installSource = $"致命性: {fatalityText} | Windows: {winState}";
            moduleName = "不适用（无 Windows 事件锚点）";
            var tokens = activeLog.EffectiveSignals.Select(static s => s.MatchedToken).Where(static t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToArray();
            exceptionCode = tokens.Length > 0 ? string.Join(" / ", tokens) : "未命中固定错误标记";
            occurrenceSummary = activeLog.IsTruncated
                ? "事故时间：未知（仅检查尾段 2 MiB）"
                : "事故时间：未知（仅手选日志）";
            timestampDisplay = activeLog.FileLastWriteUtc.HasValue
                ? "文件修改时间：" + FormatTimestamp(activeLog.FileLastWriteUtc, options.HideExactTimestamp, tz, report.GeneratedAtUtc)
                : "文件修改时间：未知";
        }
        else
        {
            incidentKindLabel = inc?.AnchorKind switch
            {
                IncidentAnchorKind.ApplicationCrash => "应用异常退出 (Event 1000)",
                IncidentAnchorKind.ApplicationHang => "应用无响应 (Event 1002)",
                IncidentAnchorKind.WerReportOnly => "仅有 WER 错误报告 (Event 1001)",
                _ => "未选中应用事故（证据不足）"
            };

            appDisplayName = inc is null
                ? "未定位程序"
                : options.HideAppName
                    ? "<APP>"
                    : PrivacyScrubber.SanitizeIdentifierValue(inc.AppName, scrubContext, 64, "未知程序");

            installSource = inc is null
                ? "无"
                : options.HideAppName
                    ? "<已隐藏>"
                    : PathClassifier.BuildShareSafeInstallSource(inc.NormalizedAppPath, inc.IsLikelyGame);

            moduleName = inc is null
                ? "无"
                : options.HideAppName && IsSameAppModule(inc.ModuleName, inc.AppName)
                    ? "<APP_MODULE>"
                    : PrivacyScrubber.SanitizeIdentifierValue(inc.ModuleName, scrubContext, 64, "未知模块");

            exceptionCode = inc is null
                ? "无"
                : PrivacyScrubber.SanitizeIdentifierValue(inc.ExceptionCode, scrubContext, 32, inc.AnchorKind == IncidentAnchorKind.ApplicationHang ? "不适用（无响应）" : "未知");

            occurrenceSummary = inc is null
                ? "0 次"
                : $"所选扫描窗内共 {inc.OccurrenceCountInWindow} 次";

            timestampDisplay = FormatTimestamp(inc?.OccurredAtUtc, options.HideExactTimestamp, tz, report.GeneratedAtUtc);
        }

        var formatLabel = activeLog?.EffectiveFormatLabel;
        var findings = report.PrimaryFindings
            .Select(f => ProjectFinding(f, inc?.AppName, formatLabel, options.HideAppName, options.HideExactTimestamp, scrubContext))
            .ToList();

        var additionalHints = (report.AdditionalHints ?? [])
            .Select(f => ProjectFinding(f, inc?.AppName, formatLabel, options.HideAppName, options.HideExactTimestamp, scrubContext))
            .ToList();

        var backgroundLines = report.SystemBackgroundEvents
            .Take(4)
            .Select(bg => FormatBackgroundLine(bg, options.HideExactTimestamp, tz))
            .ToList();

        var envFacts = ProjectEnvironmentFacts(report.Environment, scrubContext);

        var coverageLines = report.Coverage
            .Select(c =>
            {
                var sourceName = PrivacyScrubber.ScrubText(c.SourceName, scrubContext, scrubIpv4: true);
                var statusText = ToCoverageStatusText(c.Status);
                var extraTruncated = c.Truncated && c.Status != SourceCoverageStatus.Truncated
                    ? "，已截断"
                    : string.Empty;
                return $"{sourceName}: {statusText}（读取 {c.RecordsRead} 条{extraTruncated}）";
            })
            .ToList();

        var scanScopeSummary = BuildScanScopeSummary(report, isStandaloneLog);
        var limitationNotes = BuildSourceLimitationNotes(report, activeLog, isStandaloneLog, scrubContext);

        var signalTemplates = BuildFixedSignalSummaryLines(activeLog, options.HideAppName);
        var hasAvailableExcerptSource = signalTemplates.Count > 0;
        var scrubbedExcerpts = (options.IncludeScrubbedErrorExcerpt && hasAvailableExcerptSource)
            ? signalTemplates
                .Select(line => ScrubHiddenIdentities(
                    PrivacyScrubber.ScrubText(line, scrubContext, scrubIpv4: true, hideTimestamps: options.HideExactTimestamp),
                    inc?.AppName,
                    formatLabel,
                    options.HideAppName))
                .ToList()
            : [];
        var hasOptInExcerpt = scrubbedExcerpts.Count > 0;

        var privacyBadge = hasOptInExcerpt
            ? "已脱敏 · 字段白名单 + 固定结构化日志信号摘要"
            : "已脱敏 · 严格字段白名单（无原始日志/路径/标识）";

        return new ShareReportModel(
            SchemaVersion: report.SchemaVersion,
            AppVersion: report.AppVersion,
            RulesetVersion: report.RulesetVersion,
            PrivacyBadgeText: privacyBadge,
            IncidentKindLabel: incidentKindLabel,
            AppDisplayName: appDisplayName,
            DisambiguatedPathSuffix: installSource,
            ModuleName: moduleName,
            ExceptionCode: exceptionCode,
            OccurrenceSummary: occurrenceSummary,
            TimestampDisplay: timestampDisplay,
            PrimaryFindings: findings,
            BackgroundSummaryLines: backgroundLines,
            EnvironmentFacts: envFacts,
            CoverageSummaryLines: coverageLines,
            HasAvailableExcerptSource: hasAvailableExcerptSource,
            HasOptInExcerpt: hasOptInExcerpt,
            OptInScrubbedExcerptLines: scrubbedExcerpts,
            AdditionalHints: additionalHints,
            ScanScopeSummary: scanScopeSummary,
            SourceLimitationNotes: limitationNotes);
    }

    private static string BuildScanScopeSummary(DiagnosisReport report, bool isStandaloneLog)
    {
        if (report.Coverage.Count == 0)
        {
            return isStandaloneLog
                ? "Windows 事件：未扫描（仅手选日志独立诊断）"
                : "Windows 事件：未扫描";
        }

        var duration = report.ScanRequest.WindowEndUtc - report.ScanRequest.WindowStartUtc;
        var windowLabel = duration.TotalHours <= 2.5
            ? "最近 2 小时"
            : duration.TotalHours <= 25.0
                ? "最近 24 小时"
                : duration.TotalDays <= 7.5
                    ? "最近 7 天"
                    : $"{Math.Max(1, (int)Math.Round(duration.TotalHours))} 小时";

        var filterLabel = report.ScanRequest.IncludeAllApplications ? "全部应用事件" : "游戏优先视图";
        var capSuffix = report.ScanRequest.MaxEventsPerChannel > 0
            ? $"，每频道上限 {report.ScanRequest.MaxEventsPerChannel} 条"
            : string.Empty;

        var scopeText = $"Windows 扫描窗：{windowLabel}（{filterLabel}{capSuffix}）";
        return isStandaloneLog ? $"{scopeText} ｜ 当前视图：独立日志诊断" : scopeText;
    }

    private static string ToCoverageStatusText(SourceCoverageStatus status) => status switch
    {
        SourceCoverageStatus.Ok => "正常",
        SourceCoverageStatus.Truncated => "已截断（达到每频道上限）",
        SourceCoverageStatus.PartialTimeout => "超时降级（超过 10 秒软超时）",
        SourceCoverageStatus.AccessDenied => "权限受限（标准权限不可读）",
        SourceCoverageStatus.NotFound => "频道不存在",
        SourceCoverageStatus.Cancelled => "已取消",
        SourceCoverageStatus.Skipped => "已跳过",
        SourceCoverageStatus.Failed => "读取失败",
        _ => "未知状态"
    };

    private static IReadOnlyList<string> BuildSourceLimitationNotes(
        DiagnosisReport report,
        UserConfirmedEngineLog? activeLog,
        bool isStandaloneLog,
        ScrubContext scrubContext)
    {
        var notes = new List<string>();

        foreach (var cov in report.Coverage)
        {
            if (cov.Status != SourceCoverageStatus.Ok || cov.Truncated)
            {
                var safeSource = PrivacyScrubber.ScrubText(cov.SourceName, scrubContext, scrubIpv4: true);
                var statusText = cov.Status == SourceCoverageStatus.Ok
                    ? "已截断（达到每频道上限）"
                    : ToCoverageStatusText(cov.Status);
                notes.Add($"{safeSource}：{statusText}（结论可能不完整，可调整扫描窗复查）");
            }
        }

        if (isStandaloneLog && report.Coverage.Count == 0)
        {
            notes.Add("未执行 Windows 事件扫描，报告仅反映手选文件内容，不含系统级进程/故障模块锚点。");
        }

        if (activeLog is { IsTruncated: true })
        {
            notes.Add("手选日志超出 2 MiB 只读上限，仅检查尾段 2 MiB，更早报错未纳入分析。");
        }

        if (activeLog is { MayBeOverwritten: true })
        {
            notes.Add("手选日志修改时间接近或晚于后续运行，可能已被程序重开覆盖。");
        }

        if (report.Environment is { RunningOverlays.Checked: false })
        {
            notes.Add("常见覆盖层瞬时快照探测受限或不可用。");
        }

        return notes;
    }

    private static ShareFindingItem ProjectFinding(
        DiagnosisFinding f,
        string? appName,
        string? formatLabel,
        bool hideAppName,
        bool hideTimestamps,
        ScrubContext scrubContext)
    {
        var title = ScrubHiddenIdentities(
            PrivacyScrubber.ScrubText(f.Title, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
            appName,
            formatLabel,
            hideAppName);
        var statement = ScrubHiddenIdentities(
            PrivacyScrubber.ScrubText(f.Statement, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
            appName,
            formatLabel,
            hideAppName);
        var nextAction = ScrubHiddenIdentities(
            PrivacyScrubber.ScrubText(f.NextActionText, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
            appName,
            formatLabel,
            hideAppName);
        var notes = f.AmbiguityNotes
            .Select(n => ScrubHiddenIdentities(
                PrivacyScrubber.ScrubText(n, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
                appName,
                formatLabel,
                hideAppName))
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        return new ShareFindingItem(
            RuleTag: $"{f.RuleId}@{f.RuleVersion}",
            Title: title,
            AttributionText: $"归属: {f.Attribution.ToDisplayText()}",
            EvidenceStrengthText: $"证据: {f.EvidenceStrength.ToDisplayText()}",
            EvidenceStrength: f.EvidenceStrength,
            Statement: statement,
            NextActionText: nextAction,
            AmbiguityNotes: notes);
    }

    private static IReadOnlyList<string> BuildFixedSignalSummaryLines(UserConfirmedEngineLog? log, bool hideAppName = false)
    {
        if (log is null)
        {
            return [];
        }

        var safeVersion = ExtractShareSafeNumericVersion(log.EngineVersion);
        var safeRhi = NormalizeShareSafeRhi(log.RhiBackend);
        var fatalityLabel = log.Fatality switch
        {
            LogFatalityKind.Fatal => "致命错误/崩溃",
            LogFatalityKind.NonFatalEnsure => "非致命 Ensure（未证实崩溃）",
            LogFatalityKind.ChildProcessExit => "子进程异常退出（主进程退出未证实）",
            _ => "进程退出未证实"
        };

        var formatLabelText = hideAppName ? "<已隐藏>" : log.EffectiveFormatLabel;
        var header = $"日志类型: {formatLabelText} | 致命性: {fatalityLabel}";
        if (safeVersion is not null)
        {
            header += $" | 规范版本号: {safeVersion}";
        }

        var lines = new List<string> { header };

        if (safeRhi is not null)
        {
            lines.Add($"图形渲染后端 (RHI): {safeRhi}");
        }

        var effectiveSignals = log.EffectiveSignals;
        foreach (var sig in effectiveSignals)
        {
            var label = sig.SignalId switch
            {
                LogSignalId.GpuDeviceLost => "命中固定图形错误标记",
                LogSignalId.OutOfMemory => "命中固定资源不足标记",
                LogSignalId.AccessViolation => "命中固定内存访问异常标记",
                _ => "命中固定错误标记"
            };
            lines.Add($"{label}: {sig.MatchedToken}");
        }

        if (effectiveSignals.Count == 0)
        {
            lines.Add("固定错误标记扫描: 未命中已知图形丢失、资源耗尽或内存访问异常固定标记（原始日志全文限本机查看）");
        }

        if (log.IsTruncated || log.MayBeOverwritten)
        {
            var flags = new List<string>();
            if (log.IsTruncated) flags.Add("仅检查尾段 2 MiB");
            if (log.MayBeOverwritten) flags.Add("文件可能受后续重开覆盖");
            lines.Add($"日志读取状态提示: {string.Join("；", flags)}");
        }

        return lines;
    }

    private static string? ExtractShareSafeNumericVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return null;
        }

        var m = SafeNumericVersionRegex().Match(rawVersion.Trim());
        return m.Success ? m.Groups["ver"].Value : null;
    }

    private static string? NormalizeShareSafeRhi(string? rawRhi)
    {
        if (string.IsNullOrWhiteSpace(rawRhi))
        {
            return null;
        }

        var r = rawRhi.Trim();
        if (r.Contains("Direct3D 12", StringComparison.OrdinalIgnoreCase) || r.Contains("D3D12", StringComparison.OrdinalIgnoreCase))
        {
            return "Direct3D 12";
        }

        if (r.Contains("Direct3D 11", StringComparison.OrdinalIgnoreCase) || r.Contains("D3D11", StringComparison.OrdinalIgnoreCase))
        {
            return "Direct3D 11";
        }

        if (r.Contains("Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return "Vulkan";
        }

        if (r.Contains("OpenGL", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenGL";
        }

        return null;
    }

    private static bool IsSameAppModule(string? moduleName, string? appName)
    {
        if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        var mod = moduleName.Trim();
        var app = appName.Trim();
        if (string.Equals(mod, app, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var modStem = Path.GetFileNameWithoutExtension(mod);
        var appStem = Path.GetFileNameWithoutExtension(app);
        return appStem.Length >= 3 && string.Equals(modStem, appStem, StringComparison.OrdinalIgnoreCase);
    }

    private static string ScrubHiddenIdentities(string text, string? appName, string? formatLabel, bool hideAppName)
    {
        if (!hideAppName)
        {
            return text;
        }

        var result = text;
        if (!string.IsNullOrWhiteSpace(formatLabel))
        {
            result = result.Replace(formatLabel.Trim(), "<已隐藏>", StringComparison.OrdinalIgnoreCase);
        }

        ReadOnlySpan<string> knownAppLabels = ["OBS Studio", "Blender", "ComfyUI", "Unity", "Unreal"];
        foreach (var label in knownAppLabels)
        {
            result = result.Replace(label, "<已隐藏>", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(appName))
        {
            var trimmed = appName.Trim();
            if (trimmed.Length >= 2)
            {
                result = result.Replace(trimmed, "<APP>", StringComparison.OrdinalIgnoreCase);
            }

            var stem = Path.GetFileNameWithoutExtension(trimmed);
            if (stem.Length >= 3)
            {
                result = result.Replace(stem, "<APP>", StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    private static string FormatTimestamp(
        DateTimeOffset? occurredAtUtc,
        bool hideExact,
        TimeZoneInfo tz,
        DateTimeOffset referenceUtc)
    {
        if (!occurredAtUtc.HasValue)
        {
            return "未知时间";
        }

        var localTime = TimeZoneInfo.ConvertTime(occurredAtUtc.Value, tz);
        if (!hideExact)
        {
            return localTime.ToString("yyyy-MM-dd HH:mm:ss (zzz)", CultureInfo.InvariantCulture);
        }

        var delta = referenceUtc - occurredAtUtc.Value;
        var relative = delta.TotalMinutes < 1
            ? "约 1 分钟内"
            : delta.TotalHours < 1
                ? $"约 {(int)Math.Round(delta.TotalMinutes)} 分钟前"
                : delta.TotalDays < 1
                    ? $"约 {(int)Math.Round(delta.TotalHours)} 小时前"
                    : $"约 {(int)Math.Round(delta.TotalDays)} 天前";

        return $"{localTime:yyyy-MM-dd}（{relative}，已隐藏精确时间）";
    }

    private static string FormatBackgroundLine(BackgroundEventRecord bg, bool hideExact, TimeZoneInfo tz)
    {
        var timePart = bg.Evidence.TimestampUtc.HasValue
            ? hideExact
                ? TimeZoneInfo.ConvertTime(bg.Evidence.TimestampUtc.Value, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : TimeZoneInfo.ConvertTime(bg.Evidence.TimestampUtc.Value, tz).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "未知时间";

        return bg.SummaryKind == "KernelPower41"
            ? $"[{timePart}] (仅系统背景) Kernel-Power 41（BugcheckCode={bg.BugcheckCode ?? 0}，开机后补记）"
            : $"[{timePart}] (仅系统背景) {bg.SummaryKind}";
    }

    private static IReadOnlyList<ShareEnvironmentItem> ProjectEnvironmentFacts(EnvironmentSnapshot? env, ScrubContext scrubContext)
    {
        if (env is null)
        {
            return [];
        }

        var items = new List<ShareEnvironmentItem>();

        var gpuText = env.GpuAdapters.Count == 0
            ? "未检测到适配器"
            : string.Join("；", env.GpuAdapters.Select(g =>
                $"{PrivacyScrubber.SanitizeStructuredValue(g.Name, scrubContext, 48, "GPU")} ({PrivacyScrubber.SanitizeStructuredValue(g.DriverVersion, scrubContext, 24, "未知")} / 驱动包日期 {g.DriverReleaseDateIso ?? "未知"})"));
        items.Add(new ShareEnvironmentItem("显卡与驱动包日期", gpuText, "信息", EnvironmentNoticeLevel.Info));

        var pathText = !env.TargetPath.Checked
            ? "未选中目标程序"
            : $"非 ASCII: {(env.TargetPath.HasNonAscii ? "是" : "否")} | 长度 >260: {(env.TargetPath.ExceedsMaxPath260 ? "是" : "否")}";
        var pathLevel = env.TargetPath.Checked && (env.TargetPath.HasNonAscii || env.TargetPath.ExceedsMaxPath260)
            ? EnvironmentNoticeLevel.Attention
            : EnvironmentNoticeLevel.Info;
        items.Add(new ShareEnvironmentItem("目标路径特征", pathText, pathLevel == EnvironmentNoticeLevel.Attention ? "留意" : "信息", pathLevel));

        items.Add(new ShareEnvironmentItem(
            "文档目录 OneDrive",
            PrivacyScrubber.ScrubText(env.OneDriveDocs.StatusText, scrubContext, scrubIpv4: true),
            ToNoticeLabel(env.OneDriveDocs.NoticeLevel),
            env.OneDriveDocs.NoticeLevel));

        var vcMissingText = env.VcRuntime.NoticeLevel == EnvironmentNoticeLevel.Unavailable ? "探测不可用" : "未检测到登记";
        var vcText = $"x64: {(env.VcRuntime.X64Installed ? env.VcRuntime.X64Version ?? "已装" : vcMissingText)} | x86: {(env.VcRuntime.X86Installed ? env.VcRuntime.X86Version ?? "已装" : vcMissingText)}";
        items.Add(new ShareEnvironmentItem("VC++ 运行库", vcText, ToNoticeLabel(env.VcRuntime.NoticeLevel), env.VcRuntime.NoticeLevel));

        var pfText = !env.PageFile.Checked
            ? "探测不可用"
            : env.PageFile.IsDisabled
                ? "未检测到活跃配置"
                : $"当前已分配 {env.PageFile.TotalAllocatedMb} MB（{env.PageFile.PageFileCount} 个）";
        items.Add(new ShareEnvironmentItem("虚拟内存页面文件", pfText, ToNoticeLabel(env.PageFile.NoticeLevel), env.PageFile.NoticeLevel));

        var overlayText = !env.RunningOverlays.Checked
            ? "扫描受限或不可用"
            : env.RunningOverlays.DetectedOverlayNames.Count == 0
                ? "扫描瞬间未发现常见覆盖层"
                : $"扫描瞬间运行: {string.Join("、", env.RunningOverlays.DetectedOverlayNames)}";
        items.Add(new ShareEnvironmentItem("常见覆盖层快照", overlayText, env.RunningOverlays.Checked ? "信息" : "不可用", env.RunningOverlays.Checked ? EnvironmentNoticeLevel.Info : EnvironmentNoticeLevel.Unavailable));

        var hvciText = env.SystemInfo.HvciEnabled switch { true => "开", false => "关", null => "未知" };
        var sysText = $"{env.SystemInfo.OsDisplayLabel} | RAM {env.SystemInfo.TotalPhysicalMemoryMb} MB | ACP {env.SystemInfo.AcpCodePage} | HVCI {hvciText}";
        items.Add(new ShareEnvironmentItem("系统与区域设置", sysText, "信息", EnvironmentNoticeLevel.Info));

        return items;
    }

    private static string ToNoticeLabel(EnvironmentNoticeLevel level) => level switch
    {
        EnvironmentNoticeLevel.Attention => "留意",
        EnvironmentNoticeLevel.Unavailable => "不可用",
        _ => "信息"
    };
}
