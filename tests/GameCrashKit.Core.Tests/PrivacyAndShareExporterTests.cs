using GameCrashKit.Core.Exporters;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;
using GameCrashKit.Core.Rules;
using Xunit;

namespace GameCrashKit.Core.Tests;

public sealed class PrivacyAndShareExporterTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 26, 10, 30, 45, TimeSpan.Zero);

    private static (DiagnosisReport Report, ScrubContext Context) BuildHostileReport()
    {
        var context = new ScrubContext(
            CurrentUserName: "SecretGamer88",
            CurrentMachineName: "MY-SECRET-RIG",
            UserProfilePath: @"C:\Users\SecretGamer88");

        var req = new ScanRequest(BaseUtc.AddHours(-2), BaseUtc);
        var ev = new EvidenceItem(
            "ev-1",
            EvidenceSourceKind.WindowsApplicationCrash1000,
            BaseUtc.AddMinutes(-3),
            "EventSystemTimeUtc",
            "Application / Application Error / ID 1000 (v0) / RecordId 100",
            "1.0.0",
            new Dictionary<string, string> { ["AppName"] = "SecretGame.exe" },
            false);

        var engineLog = new UserConfirmedEngineLog(
            EngineLogKind.Unity,
            "Player.log",
            "Player.log (Bytes 0..500 / 500)",
            BaseUtc,
            IsTruncated: false,
            MayBeOverwritten: true,
            EngineVersion: "2022.3.20f1",
            RhiBackend: "Direct3D 11",
            HasGpuDeviceLostSignal: true,
            GpuLostToken: "DXGI_ERROR_DEVICE_REMOVED",
            HasOutOfMemorySignal: false,
            OomToken: null,
            RawErrorExcerptLines:
            [
                "User path C:\\Users\\SecretGamer88\\AppData\\LocalLow\\Studio\\Game\\Player.log on MY-SECRET-RIG\x07\x1B",
                "SID S-1-5-21-3623811015-3361044348-30300820-1013 connected from 192.168.10.42",
                "Contact player@example.com steam=76561198012345678 --auth_token=supersecret999",
                "Bearer AbCdEf123456== D3D11 DXGI_ERROR_DEVICE_REMOVED",
                "Line 5 kept for excerpt count cap",
                "Line 6 MUST BE DROPPED because cap is 5 lines"
            ]);

        var inc = new CrashIncident(
            IncidentId: "inc-1",
            AnchorKind: IncidentAnchorKind.ApplicationCrash,
            OccurredAtUtc: BaseUtc.AddMinutes(-3),
            AppName: "SecretGame.exe",
            NormalizedAppPath: @"C:\Users\SecretGamer88\Games\SecretGame\SecretGame.exe",
            DisambiguatedPathSuffix: @"...\Games\SecretGame\SecretGame.exe",
            ModuleName: "d3d11.dll",
            ModulePath: @"C:\Windows\System32\d3d11.dll",
            ExceptionCode: "0xc0000005",
            FaultingProcessIdHex: "0x1234",
            ProcessCreationTimeHex: "0x1db4000",
            ReportId: "99999999-aaaa-bbbb-cccc-dddddddddddd",
            HangType: null,
            IsLikelyGame: true,
            IsSystemNoise: false,
            IsSupportedSchemaVersion: true,
            OccurrenceCountInWindow: 2,
            OccurrenceTimestampsUtc: [BaseUtc.AddMinutes(-3), BaseUtc.AddMinutes(-20)],
            WerLinkAttribution: AttributionStrength.Direct,
            PrimaryEvidence: ev,
            LinkedWerEvidence: null,
            NearbySystemBackground: [],
            ConfirmedEngineLog: engineLog,
            AmbiguityNotes: []);

        var env = new EnvironmentSnapshot(
            BaseUtc,
            [new GpuAdapterFact("NVIDIA GeForce RTX 5060", "32.0.16.1664", "2026-08-24", false)],
            new TargetPathFact(true, false, false, 52, @"...\Games\SecretGame\SecretGame.exe"),
            new OneDriveDocsFact(true, false, false, EnvironmentNoticeLevel.Info, "未指向 OneDrive"),
            new VcRuntimeFact(true, "v14.51.36247.00", true, "v14.51.36247.00", EnvironmentNoticeLevel.Info),
            new PageFileFact(true, false, 51200, 2, false, EnvironmentNoticeLevel.Info, "51200 MB"),
            new RunningOverlaysFact(true, ["Discord"], "仅扫描瞬间"),
            new SystemLocaleAndSecurityFact("Windows 11 25H2 (Build 26200.9457)", 26200, 32768, 936, false, true),
            []);

        var report = new DiagnosisRuleEngine().Evaluate(
            new RuleEvaluationContext(req, inc, [], [new SourceCoverage("Application", req.WindowStartUtc, req.WindowEndUtc, 10, false, SourceCoverageStatus.Ok, null)], env),
            [ev],
            appVersion: "0.1.0-alpha",
            generatedAtUtc: BaseUtc);

        return (report, context);
    }

    private static void AssertContainsZeroSecrets(string output)
    {
        string[] forbidden =
        [
            "SecretGamer88",
            "MY-SECRET-RIG",
            "S-1-5-21-3623811015-3361044348-30300820-1013",
            "192.168.10.42",
            "player@example.com",
            "76561198012345678",
            "supersecret999",
            "AbCdEf123456==",
            "99999999-aaaa-bbbb-cccc-dddddddddddd",
            @"C:\Users\SecretGamer88",
            "\x07",
            "\x1B",
            "Line 6 MUST BE DROPPED"
        ];

        foreach (var secret in forbidden)
        {
            Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DefaultWhitelist_ExcludesAllSecretsAndExcerptsWhilePreservingVersions()
    {
        var (report, context) = BuildHostileReport();

        var share = ShareReportProjector.Project(report, new ShareOptions(), context, TimeZoneInfo.Utc);
        var md = MarkdownReportExporter.Render(share);

        Assert.True(share.HasAvailableExcerptSource);
        Assert.False(share.HasOptInExcerpt);
        Assert.Empty(share.OptInScrubbedExcerptLines);
        AssertContainsZeroSecrets(md);
        Assert.Contains("32.0.16.1664", md); // dotted driver version must NOT be scrubbed as IP
        Assert.DoesNotContain("附带脱敏报错摘要", md);
    }

    [Fact]
    public void OptInScrubbedExcerpt_EmitsOnlyFixedSignalTemplatesAndScrubberRedactsEdgeSecrets()
    {
        var (report, context) = BuildHostileReport();

        var share = ShareReportProjector.Project(
            report,
            new ShareOptions(HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true),
            context,
            TimeZoneInfo.Utc);
        var md = MarkdownReportExporter.Render(share);

        Assert.True(share.HasOptInExcerpt);
        Assert.Contains("日志类型: Unity", md);
        Assert.Contains("DXGI_ERROR_DEVICE_REMOVED", md);
        // Raw free-text log lines must stay strictly local even when opt-in signal summary is enabled.
        Assert.DoesNotContain("Line 5 kept", md);
        Assert.DoesNotContain("Line 6 MUST BE DROPPED", md);
        AssertContainsZeroSecrets(md);

        var edgeContext = new ScrubContext("张三", "X", @"C:\Users\张三");
        var edgeRaw = "host X account=\"player_99\" token='tok-abc-123' [U:1:987654] on C:\\Users\\张三\\Desktop\\run.log at 20:18:04.123 for 张三";
        var edgeScrubbed = PrivacyScrubber.ScrubText(edgeRaw, edgeContext, scrubIpv4: true, hideTimestamps: true);

        Assert.DoesNotContain("张三", edgeScrubbed);
        Assert.DoesNotContain("tok-abc-123", edgeScrubbed);
        Assert.DoesNotContain("player_99", edgeScrubbed);
        Assert.DoesNotContain("[U:1:987654]", edgeScrubbed);
        Assert.DoesNotContain("20:18:04", edgeScrubbed);
        Assert.Contains("<PC_NAME>", edgeScrubbed);
        Assert.Contains("<STEAM_ID>", edgeScrubbed);
        Assert.Contains("<TIME>", edgeScrubbed);
    }

    [Fact]
    public void DefaultWhitelistAndHideApp_ScrubsPrivateFolderLanIpAndSameNameModule()
    {
        var (report, context) = BuildHostileReport();
        var incWithPrivateDir = report.SelectedIncident! with
        {
            AppName = "ReviewGame.exe",
            ModuleName = "ReviewGame.exe",
            NormalizedAppPath = @"D:\QA-Family\Game\ReviewGame.exe",
            DisambiguatedPathSuffix = @"...\QA-Family\Game\ReviewGame.exe"
        };
        var privateReport = report with { SelectedIncident = incWithPrivateDir };

        // Default share: never exposes arbitrary private folder names like QA-Family.
        var defaultShare = ShareReportProjector.Project(privateReport, new ShareOptions(), context, TimeZoneInfo.Utc);
        var defaultMd = MarkdownReportExporter.Render(defaultShare);
        Assert.DoesNotContain("QA-Family", defaultMd, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("独立游戏目录", defaultShare.DisambiguatedPathSuffix);

        // HideAppName: also masks matching ModuleName and App stem in findings/summaries.
        var hiddenShare = ShareReportProjector.Project(
            privateReport,
            new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: false),
            context,
            TimeZoneInfo.Utc);
        var hiddenMd = MarkdownReportExporter.Render(hiddenShare);

        Assert.Equal("<APP>", hiddenShare.AppDisplayName);
        Assert.Equal("<APP_MODULE>", hiddenShare.ModuleName);
        Assert.Equal("<已隐藏>", hiddenShare.DisambiguatedPathSuffix);
        Assert.DoesNotContain("ReviewGame", hiddenMd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10:27:45", hiddenMd);
    }

    [Fact]
    public void HideAppNameAndTimestamp_ReplacesAppAndHidesExactClockDeterministically()
    {
        var (report, context) = BuildHostileReport();
        var options = new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: false);

        var m1 = ShareReportProjector.Project(report, options, context, TimeZoneInfo.Utc);
        var m2 = ShareReportProjector.Project(report, options, context, TimeZoneInfo.Utc);
        var md1 = MarkdownReportExporter.Render(m1);
        var md2 = MarkdownReportExporter.Render(m2);

        Assert.Equal("<APP>", m1.AppDisplayName);
        Assert.Equal("<已隐藏>", m1.DisambiguatedPathSuffix);
        Assert.DoesNotContain("SecretGame.exe", md1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10:27:45", md1);
        Assert.Equal(md1, md2);
    }

    [Fact]
    public void OptInSignalSummary_StripsPrivateEngineBranchAndCustomRhiStrings()
    {
        var (report, context) = BuildHostileReport();
        var privateLog = report.SelectedIncident!.ConfirmedEngineLog! with
        {
            EngineVersion = "5.4.4-0+++UE5+PrivateStudioBranch",
            RhiBackend = "CustomInternalRHI (StudioSecret)"
        };
        var privateInc = report.SelectedIncident! with { ConfirmedEngineLog = privateLog };
        var privateReport = report with { SelectedIncident = privateInc };

        var share = ShareReportProjector.Project(
            privateReport,
            new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true),
            context,
            TimeZoneInfo.Utc);
        var md = MarkdownReportExporter.Render(share);

        Assert.Contains("5.4.4", md);
        Assert.DoesNotContain("PrivateStudioBranch", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CustomInternalRHI", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StudioSecret", md, StringComparison.OrdinalIgnoreCase);
    }
}
