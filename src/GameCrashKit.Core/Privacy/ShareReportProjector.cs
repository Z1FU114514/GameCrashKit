using System.Globalization;
using System.IO;
using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Privacy;

/// <summary>
/// Projects a <see cref="DiagnosisReport"/> into an immutable, strictly whitelisted <see cref="ShareReportModel"/>.
/// </summary>
public static class ShareReportProjector
{
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

        var incidentKindLabel = inc?.AnchorKind switch
        {
            IncidentAnchorKind.ApplicationCrash => "应用异常退出 (Event 1000)",
            IncidentAnchorKind.ApplicationHang => "应用无响应 (Event 1002)",
            IncidentAnchorKind.WerReportOnly => "仅有 WER 错误报告 (Event 1001)",
            _ => "未选中应用事故（证据不足）"
        };

        var appDisplayName = inc is null
            ? "未定位程序"
            : options.HideAppName
                ? "<APP>"
                : PrivacyScrubber.SanitizeIdentifierValue(inc.AppName, scrubContext, 64, "未知程序");

        var installSource = inc is null
            ? "无"
            : options.HideAppName
                ? "<已隐藏>"
                : PathClassifier.BuildShareSafeInstallSource(inc.NormalizedAppPath, inc.IsLikelyGame);

        var moduleName = inc is null
            ? "无"
            : options.HideAppName && IsSameAppModule(inc.ModuleName, inc.AppName)
                ? "<APP_MODULE>"
                : PrivacyScrubber.SanitizeIdentifierValue(inc.ModuleName, scrubContext, 64, "未知模块");

        var exceptionCode = inc is null
            ? "无"
            : PrivacyScrubber.SanitizeIdentifierValue(inc.ExceptionCode, scrubContext, 32, inc.AnchorKind == IncidentAnchorKind.ApplicationHang ? "不适用（无响应）" : "未知");

        var occurrenceSummary = inc is null
            ? "0 次"
            : $"所选扫描窗内共 {inc.OccurrenceCountInWindow} 次";

        var timestampDisplay = FormatTimestamp(inc?.OccurredAtUtc, options.HideExactTimestamp, tz, report.GeneratedAtUtc);

        var findings = report.PrimaryFindings
            .Select(f => ProjectFinding(f, inc?.AppName, options.HideAppName, options.HideExactTimestamp, scrubContext))
            .ToList();

        var additionalHints = (report.AdditionalHints ?? [])
            .Select(f => ProjectFinding(f, inc?.AppName, options.HideAppName, options.HideExactTimestamp, scrubContext))
            .ToList();

        var backgroundLines = report.SystemBackgroundEvents
            .Take(4)
            .Select(bg => FormatBackgroundLine(bg, options.HideExactTimestamp, tz))
            .ToList();

        var envFacts = ProjectEnvironmentFacts(report.Environment, scrubContext);

        var coverageLines = report.Coverage
            .Select(c => $"{c.SourceName}: {c.Status}（读取 {c.RecordsRead} 条{(c.Truncated ? "，已截断" : string.Empty)}）")
            .ToList();

        var signalTemplates = BuildFixedSignalSummaryLines(inc?.ConfirmedEngineLog);
        var hasAvailableExcerptSource = signalTemplates.Count > 0;
        var scrubbedExcerpts = (options.IncludeScrubbedErrorExcerpt && hasAvailableExcerptSource)
            ? signalTemplates
                .Select(line => ScrubFindingAppNameIfHidden(
                    PrivacyScrubber.ScrubText(line, scrubContext, scrubIpv4: true, hideTimestamps: options.HideExactTimestamp),
                    inc?.AppName,
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
            AdditionalHints: additionalHints);
    }

    private static ShareFindingItem ProjectFinding(
        DiagnosisFinding f,
        string? appName,
        bool hideAppName,
        bool hideTimestamps,
        ScrubContext scrubContext)
    {
        var title = ScrubFindingAppNameIfHidden(
            PrivacyScrubber.ScrubText(f.Title, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
            appName,
            hideAppName);
        var statement = ScrubFindingAppNameIfHidden(
            PrivacyScrubber.ScrubText(f.Statement, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
            appName,
            hideAppName);
        var nextAction = ScrubFindingAppNameIfHidden(
            PrivacyScrubber.ScrubText(f.NextActionText, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
            appName,
            hideAppName);
        var notes = f.AmbiguityNotes
            .Select(n => ScrubFindingAppNameIfHidden(
                PrivacyScrubber.ScrubText(n, scrubContext, scrubIpv4: true, hideTimestamps: hideTimestamps),
                appName,
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

    private static IReadOnlyList<string> BuildFixedSignalSummaryLines(UserConfirmedEngineLog? log)
    {
        if (log is null)
        {
            return [];
        }

        var lines = new List<string>
        {
            $"日志类型: {log.Kind}" + (string.IsNullOrWhiteSpace(log.EngineVersion) ? string.Empty : $" | 引擎版本: {log.EngineVersion.Trim()}")
        };

        if (!string.IsNullOrWhiteSpace(log.RhiBackend))
        {
            lines.Add($"图形渲染后端 (RHI): {log.RhiBackend.Trim()}");
        }

        if (log.HasGpuDeviceLostSignal)
        {
            lines.Add($"命中固定图形错误标记: {log.GpuLostToken ?? "GPU_DEVICE_LOST"}");
        }

        if (log.HasOutOfMemorySignal)
        {
            lines.Add($"命中固定资源不足标记: {log.OomToken ?? "OUT_OF_MEMORY"}");
        }

        if (!log.HasGpuDeviceLostSignal && !log.HasOutOfMemorySignal)
        {
            lines.Add("固定错误标记扫描: 未命中已知图形丢失或资源耗尽固定标记（原始日志全文限本机查看）");
        }

        if (log.IsTruncated || log.MayBeOverwritten)
        {
            var flags = new List<string>();
            if (log.IsTruncated) flags.Add("超过 2 MiB 仅解析文件末尾");
            if (log.MayBeOverwritten) flags.Add("文件可能受后续重开覆盖");
            lines.Add($"日志读取状态提示: {string.Join("；", flags)}");
        }

        return lines;
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

    private static string ScrubFindingAppNameIfHidden(string text, string? appName, bool hideAppName)
    {
        if (!hideAppName || string.IsNullOrWhiteSpace(appName))
        {
            return text;
        }

        var trimmed = appName.Trim();
        var result = text;
        if (trimmed.Length >= 2)
        {
            result = result.Replace(trimmed, "<APP>", StringComparison.OrdinalIgnoreCase);
        }

        var stem = Path.GetFileNameWithoutExtension(trimmed);
        if (stem.Length >= 3)
        {
            result = result.Replace(stem, "<APP>", StringComparison.OrdinalIgnoreCase);
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
