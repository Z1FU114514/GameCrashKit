using GameCrashKit.Core.Models;
using GameCrashKit.Core.Parsers;
using GameCrashKit.Core.Privacy;
using GameCrashKit.Core.Rules;
using GameCrashKit.Core.Exporters;
using Xunit;

namespace GameCrashKit.Core.Tests;

public sealed class CrossAppLogAdapterTests
{
    private static readonly ScrubContext HostileScrub = new(
        CurrentUserName: "SecretStreamer",
        CurrentMachineName: "X",
        UserProfilePath: @"C:\Users\SecretStreamer");

    private static string LoadCrossAppFixture(string fileName)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CrossAppLogs", fileName);
        Assert.True(File.Exists(fullPath), $"Cross-app fixture not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static DiagnosisReport EvaluateStandalone(UserConfirmedEngineLog log)
    {
        var now = new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
        var req = new ScanRequest(now.AddHours(-2), now, IncludeAllApplications: false, MaxEventsPerChannel: 0);
        return new DiagnosisRuleEngine().Evaluate(
            new RuleEvaluationContext(
                Request: req,
                SelectedIncident: null,
                SystemBackgroundEvents: [],
                Coverage: [],
                Environment: null,
                StandaloneLog: log,
                SubjectKind: ReportSubjectKind.StandaloneLogOnly,
                WindowsScanStateLabel: "未扫描"),
            allEvidence: [],
            generatedAtUtc: now);
    }

    [Fact]
    public void ObsD3d11DeviceRemoved_ExtractsObsAdapterAndTriggersGpuLostRule()
    {
        var content = LoadCrossAppFixture("obs-d3d11-device-removed.txt");
        var parsed = EngineLogParser.ParseGenericManualLog(content, "obs.txt", "obs.txt", DateTimeOffset.UtcNow, isTruncated: false);

        Assert.Equal("OBS Studio 日志", parsed.EffectiveFormatLabel);
        Assert.Equal("30.2.3", parsed.EngineVersion);
        Assert.Equal("Direct3D 11", parsed.RhiBackend);
        Assert.Equal(LogFatalityKind.Unknown, parsed.Fatality);
        Assert.True(parsed.HasGpuDeviceLostSignal);
        Assert.Contains(parsed.EffectiveSignals, static s => s.SignalId == LogSignalId.GpuDeviceLost);

        var report = EvaluateStandalone(parsed);
        var gpuFinding = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-GPU-LOST");
        Assert.Equal(EvidenceStrength.LimitedClue, gpuFinding.EvidenceStrength);
        Assert.Contains(report.PrimaryFindings, static f => f.RuleId == "LOG-ONLY-SUMMARY");

        var md = MarkdownReportExporter.Render(ShareReportProjector.Project(
            report,
            new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true),
            HostileScrub,
            TimeZoneInfo.Utc));
        Assert.Contains("30.2.3", md);
        Assert.Contains("Direct3D 11", md);
        Assert.DoesNotContain("OBS", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretStreamer", md);
        Assert.DoesNotContain("obs_secret_tok", md);
        Assert.DoesNotContain("192.168.1.55", md);
    }

    [Fact]
    public void ObsNormalSession_NegativeControl_EmitsZeroCrashSignalsOrFalsePositives_AndHidesAppLabel()
    {
        var content = LoadCrossAppFixture("obs-normal-session.txt");
        var parsed = EngineLogParser.ParseGenericManualLog(content, "obs-ok.txt", "obs-ok.txt", DateTimeOffset.UtcNow, isTruncated: false);

        Assert.Equal("OBS Studio 日志", parsed.EffectiveFormatLabel);
        Assert.Equal(LogFatalityKind.Unknown, parsed.Fatality);
        Assert.Empty(parsed.EffectiveSignals);
        Assert.False(parsed.HasGpuDeviceLostSignal);
        Assert.False(parsed.HasOutOfMemorySignal);

        var report = EvaluateStandalone(parsed);
        var finding = Assert.Single(report.PrimaryFindings);
        Assert.Equal("LOG-ONLY-SUMMARY", finding.RuleId);
        Assert.Contains("未命中已知标记", finding.Title);

        foreach (var includeExcerpt in new[] { false, true })
        {
            var mdHidden = MarkdownReportExporter.Render(ShareReportProjector.Project(
                report,
                new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: includeExcerpt),
                HostileScrub,
                TimeZoneInfo.Utc));
            Assert.DoesNotContain("OBS", mdHidden, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<已隐藏>", mdHidden);
        }
    }

    [Fact]
    public void Blender42AccessViolation_ExtractsFatalAccessViolationAndStripsBacktraceAddons()
    {
        var content = LoadCrossAppFixture("blender-4.2-av.crash.txt");
        var parsed = EngineLogParser.ParseGenericManualLog(content, "blender.crash.txt", "blender.crash.txt", DateTimeOffset.UtcNow, isTruncated: false);

        Assert.Equal("Blender 崩溃日志", parsed.EffectiveFormatLabel);
        Assert.Equal("4.2.0", parsed.EngineVersion);
        Assert.Equal(LogFatalityKind.Fatal, parsed.Fatality);
        Assert.Contains(parsed.EffectiveSignals, static s =>
            s.SignalId == LogSignalId.AccessViolation && s.MatchedToken == "EXCEPTION_ACCESS_VIOLATION");
        Assert.All(parsed.RawErrorExcerptLines, static l => Assert.DoesNotContain("my_private_addon.py", l));
        Assert.All(parsed.RawErrorExcerptLines, static l => Assert.DoesNotContain("file_execute_get_description", l));

        var report = EvaluateStandalone(parsed);
        var md = MarkdownReportExporter.Render(ShareReportProjector.Project(
            report,
            new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true),
            new ScrubContext("SecretArtist", "X", @"C:\Users\SecretArtist"),
            TimeZoneInfo.Utc));
        Assert.Contains("EXCEPTION_ACCESS_VIOLATION", md);
        Assert.DoesNotContain("my_private_addon.py", md);
        Assert.DoesNotContain("SecretArtist", md);
    }

    [Fact]
    public void Blender42CyclesOom_StaysLimitedClueWithoutMainProcessExitProofAndMasksPrivateBlendPath()
    {
        var content = LoadCrossAppFixture("blender-4.2-cycles-oom.log");
        var parsed = EngineLogParser.ParseGenericManualLog(content, "blender.log", "blender.log", DateTimeOffset.UtcNow, isTruncated: false);

        Assert.Equal("Blender 崩溃日志", parsed.EffectiveFormatLabel);
        Assert.Equal(LogFatalityKind.Unknown, parsed.Fatality);
        Assert.True(parsed.HasOutOfMemorySignal);
        Assert.Contains(parsed.EffectiveSignals, static s => s.SignalId == LogSignalId.OutOfMemory);

        var report = EvaluateStandalone(parsed);
        var oom = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-OOM");
        Assert.Equal(EvidenceStrength.LimitedClue, oom.EvidenceStrength);
        Assert.DoesNotContain("页面文件", oom.NextActionText);

        var md = MarkdownReportExporter.Render(ShareReportProjector.Project(
            report,
            new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true),
            new ScrubContext("SecretArtist", "X", @"C:\Users\SecretArtist"),
            TimeZoneInfo.Utc));
        Assert.DoesNotContain("SecretClientScene", md);
        Assert.DoesNotContain("SecretArtist", md);
        Assert.DoesNotContain("Blender", md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlenderStackOverflowWithAvStringInsideStackTrace_DoesNotMisclassifyAsAccessViolation()
    {
        const string nonAvBlender = """
            # Blender 4.2.0, Commit date: 2024-07-16 06:20, Hash a9326cb1a8cb
            # backtrace
            Exception Record:
            ExceptionCode         : EXCEPTION_STACK_OVERFLOW
            Exception Module      : blender.exe

            Stack trace:
            blender.exe :0x00007FF72DAB7400  handle_EXCEPTION_ACCESS_VIOLATION_wrapper
            """;

        var parsed = EngineLogParser.ParseGenericManualLog(nonAvBlender, "blender.crash.txt", "blender.crash.txt", null, false);
        Assert.Equal(LogFatalityKind.Fatal, parsed.Fatality);
        Assert.DoesNotContain(parsed.EffectiveSignals, static s => s.SignalId == LogSignalId.AccessViolation);
    }

    [Fact]
    public void ComfyUiCudaOom_StaysLimitedClueWithoutMainExitProofAndMasksApiKey()
    {
        var content = LoadCrossAppFixture("comfyui-desktop-cuda-oom.log");
        var parsed = EngineLogParser.ParseGenericManualLog(content, "comfy.log", "comfy.log", DateTimeOffset.UtcNow, isTruncated: false);

        Assert.Equal("ComfyUI 日志", parsed.EffectiveFormatLabel);
        Assert.Equal("0.4.2", parsed.EngineVersion);
        Assert.Equal(LogFatalityKind.Unknown, parsed.Fatality);
        Assert.True(parsed.HasOutOfMemorySignal);

        var report = EvaluateStandalone(parsed);
        var oom = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-OOM");
        Assert.Equal(EvidenceStrength.LimitedClue, oom.EvidenceStrength);

        var md = MarkdownReportExporter.Render(ShareReportProjector.Project(
            report,
            new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true),
            new ScrubContext("SecretDev", "X", @"C:\Users\SecretDev"),
            TimeZoneInfo.Utc));
        Assert.Contains("CUDA out of memory", md);
        Assert.DoesNotContain("ComfyUI", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_comfy_key", md);
        Assert.DoesNotContain("SecretDev", md);
    }

    [Fact]
    public void ComfyUiPythonExit3221225477_MapsToChildProcessExitAndSupportsColonVariant()
    {
        var content = LoadCrossAppFixture("comfyui-desktop-python-av.log");
        var parsed = EngineLogParser.ParseGenericManualLog(content, "comfy-av.log", "comfy-av.log", DateTimeOffset.UtcNow, isTruncated: false);

        Assert.Equal("ComfyUI 日志", parsed.EffectiveFormatLabel);
        Assert.Equal(LogFatalityKind.ChildProcessExit, parsed.Fatality);
        Assert.False(parsed.HasOutOfMemorySignal);
        Assert.Contains(parsed.EffectiveSignals, static s =>
            s.SignalId == LogSignalId.AccessViolation &&
            s.MatchedToken.Contains("Python 子进程", StringComparison.Ordinal) &&
            s.MatchedToken.Contains("3221225477", StringComparison.Ordinal));

        var report = EvaluateStandalone(parsed);
        Assert.DoesNotContain(report.PrimaryFindings, static f => f.RuleId == "ENGINE-OOM");
        var summary = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "LOG-ONLY-SUMMARY");
        Assert.Equal(EvidenceStrength.LimitedClue, summary.EvidenceStrength);
        Assert.Contains("子进程异常退出", summary.Title);

        var colonVariant = content.Replace("exited with code 3221225477", "exited with code: 3221225477", StringComparison.Ordinal);
        var parsedColon = EngineLogParser.ParseGenericManualLog(colonVariant, "comfy-colon.log", "comfy-colon.log", null, false);
        Assert.Equal(LogFatalityKind.ChildProcessExit, parsedColon.Fatality);
        Assert.Contains(parsedColon.EffectiveSignals, static s => s.SignalId == LogSignalId.AccessViolation);
    }
}
