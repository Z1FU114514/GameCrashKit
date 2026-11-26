using GameCrashKit.Core.Models;
using GameCrashKit.Core.Rules;
using Xunit;

namespace GameCrashKit.Core.Tests;

public sealed class DiagnosisRuleEngineTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly ScanRequest DefaultReq = new(NowUtc.AddHours(-2), NowUtc);

    private static CrashIncident MakeIncident(
        IncidentAnchorKind kind = IncidentAnchorKind.ApplicationCrash,
        string? appName = "SampleGame.exe",
        string? moduleName = "SampleGame.exe",
        string? modulePath = @"D:\Games\steamapps\common\SampleGame\SampleGame.exe",
        string? exceptionCode = "0xc0000005",
        bool supportedSchema = true,
        AttributionStrength werAttr = AttributionStrength.Unlinked,
        EvidenceItem? linkedWer = null,
        IReadOnlyList<BackgroundEventRecord>? nearbyBg = null,
        UserConfirmedEngineLog? engineLog = null)
    {
        var ev = new EvidenceItem(
            "ev-1",
            kind == IncidentAnchorKind.ApplicationHang ? EvidenceSourceKind.WindowsApplicationHang1002
                : kind == IncidentAnchorKind.WerReportOnly ? EvidenceSourceKind.WindowsWerReport1001
                : EvidenceSourceKind.WindowsApplicationCrash1000,
            NowUtc.AddMinutes(-5),
            "EventSystemTimeUtc",
            "Application / Application Error / ID 1000 (v0) / RecordId 1",
            "1.0.0",
            new Dictionary<string, string> { ["EventName"] = "APPCRASH" },
            !supportedSchema);

        return new CrashIncident(
            IncidentId: "inc-1",
            AnchorKind: kind,
            OccurredAtUtc: NowUtc.AddMinutes(-5),
            AppName: appName,
            NormalizedAppPath: @"D:\Games\steamapps\common\SampleGame\SampleGame.exe",
            DisambiguatedPathSuffix: @"...\common\SampleGame\SampleGame.exe",
            ModuleName: moduleName,
            ModulePath: modulePath,
            ExceptionCode: exceptionCode,
            FaultingProcessIdHex: "0x100",
            ProcessCreationTimeHex: "0x1",
            ReportId: "11111111-2222-3333-4444-555555555555",
            HangType: kind == IncidentAnchorKind.ApplicationHang ? "Cross-thread" : null,
            IsLikelyGame: true,
            IsSystemNoise: false,
            IsSupportedSchemaVersion: supportedSchema,
            OccurrenceCountInWindow: 1,
            OccurrenceTimestampsUtc: [NowUtc.AddMinutes(-5)],
            WerLinkAttribution: werAttr,
            PrimaryEvidence: ev,
            LinkedWerEvidence: linkedWer,
            NearbySystemBackground: nearbyBg ?? [],
            ConfirmedEngineLog: engineLog,
            AmbiguityNotes: []);
    }

    [Fact]
    public void Evaluate_AppCrashWithGenericOsModule_StatesModuleIsOnlyFaultLocation()
    {
        var engine = new DiagnosisRuleEngine();
        var inc = MakeIncident(moduleName: "ntdll.dll", modulePath: @"C:\Windows\System32\ntdll.dll", exceptionCode: "0xc0000374");
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        var appCrash = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "APP-CRASH");
        Assert.Equal(AttributionStrength.Direct, appCrash.Attribution);
        Assert.Equal(EvidenceStrength.ExplicitRecord, appCrash.EvidenceStrength);
        Assert.Contains("通用系统模块 ntdll.dll", appCrash.Statement);
        Assert.DoesNotContain(report.PrimaryFindings, static f => f.RuleId == "EXCEPTION-AV");
    }

    [Fact]
    public void Evaluate_AppHang1002_TriggersAppHangAndNotAppCrash()
    {
        var engine = new DiagnosisRuleEngine();
        var inc = MakeIncident(kind: IncidentAnchorKind.ApplicationHang, moduleName: null, exceptionCode: null);
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        Assert.Contains(report.PrimaryFindings, static f => f.RuleId == "APP-HANG" && f.EvidenceStrength == EvidenceStrength.ExplicitRecord);
        Assert.DoesNotContain(report.PrimaryFindings, static f => f.RuleId == "APP-CRASH");
    }

    [Fact]
    public void Evaluate_WerReportOnlyAndProbableWer_ProduceExpectedFindings()
    {
        var engine = new DiagnosisRuleEngine();
        var werOnly = MakeIncident(kind: IncidentAnchorKind.WerReportOnly, moduleName: null, exceptionCode: null);
        var r1 = engine.Evaluate(new RuleEvaluationContext(DefaultReq, werOnly, [], [], null), [werOnly.PrimaryEvidence]);
        Assert.Contains(r1.PrimaryFindings, static f => f.RuleId == "WER-REPORT" && f.Attribution == AttributionStrength.Direct);

        var werEv = new EvidenceItem("ev-wer", EvidenceSourceKind.WindowsWerReport1001, NowUtc, "EventSystemTimeUtc", "Application / WER / 1001", "1.0.0", new Dictionary<string, string> { ["EventName"] = "APPCRASH" }, false);
        var crashWithProbableWer = MakeIncident(exceptionCode: "0xc0000001", werAttr: AttributionStrength.Probable, linkedWer: werEv);
        var r2 = engine.Evaluate(new RuleEvaluationContext(DefaultReq, crashWithProbableWer, [], [], null), [crashWithProbableWer.PrimaryEvidence, werEv]);
        Assert.Contains(r2.PrimaryFindings, static f => f.RuleId == "WER-REPORT" && f.Attribution == AttributionStrength.Probable && f.EvidenceStrength == EvidenceStrength.LimitedClue);
    }

    [Fact]
    public void Evaluate_AccessViolation0xc0000005_ExplicitlyStatesUnknownRootCause()
    {
        var engine = new DiagnosisRuleEngine();
        var inc = MakeIncident(exceptionCode: "0xc0000005");
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        var av = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "EXCEPTION-AV");
        Assert.Equal(EvidenceStrength.ExplicitRecord, av.EvidenceStrength);
        Assert.Contains("不能归咎内存条", av.Statement);
    }

    [Fact]
    public void Evaluate_EngineGpuLostWithNearby4101_NeverRecommendsDduOrBlamesHardware()
    {
        var engine = new DiagnosisRuleEngine();
        var bg4101 = new BackgroundEventRecord(
            new EvidenceItem("ev-4101", EvidenceSourceKind.WindowsDisplay4101, NowUtc.AddMinutes(-5), "EventSystemTimeUtc", "System / Display / 4101", "1.0.0", new Dictionary<string, string>(), false),
            AttributionStrength.NearbyOnly,
            "Display4101");

        var engineLog = new UserConfirmedEngineLog(
            EngineLogKind.Unity,
            "Unity Player.log",
            "Player.log",
            NowUtc,
            IsTruncated: false,
            MayBeOverwritten: false,
            EngineVersion: "2022.3.20f1c1",
            RhiBackend: "D3D11",
            HasGpuDeviceLostSignal: true,
            GpuLostToken: "DXGI_ERROR_DEVICE_REMOVED",
            HasOutOfMemorySignal: false,
            OomToken: null,
            RawErrorExcerptLines: ["D3D11: Failed to create RenderTexture, error DXGI_ERROR_DEVICE_REMOVED"]);

        var inc = MakeIncident(nearbyBg: [bg4101], engineLog: engineLog);
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence, bg4101.Evidence]);

        var gpuFinding = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-GPU-LOST");
        Assert.Equal(AttributionStrength.UserSelected, gpuFinding.Attribution);
        Assert.Equal(EvidenceStrength.ExplicitRecord, gpuFinding.EvidenceStrength);
        Assert.Contains("无法直接断言硬件损坏", gpuFinding.Statement);
        Assert.All(report.PrimaryFindings, static f =>
        {
            Assert.DoesNotContain("DDU", f.NextActionText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HVCI", f.NextActionText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Evaluate_EngineOomWithLowPageFile_ReferencesNeutralPageFileSnapshot()
    {
        var engine = new DiagnosisRuleEngine();
        var engineLog = new UserConfirmedEngineLog(
            EngineLogKind.Unreal,
            "Unreal CrashContext.runtime-xml",
            "CrashContext.runtime-xml",
            NowUtc,
            false,
            false,
            "5.4.4",
            "D3D12",
            false,
            null,
            true,
            "Out of video memory",
            ["Out of video memory trying to allocate a rendering resource."]);

        var env = new EnvironmentSnapshot(
            NowUtc,
            [],
            new TargetPathFact(false, false, false, null, null),
            new OneDriveDocsFact(true, false, false, EnvironmentNoticeLevel.Info, "ok"),
            new VcRuntimeFact(true, "14.51", true, "14.51", EnvironmentNoticeLevel.Info),
            new PageFileFact(true, true, 0, 0, false, EnvironmentNoticeLevel.Attention, "disabled"),
            new RunningOverlaysFact(true, [], "note"),
            new SystemLocaleAndSecurityFact("Windows 11", 26200, 16384, 936, false, true),
            []);

        var inc = MakeIncident(engineLog: engineLog);
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], env), [inc.PrimaryEvidence]);

        var oom = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-OOM");
        Assert.Contains("页面文件", oom.NextActionText);
    }

    [Fact]
    public void Evaluate_ThirdPartyModuleHint_RejectsSystem32ProxyDllNegativeCase()
    {
        var engine = new DiagnosisRuleEngine();

        var systemDinput = MakeIncident(moduleName: "dinput8.dll", modulePath: @"C:\Windows\System32\dinput8.dll", exceptionCode: "0xc0000001");
        var negReport = engine.Evaluate(new RuleEvaluationContext(DefaultReq, systemDinput, [], [], null), [systemDinput.PrimaryEvidence]);
        Assert.DoesNotContain(negReport.PrimaryFindings, static f => f.RuleId == "THIRD-PARTY-MODULE-HINT");

        var localDinput = MakeIncident(moduleName: "dinput8.dll", modulePath: @"D:\Games\steamapps\common\SampleGame\dinput8.dll", exceptionCode: "0xc0000001");
        var posReport = engine.Evaluate(new RuleEvaluationContext(DefaultReq, localDinput, [], [], null), [localDinput.PrimaryEvidence]);
        Assert.Contains(posReport.PrimaryFindings, static f => f.RuleId == "THIRD-PARTY-MODULE-HINT" && f.EvidenceStrength == EvidenceStrength.LimitedClue);
    }

    [Fact]
    public void Evaluate_RuntimeModuleHint_TriggersForVcRuntimeAndNeverExceedsLimitedClue()
    {
        var engine = new DiagnosisRuleEngine();
        var inc = MakeIncident(moduleName: "vcruntime140.dll", modulePath: @"C:\Windows\System32\vcruntime140.dll", exceptionCode: "0xc0000001");
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        var rt = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "RUNTIME-MODULE-HINT");
        Assert.Equal(EvidenceStrength.LimitedClue, rt.EvidenceStrength);
    }

    [Fact]
    public void Evaluate_SystemContextWithNearby4101AndKp41_NeverEvictsDirectFactsAndNeverBlamesDriver()
    {
        var engine = new DiagnosisRuleEngine();
        var bg4101 = new BackgroundEventRecord(
            new EvidenceItem("ev-4101", EvidenceSourceKind.WindowsDisplay4101, NowUtc.AddMinutes(-5), "EventSystemTimeUtc", "System / Display / 4101", "1.0.0", new Dictionary<string, string>(), false),
            AttributionStrength.NearbyOnly,
            "Display4101");
        var bgKp41 = new BackgroundEventRecord(
            new EvidenceItem("ev-kp41", EvidenceSourceKind.WindowsKernelPower41, NowUtc.AddMinutes(-10), "EventSystemTimeUtc", "System / Kernel-Power / 41", "1.0.0", new Dictionary<string, string>(), false),
            AttributionStrength.NearbyOnly,
            "KernelPower41",
            0);

        // 2 ExplicitRecords (APP-CRASH, EXCEPTION-AV) + 2 LimitedClues (RUNTIME-MODULE-HINT, SYSTEM-CONTEXT) -> ExplicitRecords MUST stay!
        var inc = MakeIncident(moduleName: "vcruntime140.dll", exceptionCode: "0xc0000005", nearbyBg: [bg4101]);
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [bgKp41], [], null), [inc.PrimaryEvidence, bg4101.Evidence, bgKp41.Evidence]);

        Assert.Equal(3, report.PrimaryFindings.Count);
        Assert.Contains(report.PrimaryFindings, static f => f.RuleId == "APP-CRASH");
        Assert.Contains(report.PrimaryFindings, static f => f.RuleId == "EXCEPTION-AV");
        Assert.All(report.PrimaryFindings, static f =>
        {
            Assert.DoesNotContain("DDU", f.NextActionText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("HVCI", f.NextActionText, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Evaluate_NoSelectedIncidentEvenWithKernelPower41_TriggersUnknownFallbackDeterministically()
    {
        var engine = new DiagnosisRuleEngine();
        var bgKp41 = new BackgroundEventRecord(
            new EvidenceItem("ev-kp41", EvidenceSourceKind.WindowsKernelPower41, NowUtc.AddMinutes(-10), "EventSystemTimeUtc", "System / Kernel-Power / 41", "1.0.0", new Dictionary<string, string>(), false),
            AttributionStrength.NearbyOnly,
            "KernelPower41",
            0);

        var coverage = new List<SourceCoverage>
        {
            new("Application", DefaultReq.WindowStartUtc, DefaultReq.WindowEndUtc, 0, false, SourceCoverageStatus.Ok, null)
        };

        var r1 = engine.Evaluate(new RuleEvaluationContext(DefaultReq, null, [bgKp41], coverage, null), [bgKp41.Evidence], generatedAtUtc: NowUtc);
        var r2 = engine.Evaluate(new RuleEvaluationContext(DefaultReq, null, [bgKp41], coverage, null), [bgKp41.Evidence], generatedAtUtc: NowUtc);

        var unknown = Assert.Single(r1.PrimaryFindings);
        var unknown2 = Assert.Single(r2.PrimaryFindings);
        Assert.Equal("UNKNOWN", unknown.RuleId);
        Assert.Equal(EvidenceStrength.Insufficient, unknown.EvidenceStrength);
        Assert.Contains("Kernel-Power 41", unknown.Statement);
        Assert.Equal(unknown.RuleId, unknown2.RuleId);
        Assert.Equal(unknown.RuleVersion, unknown2.RuleVersion);
        Assert.Equal(unknown.Statement, unknown2.Statement);
        Assert.Equal(unknown.NextActionText, unknown2.NextActionText);
        Assert.Equal(unknown.EvidenceIds, unknown2.EvidenceIds);
        Assert.Equal(unknown.AmbiguityNotes, unknown2.AmbiguityNotes);
    }

    [Fact]
    public void Evaluate_ProxyDllWithNullModulePath_DoesNotTriggerThirdPartyModuleHint()
    {
        var engine = new DiagnosisRuleEngine();
        var inc = MakeIncident(moduleName: "version.dll", modulePath: null, exceptionCode: "0xc0000001");
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        Assert.DoesNotContain(report.PrimaryFindings, static f => f.RuleId == "THIRD-PARTY-MODULE-HINT");
    }

    [Fact]
    public void Evaluate_Exception0xe0000008Alone_DowngradesToLimitedClueWithoutPageFilePrescription()
    {
        var engine = new DiagnosisRuleEngine();
        var inc = MakeIncident(moduleName: "chrome_elf.dll", exceptionCode: "0xe0000008");
        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        var oom = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-OOM");
        Assert.Equal(EvidenceStrength.LimitedClue, oom.EvidenceStrength);
        Assert.DoesNotContain("关闭", oom.NextActionText);
        Assert.DoesNotContain("页面文件", oom.NextActionText);
    }

    [Fact]
    public void Evaluate_PrimaryCapExceeded_PreservesAdditionalHints()
    {
        var engine = new DiagnosisRuleEngine();
        var engineLog = new UserConfirmedEngineLog(
            EngineLogKind.Unity,
            "Player.log",
            "Player.log",
            NowUtc,
            false,
            false,
            "2022.3",
            "D3D11",
            true,
            "DXGI_ERROR_DEVICE_REMOVED",
            false,
            null,
            ["DXGI_ERROR_DEVICE_REMOVED"]);

        var inc = MakeIncident(
            moduleName: "RTSSHooks64.dll",
            modulePath: @"C:\Program Files (x86)\RivaTuner\RTSSHooks64.dll",
            exceptionCode: "0xc0000005",
            engineLog: engineLog);

        var report = engine.Evaluate(new RuleEvaluationContext(DefaultReq, inc, [], [], null), [inc.PrimaryEvidence]);

        Assert.Equal(3, report.PrimaryFindings.Count);
        Assert.NotNull(report.AdditionalHints);
        Assert.Contains(report.AdditionalHints!, static h => h.RuleId == "THIRD-PARTY-MODULE-HINT");
    }
}
