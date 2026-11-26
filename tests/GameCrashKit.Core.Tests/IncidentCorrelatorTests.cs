using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;
using Xunit;

namespace GameCrashKit.Core.Tests;

public sealed class IncidentCorrelatorTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    private static ScanRequest DefaultRequest(bool includeAll = false) =>
        new(BaseUtc.AddHours(-7 * 24), BaseUtc.AddHours(1), IncludeAllApplications: includeAll);

    private static ParsedEventRecord MakeApp1000(
        long recordId,
        DateTimeOffset timeUtc,
        string appName = "SampleGame.exe",
        string appPath = @"D:\Games\steamapps\common\SampleGame\SampleGame.exe",
        string moduleName = "SampleGame.exe",
        string exceptionCode = "c0000005",
        string pidHex = "0x1000",
        string createTimeHex = "0x1db400000000001",
        string? reportId = "11111111-2222-3333-4444-555555555555")
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppName"] = appName,
            ["AppVersion"] = "1.0.0.0",
            ["ModuleName"] = moduleName,
            ["ModuleVersion"] = "1.0.0.0",
            ["ExceptionCode"] = exceptionCode,
            ["FaultingOffset"] = "0000000000001000",
            ["ProcessId"] = pidHex,
            ["ProcessCreationTime"] = createTimeHex,
            ["AppPath"] = appPath,
            ["ModulePath"] = appPath,
            ["IntegratorReportId"] = reportId ?? string.Empty
        };

        return new ParsedEventRecord("Application", "Application Error", 1000, 0, 2, recordId, timeUtc, fields, [], false);
    }

    private static ParsedEventRecord MakeApp1002(
        long recordId,
        DateTimeOffset timeUtc,
        string appName = "SampleGame.exe",
        string exeFileName = @"D:\Games\steamapps\common\SampleGame\SampleGame.exe",
        string pidHex = "0x2000",
        string startTimeHex = "0x1db400000000002",
        string? reportId = "22222222-3333-4444-5555-666666666666")
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AppName"] = appName,
            ["AppVersion"] = "1.0.0.0",
            ["ProcessId"] = pidHex,
            ["StartTime"] = startTimeHex,
            ["TerminationTime"] = "350",
            ["ExeFileName"] = exeFileName,
            ["ReportId"] = reportId ?? string.Empty,
            ["HangType"] = "Top level window is idle"
        };

        return new ParsedEventRecord("Application", "Application Hang", 1002, 0, 2, recordId, timeUtc, fields, [], false);
    }

    private static ParsedEventRecord MakeWer1001(
        long recordId,
        DateTimeOffset timeUtc,
        string eventName,
        string p1,
        string? reportId,
        string? appPath = null,
        string? pidHex = null,
        string? procCreationHex = null)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EventName"] = eventName,
            ["Bucket"] = "9999999",
            ["P1"] = p1,
            ["ReportId"] = reportId ?? string.Empty
        };
        if (appPath is not null) fields["AppPath"] = appPath;
        if (pidHex is not null) fields["ProcessId"] = pidHex;
        if (procCreationHex is not null) fields["ProcessCreationTime"] = procCreationHex;

        return new ParsedEventRecord("Application", "Windows Error Reporting", 1001, 0, 4, recordId, timeUtc, fields, [], false);
    }

    private static ParsedEventRecord MakeKp41(long recordId, DateTimeOffset timeUtc)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BugcheckCode"] = "0",
            ["SleepInProgress"] = "0",
            ["PowerButtonTimestamp"] = "0"
        };
        return new ParsedEventRecord("System", "Microsoft-Windows-Kernel-Power", 41, 10, 1, recordId, timeUtc, fields, [], false);
    }

    [Fact]
    public void Correlate_DirectReportIdMatch_LinksCrash1000AndWer1001AsSingleIncident()
    {
        var ev1000 = MakeApp1000(101, BaseUtc);
        var ev1001 = MakeWer1001(102, BaseUtc.AddSeconds(3), "APPCRASH", "SampleGame.exe", "11111111-2222-3333-4444-555555555555");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, ev1001]);

        var inc = Assert.Single(result.Candidates);
        Assert.Equal(IncidentAnchorKind.ApplicationCrash, inc.AnchorKind);
        Assert.Equal(AttributionStrength.Direct, inc.WerLinkAttribution);
        Assert.NotNull(inc.LinkedWerEvidence);
    }

    [Fact]
    public void Correlate_DirectReportIdMatch_LinksHang1002AndWer1001AsSingleIncident()
    {
        var ev1002 = MakeApp1002(201, BaseUtc);
        var ev1001 = MakeWer1001(202, BaseUtc.AddSeconds(4), "AppHangB1", "SampleGame.exe", "22222222-3333-4444-5555-666666666666");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1002, ev1001]);

        var inc = Assert.Single(result.Candidates);
        Assert.Equal(IncidentAnchorKind.ApplicationHang, inc.AnchorKind);
        Assert.Equal(AttributionStrength.Direct, inc.WerLinkAttribution);
        Assert.NotNull(inc.LinkedWerEvidence);
    }

    [Fact]
    public void Correlate_ProbableMatchViaNormalizedFullPathWithin120s_LinksAsProbable()
    {
        var ev1000 = MakeApp1000(101, BaseUtc, reportId: null);
        var ev1001 = MakeWer1001(
            102,
            BaseUtc.AddSeconds(45),
            "APPCRASH",
            "SampleGame.exe",
            reportId: null,
            appPath: "d:/Games/steamapps/common/SampleGame/SampleGame.exe");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, ev1001]);

        var inc = Assert.Single(result.Candidates);
        Assert.Equal(AttributionStrength.Probable, inc.WerLinkAttribution);
        Assert.NotNull(inc.LinkedWerEvidence);
    }

    [Fact]
    public void Correlate_ProbableMatchViaPidAndCreationTime_LinksAsProbable()
    {
        var ev1000 = MakeApp1000(101, BaseUtc, pidHex: "0x2a10", createTimeHex: "0x1db401234567890", reportId: null);
        var ev1001 = MakeWer1001(102, BaseUtc.AddSeconds(15), "APPCRASH", "SampleGame.exe", reportId: null, pidHex: "0x2a10", procCreationHex: "0x1db401234567890");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, ev1001]);

        var inc = Assert.Single(result.Candidates);
        Assert.Equal(AttributionStrength.Probable, inc.WerLinkAttribution);
    }

    [Fact]
    public void Correlate_PidReuseWithDifferentCreationTime_DoesNotLink()
    {
        var ev1000 = MakeApp1000(101, BaseUtc, pidHex: "0x2a10", createTimeHex: "0x1db401234567890", reportId: null);
        var ev1001 = MakeWer1001(102, BaseUtc.AddSeconds(15), "APPCRASH", "SampleGame.exe", reportId: null, pidHex: "0x2a10", procCreationHex: "0x1db499999999999");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, ev1001]);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, static c => c.AnchorKind == IncidentAnchorKind.ApplicationCrash && c.LinkedWerEvidence is null);
        Assert.Contains(result.Candidates, static c => c.AnchorKind == IncidentAnchorKind.WerReportOnly);
    }

    [Fact]
    public void Correlate_NameOnlyAdjacentWerWithoutPathOrPidCreation_DoesNotLink()
    {
        var ev1000 = MakeApp1000(101, BaseUtc, reportId: null);
        var ev1001 = MakeWer1001(102, BaseUtc.AddSeconds(2), "APPCRASH", "SampleGame.exe", reportId: null);

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, ev1001]);

        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Correlate_SameNameDifferentPaths_RemainSeparateWithDisambiguatedSuffixes()
    {
        var ev1 = MakeApp1000(101, BaseUtc, appName: "Game.exe", appPath: @"D:\Games\steamapps\common\Beta\Game.exe", reportId: "11111111-1111-1111-1111-111111111111");
        var ev2 = MakeApp1000(102, BaseUtc.AddMinutes(-5), appName: "Game.exe", appPath: @"E:\Epic Games\Prod\Game.exe", reportId: "22222222-2222-2222-2222-222222222222");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1, ev2]);

        Assert.Equal(2, result.Candidates.Count);
        Assert.NotEqual(result.Candidates[0].DisambiguatedPathSuffix, result.Candidates[1].DisambiguatedPathSuffix);
    }

    [Fact]
    public void Correlate_LateWerBeyond120sWithoutReportId_RemainsStandalone()
    {
        var ev1000 = MakeApp1000(101, BaseUtc, reportId: null);
        var ev1001 = MakeWer1001(102, BaseUtc.AddSeconds(180), "APPCRASH", "SampleGame.exe", reportId: null, appPath: @"D:\Games\steamapps\common\SampleGame\SampleGame.exe");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, ev1001]);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, static c => c.AnchorKind == IncidentAnchorKind.WerReportOnly);
    }

    [Fact]
    public void Correlate_AmbiguousMultiAnchorMatch_KeepsWerStandaloneWithAmbiguityNote()
    {
        var a1 = MakeApp1000(101, BaseUtc, reportId: null);
        var a2 = MakeApp1000(102, BaseUtc.AddSeconds(2), reportId: null);
        var wer = MakeWer1001(103, BaseUtc.AddSeconds(5), "APPCRASH", "SampleGame.exe", reportId: null, appPath: @"D:\Games\steamapps\common\SampleGame\SampleGame.exe");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [a1, a2, wer]);

        // a1 and a2 aggregate into 1 crash candidate (count=2), plus 1 standalone WER candidate with ambiguity note.
        Assert.Equal(2, result.Candidates.Count);
        var werOnly = Assert.Single(result.Candidates, static c => c.AnchorKind == IncidentAnchorKind.WerReportOnly);
        Assert.NotEmpty(werOnly.AmbiguityNotes);
    }

    [Fact]
    public void Correlate_CrossChannelSameRecordId_AreNotDeduplicatedTogether()
    {
        var app1000 = MakeApp1000(500, BaseUtc);
        var kp41SameRecordId = MakeKp41(500, BaseUtc.AddMinutes(-1));

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [app1000, kp41SameRecordId]);

        Assert.Single(result.Candidates);
        Assert.Single(result.SystemBackgroundEvents);
    }

    [Fact]
    public void Correlate_IsolatedKernelPower41_ProducesZeroCrashCandidates()
    {
        var kp41 = MakeKp41(501, BaseUtc);

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [kp41]);

        Assert.Empty(result.Candidates);
        Assert.Single(result.SystemBackgroundEvents);
    }

    [Fact]
    public void Correlate_RepeatedCrashesInWindow_AggregatesOccurrenceCountAndTimestamps()
    {
        var c1 = MakeApp1000(101, BaseUtc.AddMinutes(-30), reportId: "11111111-1111-1111-1111-111111111111");
        var c2 = MakeApp1000(102, BaseUtc.AddMinutes(-15), reportId: "22222222-2222-2222-2222-222222222222");
        var c3 = MakeApp1000(103, BaseUtc, reportId: "33333333-3333-3333-3333-333333333333");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [c1, c2, c3]);

        var inc = Assert.Single(result.Candidates);
        Assert.Equal(3, inc.OccurrenceCountInWindow);
        Assert.Equal(3, inc.OccurrenceTimestampsUtc.Count);
        Assert.Equal(BaseUtc, inc.OccurredAtUtc);
    }

    [Fact]
    public void Correlate_SystemNoiseFilteredByDefaultAndRestoredWhenIncludeAllTrue()
    {
        var noise = MakeApp1000(101, BaseUtc, appName: "SearchHost.exe", appPath: @"C:\Windows\SystemApps\SearchHost.exe");
        var game = MakeApp1000(102, BaseUtc.AddMinutes(-10), appName: "MyGame.exe", appPath: @"D:\Games\steamapps\common\MyGame\MyGame.exe");

        var defaultRes = IncidentCorrelator.Correlate(DefaultRequest(includeAll: false), [noise, game]);
        Assert.Single(defaultRes.Candidates);
        Assert.Equal(1, defaultRes.FilteredNoiseCount);
        Assert.Equal("MyGame.exe", defaultRes.Candidates[0].AppName);

        var allRes = IncidentCorrelator.Correlate(DefaultRequest(includeAll: true), [noise, game]);
        Assert.Equal(2, allRes.Candidates.Count);
        Assert.Equal(0, allRes.FilteredNoiseCount);
        Assert.Equal("MyGame.exe", allRes.Candidates[0].AppName); // likely game prioritized first
    }

    [Fact]
    public void Correlate_ReversedWerOrder_StillLinksUniqueReportIdDirectAndBlocksConflictingWeakMatch()
    {
        var ev1000 = MakeApp1000(101, BaseUtc, reportId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var werWeakFirst = MakeWer1001(
            102,
            BaseUtc.AddSeconds(2),
            "APPCRASH",
            "SampleGame.exe",
            reportId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            appPath: @"D:\Games\steamapps\common\SampleGame\SampleGame.exe");
        var werDirectSecond = MakeWer1001(
            103,
            BaseUtc.AddSeconds(4),
            "APPCRASH",
            "SampleGame.exe",
            reportId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            appPath: @"D:\Games\steamapps\common\SampleGame\SampleGame.exe");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [ev1000, werWeakFirst, werDirectSecond]);

        var crashInc = Assert.Single(result.Candidates, static c => c.AnchorKind == IncidentAnchorKind.ApplicationCrash);
        Assert.Equal(AttributionStrength.Direct, crashInc.WerLinkAttribution);
        Assert.Single(result.Candidates, static c => c.AnchorKind == IncidentAnchorKind.WerReportOnly);
    }

    [Fact]
    public void Correlate_RepeatedIncidents_RetainsEarlierWerEvidenceWhenLatestAnchorUnlinked()
    {
        var older1000 = MakeApp1000(101, BaseUtc.AddMinutes(-20), reportId: "11111111-1111-1111-1111-111111111111");
        var olderWer = MakeWer1001(102, BaseUtc.AddMinutes(-20).AddSeconds(2), "APPCRASH", "SampleGame.exe", "11111111-1111-1111-1111-111111111111");
        var newer1000 = MakeApp1000(103, BaseUtc, reportId: "22222222-2222-2222-2222-222222222222");

        var result = IncidentCorrelator.Correlate(DefaultRequest(), [older1000, olderWer, newer1000]);

        var inc = Assert.Single(result.Candidates);
        Assert.Equal(2, inc.OccurrenceCountInWindow);
        Assert.Equal(AttributionStrength.Direct, inc.WerLinkAttribution);
        Assert.NotNull(inc.LinkedWerEvidence);
    }
}
