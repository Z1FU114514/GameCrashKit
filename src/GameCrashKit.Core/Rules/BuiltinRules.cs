using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Rules;

/// <summary>
/// Versioned pure C# rule implementations for v0.1.
/// Every finding carries independent AttributionStrength and EvidenceStrength scales,
/// never fabricates probabilities, and never recommends DDU or disabling HVCI.
/// </summary>
public static class BuiltinRules
{
    public static IReadOnlyList<IDiagnosisRule> All { get; } =
    [
        new AppCrashRule(),
        new AppHangRule(),
        new WerReportRule(),
        new ExceptionAccessViolationRule(),
        new EngineGpuLostRule(),
        new EngineOomRule(),
        new ThirdPartyModuleHintRule(),
        new RuntimeModuleHintRule(),
        new SystemContextRule(),
        new UnknownFallbackRule()
    ];

    private static readonly HashSet<string> GenericOsSystemModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll.dll",
        "KERNELBASE.dll",
        "kernel32.dll",
        "combase.dll",
        "user32.dll",
        "win32u.dll",
        "msvcrt.dll"
    };

    private static readonly string[] ExplicitOverlayHookPrefixes =
    [
        "RTSSHooks",
        "DiscordHook",
        "OverlayVulkan",
        "GameOverlayRenderer",
        "EOSOVH-",
        "SpecialK",
        "ReShade"
    ];

    private static readonly HashSet<string> ProxyModuleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dinput8.dll",
        "version.dll"
    };

    private static readonly HashSet<string> VcRuntimeModuleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "concrt140.dll",
        "vcomp140.dll",
        "ucrtbase.dll"
    };

    public sealed class AppCrashRule : IDiagnosisRule
    {
        public string RuleId => "APP-CRASH";
        public string RuleVersion => "1";
        public int SpecificityRank => 20;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null || inc.AnchorKind != IncidentAnchorKind.ApplicationCrash)
            {
                return null;
            }

            var hasSchemaIssue = !inc.IsSupportedSchemaVersion || string.IsNullOrWhiteSpace(inc.AppName);
            var strength = hasSchemaIssue ? EvidenceStrength.LimitedClue : EvidenceStrength.ExplicitRecord;

            var appLabel = string.IsNullOrWhiteSpace(inc.AppName) ? "未知程序" : inc.AppName;
            var modLabel = string.IsNullOrWhiteSpace(inc.ModuleName) ? "未知模块" : inc.ModuleName;
            var codeLabel = string.IsNullOrWhiteSpace(inc.ExceptionCode) ? "未知异常码" : inc.ExceptionCode;

            var isGenericOsMod = !string.IsNullOrWhiteSpace(inc.ModuleName) && GenericOsSystemModules.Contains(inc.ModuleName);
            var statement = isGenericOsMod
                ? $"Windows 记录到 {appLabel} 异常退出，故障落点位于通用系统模块 {modLabel}（异常码 {codeLabel}）。仅凭通用系统模块不能直接认定系统文件损坏，它通常是上层调用异常的最终落点。"
                : $"Windows 记录到 {appLabel} 异常退出，记录的故障模块为 {modLabel}（异常码 {codeLabel}）。";

            var evidenceIds = new List<string> { inc.PrimaryEvidence.EvidenceId };
            if (inc.LinkedWerEvidence is not null)
            {
                evidenceIds.Add(inc.LinkedWerEvidence.EvidenceId);
            }

            var ambiguity = new List<string>(inc.AmbiguityNotes);
            if (!inc.IsSupportedSchemaVersion)
            {
                ambiguity.Add("该事件 XML 版本超出已验证范围，部分字段可能缺失。");
            }

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: "Windows 记录到应用故障 (Event 1000)",
                Statement: statement,
                Attribution: AttributionStrength.Direct,
                EvidenceStrength: strength,
                EvidenceIds: evidenceIds,
                AmbiguityNotes: ambiguity,
                NextActionTemplateId: "CHECK_APP_CRASH_CONTEXT",
                NextActionText: "请先核对崩溃程序名与退出时间是否与本次闪退一致；若附带或手选游戏引擎日志，可补充更具体的报错上下文后再导出急诊单求助。",
                OfficialReferenceLabel: "Microsoft 应用崩溃排查说明",
                OfficialReferenceUrl: "https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior",
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class AppHangRule : IDiagnosisRule
    {
        public string RuleId => "APP-HANG";
        public string RuleVersion => "1";
        public int SpecificityRank => 20;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null || inc.AnchorKind != IncidentAnchorKind.ApplicationHang)
            {
                return null;
            }

            var hasSchemaIssue = !inc.IsSupportedSchemaVersion || string.IsNullOrWhiteSpace(inc.AppName);
            var strength = hasSchemaIssue ? EvidenceStrength.LimitedClue : EvidenceStrength.ExplicitRecord;
            var appLabel = string.IsNullOrWhiteSpace(inc.AppName) ? "未知程序" : inc.AppName;
            var hangType = string.IsNullOrWhiteSpace(inc.HangType) ? "未指明类型" : inc.HangType;

            var evidenceIds = new List<string> { inc.PrimaryEvidence.EvidenceId };
            if (inc.LinkedWerEvidence is not null)
            {
                evidenceIds.Add(inc.LinkedWerEvidence.EvidenceId);
            }

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: "Windows 记录到应用无响应 (Event 1002)",
                Statement: $"Windows 记录到 {appLabel} 发生无响应卡死（HangType: {hangType}），并非直接内存崩溃退出。",
                Attribution: AttributionStrength.Direct,
                EvidenceStrength: strength,
                EvidenceIds: evidenceIds,
                AmbiguityNotes: inc.AmbiguityNotes,
                NextActionTemplateId: "RECORD_HANG_STAGE",
                NextActionText: "记录无响应发生在启动加载、切屏还是特定关卡期间，以及大概等待了多久，可补充对应游戏日志文件以便进一步排查。",
                OfficialReferenceLabel: null,
                OfficialReferenceUrl: null,
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class WerReportRule : IDiagnosisRule
    {
        public string RuleId => "WER-REPORT";
        public string RuleVersion => "1";
        public int SpecificityRank => 15;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null)
            {
                return null;
            }

            if (inc.AnchorKind == IncidentAnchorKind.WerReportOnly)
            {
                var eventName = inc.PrimaryEvidence.AllowedFields.GetValueOrDefault("EventName", "未知类型");
                var appLabel = string.IsNullOrWhiteSpace(inc.AppName) ? "未知程序" : inc.AppName;
                return new DiagnosisFinding(
                    RuleId: RuleId,
                    RuleVersion: RuleVersion,
                    Title: "仅发现 Windows 错误报告 (Event 1001)",
                    Statement: $"Windows 生成了一条 {eventName} 错误报告（涉及 {appLabel}），但扫描窗内未匹配到对应的 Event 1000 故障或 1002 无响应主事件。",
                    Attribution: AttributionStrength.Direct,
                    EvidenceStrength: EvidenceStrength.ExplicitRecord,
                    EvidenceIds: [inc.PrimaryEvidence.EvidenceId],
                    AmbiguityNotes: inc.AmbiguityNotes,
                    NextActionTemplateId: "CHECK_WER_ONLY",
                    NextActionText: "若程序发生的是游戏内自定义崩溃处理器退出，建议手选游戏自带崩溃日志补充线索，或扩大扫描时间窗至 24 小时确认。",
                    OfficialReferenceLabel: null,
                    OfficialReferenceUrl: null,
                    SpecificityRank: SpecificityRank);
            }

            if (inc.LinkedWerEvidence is not null && inc.WerLinkAttribution == AttributionStrength.Probable)
            {
                var eventName = inc.LinkedWerEvidence.AllowedFields.GetValueOrDefault("EventName", "WER");
                return new DiagnosisFinding(
                    RuleId: RuleId,
                    RuleVersion: RuleVersion,
                    Title: "关联到可能对应的 WER 错误报告",
                    Statement: $"在时间窗与进程身份一致的前提下关联到一条 {eventName} 错误报告，该报告为基于路径或进程时序的「可能对应」补充线索。",
                    Attribution: AttributionStrength.Probable,
                    EvidenceStrength: EvidenceStrength.LimitedClue,
                    EvidenceIds: [inc.LinkedWerEvidence.EvidenceId],
                    AmbiguityNotes: ["WER 与主事件缺少相同 ReportId，联结仅属可能对应。"],
                    NextActionTemplateId: "VERIFY_WER_TIMING",
                    NextActionText: "核对补充报告的时间点是否与本次故障发生瞬间吻合。",
                    OfficialReferenceLabel: null,
                    OfficialReferenceUrl: null,
                    SpecificityRank: SpecificityRank);
            }

            return null;
        }
    }

    public sealed class ExceptionAccessViolationRule : IDiagnosisRule
    {
        public string RuleId => "EXCEPTION-AV";
        public string RuleVersion => "1";
        public int SpecificityRank => 40;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null ||
                inc.AnchorKind != IncidentAnchorKind.ApplicationCrash ||
                !string.Equals(inc.ExceptionCode, "0xc0000005", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: "进程发生内存访问违规 (0xc0000005，根因未知)",
                Statement: "结构化事件字段记录到异常码 0xc0000005（Access Violation）。这仅说明进程尝试读写了无效或受保护的内存地址；单凭此异常码不能归咎内存条损坏、显卡故障或第三方 Mod。",
                Attribution: AttributionStrength.Direct,
                EvidenceStrength: EvidenceStrength.ExplicitRecord,
                EvidenceIds: [inc.PrimaryEvidence.EvidenceId],
                AmbiguityNotes: [],
                NextActionTemplateId: "CHECK_AV_REPRO",
                NextActionText: "核对闪退是否在同一场景或操作后稳定复现，并尝试手选对应游戏的 Unity/Unreal 日志以获取具体的调用前报错。",
                OfficialReferenceLabel: "Microsoft 异常代码排查参考",
                OfficialReferenceUrl: "https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-application-service-crashing-behavior",
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class EngineGpuLostRule : IDiagnosisRule
    {
        public string RuleId => "ENGINE-GPU-LOST";
        public string RuleVersion => "1";
        public int SpecificityRank => 80;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var log = context.SelectedIncident?.ConfirmedEngineLog;
            if (log is null || !log.HasGpuDeviceLostSignal)
            {
                return null;
            }

            var has4101 = context.SelectedIncident!.NearbySystemBackground
                .Any(static b => b.SummaryKind == "Display4101");

            var strength = log.IsTruncated ? EvidenceStrength.LimitedClue : EvidenceStrength.ExplicitRecord;
            var tokenText = string.IsNullOrWhiteSpace(log.GpuLostToken) ? "图形设备丢失信号" : log.GpuLostToken;
            var extra4101 = has4101 ? " 同时在故障前后 120 秒内记录到系统显示恢复背景事件，两者时间吻合。" : string.Empty;

            var ambiguity = new List<string>();
            if (log.IsTruncated)
            {
                ambiguity.Add("日志文件超出 2 MiB 读取上限，仅解析末尾部分。");
            }

            if (log.MayBeOverwritten)
            {
                ambiguity.Add("引擎日志可能被后续重开游戏覆盖，已按当前手选/确认文件记录归属。");
            }

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: $"手选日志记录图形错误标记 ({tokenText})",
                Statement: $"已确认的引擎日志中明确出现 {tokenText}。{extra4101} 驱动兼容性、游戏渲染管线异常、显存压力或显卡超频/降压不稳定均可能触发该标记，目前无法直接断言硬件损坏（且该标记本身不包含具体设备移除子原因）。",
                Attribution: AttributionStrength.UserSelected,
                EvidenceStrength: strength,
                EvidenceIds: [$"engine-log:{log.Kind}"],
                AmbiguityNotes: ambiguity,
                NextActionTemplateId: "CHECK_GPU_OVERCLOCK_AND_OVERLAY",
                NextActionText: "请先核对所选日志对应的游戏名与修改时间是否匹配本次闪退；确认后建议一次仅改动一个变量对照复测（例如先恢复显卡默认频率，或先临时关闭第三方游戏叠加层）。",
                OfficialReferenceLabel: "Microsoft Direct3D 设备丢失说明",
                OfficialReferenceUrl: "https://learn.microsoft.com/en-us/windows/uwp/gaming/handling-device-lost-scenarios",
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class EngineOomRule : IDiagnosisRule
    {
        public string RuleId => "ENGINE-OOM";
        public string RuleVersion => "2";
        public int SpecificityRank => 75;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null)
            {
                return null;
            }

            var log = inc.ConfirmedEngineLog;
            var logOom = log is { HasOutOfMemorySignal: true };
            var codeOom = inc.AnchorKind == IncidentAnchorKind.ApplicationCrash &&
                          string.Equals(inc.ExceptionCode, "0xe0000008", StringComparison.OrdinalIgnoreCase);

            if (!logOom && !codeOom)
            {
                return null;
            }

            var evidenceIds = new List<string>();
            if (codeOom)
            {
                evidenceIds.Add(inc.PrimaryEvidence.EvidenceId);
            }

            if (logOom)
            {
                evidenceIds.Add($"engine-log:{log!.Kind}");
            }

            if (!logOom)
            {
                // 0xe0000008 alone is a custom OOM exception code (e.g., Chromium/Electron); without an engine log, treat only as LimitedClue.
                return new DiagnosisFinding(
                    RuleId: RuleId,
                    RuleVersion: RuleVersion,
                    Title: "事件异常码 0xe0000008（常见于自定义内存不足语义）",
                    Statement: "事件结构化字段记录到异常码 0xe0000008。该码常见于基于 Chromium/Electron 等运行时自主上报的内存分配失败，但对未知游戏本体无法单独断定确切原因。",
                    Attribution: AttributionStrength.Direct,
                    EvidenceStrength: EvidenceStrength.LimitedClue,
                    EvidenceIds: evidenceIds,
                    AmbiguityNotes: ["仅凭异常码缺少引擎或进程内存上下文，暂不能直接归因为系统页面文件或物理内存不足。"],
                    NextActionTemplateId: "CHECK_OOM_CODE_WITH_LOG",
                    NextActionText: "建议先核对程序是否内置 Chromium/启动器壳层，并手动关联对应游戏的引擎崩溃日志确认是否真正触发 OOM 后再决定后续排查。",
                    OfficialReferenceLabel: null,
                    OfficialReferenceUrl: null,
                    SpecificityRank: SpecificityRank);
            }

            if (log!.Fatality == LogFatalityKind.NonFatalEnsure)
            {
                var ensureNotes = new List<string>
                {
                    "所选 Unreal 上下文标记为非致命 Ensure，未证实该次记录本身导致进程崩溃。"
                };
                if (log.IsTruncated)
                {
                    ensureNotes.Add("日志文件超出 2 MiB 读取上限，仅解析末尾部分。");
                }

                return new DiagnosisFinding(
                    RuleId: RuleId,
                    RuleVersion: RuleVersion,
                    Title: $"手选日志记录非致命 Ensure（含分配失败标记：{log.OomToken ?? "OOM"}）",
                    Statement: "已确认的日志包含非致命 Ensure 上下文及内存/显存分配失败文字，目前不足以断定进程直接崩溃或硬件/系统虚拟内存故障。",
                    Attribution: AttributionStrength.UserSelected,
                    EvidenceStrength: EvidenceStrength.LimitedClue,
                    EvidenceIds: evidenceIds,
                    AmbiguityNotes: ensureNotes,
                    NextActionTemplateId: "CHECK_NONFATAL_ENSURE",
                    NextActionText: "请核对闪退时间是否真的与该条 Ensure 吻合；当前为非致命告警上下文，建议先勿调整页面文件或系统配置。",
                    OfficialReferenceLabel: null,
                    OfficialReferenceUrl: null,
                    SpecificityRank: SpecificityRank);
            }

            var ambiguity = new List<string>();
            if (log.IsTruncated)
            {
                ambiguity.Add("日志文件超出 2 MiB 读取上限，仅解析末尾部分。");
            }

            if (log.MayBeOverwritten)
            {
                ambiguity.Add("引擎日志可能被后续重开游戏覆盖，已按当前手选/确认文件记录归属。");
            }

            var attribution = codeOom ? AttributionStrength.Direct : AttributionStrength.UserSelected;
            var strength = log.IsTruncated ? EvidenceStrength.LimitedClue : EvidenceStrength.ExplicitRecord;

            var pageFile = context.Environment?.PageFile;
            var allowPageFileHint = !log.IsTruncated &&
                pageFile is { Checked: true } &&
                (pageFile.IsDisabled || pageFile.TotalAllocatedMb < 4096);

            var pageFileHint = allowPageFileHint
                ? pageFile!.IsDisabled
                    ? "检测到本机当前未见活跃虚拟内存配置，若在完整崩溃中稳定复现，可核查系统托管页面文件设置。"
                    : $"检测到本机当前已分配页面文件总量较小（{pageFile.TotalAllocatedMb} MB），建议结合游戏画质/纹理选项排查。"
                : "建议先降低高显存纹理与画质选项对比复测，并关闭后台高内存占用程序。";

            var statement = codeOom
                ? $"事件记录异常码 0xe0000008，同时手选日志记录到内存/显存分配失败标记（{log.OomToken ?? "OOM"}）。"
                : $"已确认的引擎日志记录到内存/显存分配失败信号（{log.OomToken ?? "OOM"}）。";

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: "记录到内存或显存资源耗尽信号",
                Statement: statement,
                Attribution: attribution,
                EvidenceStrength: strength,
                EvidenceIds: evidenceIds,
                AmbiguityNotes: ambiguity,
                NextActionTemplateId: "CHECK_OOM_AND_PAGEFILE",
                NextActionText: pageFileHint,
                OfficialReferenceLabel: null,
                OfficialReferenceUrl: null,
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class ThirdPartyModuleHintRule : IDiagnosisRule
    {
        public string RuleId => "THIRD-PARTY-MODULE-HINT";
        public string RuleVersion => "1";
        public int SpecificityRank => 60;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null || inc.AnchorKind != IncidentAnchorKind.ApplicationCrash || string.IsNullOrWhiteSpace(inc.ModuleName))
            {
                return null;
            }

            var modFile = Path.GetFileName(inc.ModuleName.Trim());
            var isExplicitHook = ExplicitOverlayHookPrefixes.Any(p => modFile.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            var isLocalProxy = ProxyModuleNames.Contains(modFile) &&
                !string.IsNullOrWhiteSpace(inc.ModulePath) &&
                !PathClassifier.IsWindowsSystemDirectoryPath(inc.ModulePath) &&
                PathClassifier.IsInSameDirectoryOrSubtree(inc.ModulePath, inc.NormalizedAppPath);

            if (!isExplicitHook && !isLocalProxy)
            {
                return null;
            }

            var statement = isLocalProxy
                ? $"事件记录故障模块路径位于游戏目录内的本地代理模块 {modFile}。该类 DLL 常见于本地注入或 Mod 加载器，但也有部分游戏本身附带或依赖它们，当前仅作为有限线索。"
                : $"事件记录故障模块为已知第三方覆盖层或注入模块 {modFile}。该类模块常见于性能监控叠加层或录屏软件，但也有可能只是游戏异常调用的落点，当前仅作为有限线索。";

            var nextAction = isLocalProxy
                ? $"优先排查最近新增的 Mod 或外置插件；做对照隔离前请确认游戏目录内的 {modFile} 不是游戏本体必需文件，并保留原文件备份。"
                : $"尝试从对应监控/录制软件界面中临时关闭叠加层功能（优先避免直接删改系统或游戏文件），观察闪退是否消失。";

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: $"故障落点位于第三方注入或覆盖层模块 ({modFile})",
                Statement: statement,
                Attribution: AttributionStrength.Direct,
                EvidenceStrength: EvidenceStrength.LimitedClue,
                EvidenceIds: [inc.PrimaryEvidence.EvidenceId],
                AmbiguityNotes: ["仅表明崩溃发生在该模块上下文内，不排除是游戏本体传入了异常状态。"],
                NextActionTemplateId: "ISOLATE_OVERLAY_OR_PROXY_DLL",
                NextActionText: nextAction,
                OfficialReferenceLabel: null,
                OfficialReferenceUrl: null,
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class RuntimeModuleHintRule : IDiagnosisRule
    {
        public string RuleId => "RUNTIME-MODULE-HINT";
        public string RuleVersion => "1";
        public int SpecificityRank => 55;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null || inc.AnchorKind != IncidentAnchorKind.ApplicationCrash || string.IsNullOrWhiteSpace(inc.ModuleName))
            {
                return null;
            }

            var modFile = Path.GetFileName(inc.ModuleName.Trim());
            if (!VcRuntimeModuleNames.Contains(modFile))
            {
                return null;
            }

            var isUcrt = string.Equals(modFile, "ucrtbase.dll", StringComparison.OrdinalIgnoreCase);
            var statement = isUcrt
                ? "故障模块为 Windows 通用 C 运行时 ucrtbase.dll。许多应用异常最终会落在该模块中，仅此无法断定运行库本身损坏。"
                : $"故障模块位于 Visual C++ 运行库 ({modFile})，可能涉及运行库缺失/版本过旧或上层传入无效参数，仅作有限线索参考。";

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: $"故障落点位于运行库模块 ({modFile})",
                Statement: statement,
                Attribution: AttributionStrength.Direct,
                EvidenceStrength: EvidenceStrength.LimitedClue,
                EvidenceIds: [inc.PrimaryEvidence.EvidenceId],
                AmbiguityNotes: [],
                NextActionTemplateId: "CHECK_VCPP_REDIST",
                NextActionText: "可对照下方本机环境快照核验 x64/x86 Visual C++ 运行库安装状态，并按微软官方包完成修复或验证游戏文件完整性。",
                OfficialReferenceLabel: "Microsoft Visual C++ 最新受支持下载",
                OfficialReferenceUrl: "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class SystemContextRule : IDiagnosisRule
    {
        public string RuleId => "SYSTEM-CONTEXT";
        public string RuleVersion => "1";
        public int SpecificityRank => 30;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            var inc = context.SelectedIncident;
            if (inc is null)
            {
                return null;
            }

            var has4101 = inc.NearbySystemBackground.Any(static b => b.SummaryKind == "Display4101");
            var hasKp41 = context.SystemBackgroundEvents.Any(static b => b.SummaryKind == "KernelPower41");

            if (!has4101 && !hasKp41)
            {
                return null;
            }

            var evidenceIds = new List<string>();
            var parts = new List<string>();
            if (has4101)
            {
                var disp = inc.NearbySystemBackground.First(static b => b.SummaryKind == "Display4101");
                evidenceIds.Add(disp.Evidence.EvidenceId);
                parts.Add("事故前后 120 秒内系统记录到显示恢复事件 (Display 4101)");
            }

            if (hasKp41)
            {
                var kp = context.SystemBackgroundEvents.First(static b => b.SummaryKind == "KernelPower41");
                evidenceIds.Add(kp.Evidence.EvidenceId);
                parts.Add("扫描窗内存在上一次异常关机/重启记录 (Kernel-Power 41，记录时间为系统再次开机时刻)");
            }

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: "扫描窗内存在邻近系统背景事件",
                Statement: $"{string.Join("；", parts)}。这些事件无具体游戏进程归属，仅作系统时间线背景参考，不能单独用于判定显卡驱动或硬件故障。",
                Attribution: AttributionStrength.NearbyOnly,
                EvidenceStrength: EvidenceStrength.LimitedClue,
                EvidenceIds: evidenceIds,
                AmbiguityNotes: ["无进程级归属，仅表示同一时间段内的系统侧背景现象。"],
                NextActionTemplateId: "CONFIRM_SCREEN_OR_REBOOT",
                NextActionText: "请回忆闪退瞬间是否伴随屏幕黑屏闪烁、整机断电或自动重启；若仅为单游戏退回桌面且无引擎图形报错，不要据此盲目重装驱动或改动系统安全设置。",
                OfficialReferenceLabel: "Microsoft Kernel-Power 41 说明",
                OfficialReferenceUrl: "https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/event-id-41-restart",
                SpecificityRank: SpecificityRank);
        }
    }

    public sealed class UnknownFallbackRule : IDiagnosisRule
    {
        public string RuleId => "UNKNOWN";
        public string RuleVersion => "1";
        public int SpecificityRank => 10;

        public DiagnosisFinding? Evaluate(RuleEvaluationContext context)
        {
            if (context.SelectedIncident is not null)
            {
                return null;
            }

            var degradedSources = context.Coverage
                .Where(static c => c.Status != SourceCoverageStatus.Ok)
                .Select(static c => $"{c.SourceName}({c.Status})")
                .ToList();

            var kpNote = context.SystemBackgroundEvents.Any(static b => b.SummaryKind == "KernelPower41")
                ? " 注意：虽然扫描窗内存在 Kernel-Power 41 系统异常关机背景，但孤立系统背景无法指向具体游戏闪退。"
                : string.Empty;

            var degradedNote = degradedSources.Count > 0
                ? $" 部分来源存在受限或降级：{string.Join("、", degradedSources)}。"
                : string.Empty;

            return new DiagnosisFinding(
                RuleId: RuleId,
                RuleVersion: RuleVersion,
                Title: "在当前范围内未找到足够证据",
                Statement: $"未选中或未发现足以定位本次闪退的应用故障记录。{kpNote}{degradedNote}".Trim(),
                Attribution: AttributionStrength.Unlinked,
                EvidenceStrength: EvidenceStrength.Insufficient,
                EvidenceIds: [],
                AmbiguityNotes: degradedSources,
                NextActionTemplateId: "EXPAND_WINDOW_OR_SELECT_LOG",
                NextActionText: "可尝试将扫描窗口切换为最近 24 小时或 7 天、开启「显示全部应用事件」排除过滤，或手动拖入游戏自身的崩溃日志文件进行辅助排查。",
                OfficialReferenceLabel: null,
                OfficialReferenceUrl: null,
                SpecificityRank: SpecificityRank);
        }
    }
}
