using GameCrashKit.App.Rendering;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;
using GameCrashKit.Core.Rules;
using Xunit;

namespace GameCrashKit.App.Tests;

public sealed class EmergencyCardPngRendererTests
{
    [Fact]
    public void RenderToPngBytes_OnStaThread_ProducesValid900px96DpiPngMatchingPreviewDecode()
    {
        PngRenderResult? renderResult = null;
        ShareReportModel? projectedModel = null;
        int decodedPixelWidth = 0;
        int decodedPixelHeight = 0;
        double decodedDpiX = 0;
        double decodedDpiY = 0;
        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                projectedModel = BuildSampleShareModel(includeExcerpt: true);
                renderResult = EmergencyCardPngRenderer.RenderToPngBytes(projectedModel);

                var preview = EmergencyCardPngRenderer.CreateFrozenBitmapImage(renderResult.PngBytes);
                decodedPixelWidth = preview.PixelWidth;
                decodedPixelHeight = preview.PixelHeight;
                decodedDpiX = preview.DpiX;
                decodedDpiY = preview.DpiY;
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(staException);
        Assert.NotNull(projectedModel);
        Assert.False(string.IsNullOrWhiteSpace(projectedModel!.ScanScopeSummary));
        Assert.NotEmpty(projectedModel.SourceLimitationNotes!);
        Assert.NotNull(renderResult);
        Assert.Equal(EmergencyCardPngRenderer.CardWidth, renderResult!.PixelWidth);
        Assert.InRange(renderResult.PixelHeight, 500, 2400);
        Assert.True(renderResult.PngBytes.Length > 10_000, "PNG output unexpectedly tiny.");

        // Verify valid PNG magic bytes: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal((byte)0x89, renderResult.PngBytes[0]);
        Assert.Equal((byte)0x50, renderResult.PngBytes[1]);
        Assert.Equal((byte)0x4E, renderResult.PngBytes[2]);
        Assert.Equal((byte)0x47, renderResult.PngBytes[3]);

        // Verify WYSIWYG preview decodes identically at 96 DPI
        Assert.Equal(EmergencyCardPngRenderer.CardWidth, decodedPixelWidth);
        Assert.Equal(renderResult.PixelHeight, decodedPixelHeight);
        Assert.Equal(96.0, decodedDpiX, precision: 1);
        Assert.Equal(96.0, decodedDpiY, precision: 1);
    }

    private static ShareReportModel BuildSampleShareModel(bool includeExcerpt)
    {
        var nowUtc = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);
        var req = new ScanRequest(nowUtc.AddHours(-2), nowUtc);
        var ev = new EvidenceItem(
            "ev-1",
            EvidenceSourceKind.WindowsApplicationCrash1000,
            nowUtc.AddMinutes(-5),
            "EventSystemTimeUtc",
            "Application / Application Error / ID 1000 (v0) / RecordId 42",
            "1.0.0",
            new Dictionary<string, string> { ["AppName"] = "SampleChineseGame.exe" },
            false);

        var engineLog = new UserConfirmedEngineLog(
            EngineLogKind.Unity,
            "Player.log",
            "Player.log (Bytes 0..500 / 500)",
            nowUtc,
            false,
            true,
            "2022.3.20f1c1",
            "Direct3D 11",
            true,
            "DXGI_ERROR_DEVICE_REMOVED",
            false,
            null,
            ["D3D11: Failed to create RenderTexture, error DXGI_ERROR_DEVICE_REMOVED"]);

        var inc = new CrashIncident(
            "inc-1",
            IncidentAnchorKind.ApplicationCrash,
            nowUtc.AddMinutes(-5),
            "SampleChineseGame.exe",
            @"D:\Games\steamapps\common\SampleChineseGame\SampleChineseGame.exe",
            @"...\common\SampleChineseGame\SampleChineseGame.exe",
            "d3d11.dll",
            @"C:\Windows\System32\d3d11.dll",
            "0xc0000005",
            "0x1000",
            "0x1db400",
            null,
            null,
            true,
            false,
            true,
            2,
            [nowUtc.AddMinutes(-5), nowUtc.AddMinutes(-25)],
            AttributionStrength.Unlinked,
            ev,
            null,
            [],
            engineLog,
            []);

        var env = new EnvironmentSnapshot(
            nowUtc,
            [new GpuAdapterFact("NVIDIA GeForce RTX 5060", "32.0.16.1664", "2026-08-24", false)],
            new TargetPathFact(true, false, false, 64, @"...\common\SampleChineseGame\SampleChineseGame.exe"),
            new OneDriveDocsFact(true, false, false, EnvironmentNoticeLevel.Info, "未指向 OneDrive"),
            new VcRuntimeFact(true, "v14.51.36247.00", true, "v14.51.36247.00", EnvironmentNoticeLevel.Info),
            new PageFileFact(true, false, 51200, 2, false, EnvironmentNoticeLevel.Info, "51200 MB"),
            new RunningOverlaysFact(true, [], "扫描瞬间未发现常见覆盖层"),
            new SystemLocaleAndSecurityFact("Windows 11 25H2 (Build 26200.9457)", 26200, 32768, 936, false, true),
            []);

        var report = new DiagnosisRuleEngine().Evaluate(
            new RuleEvaluationContext(req, inc, [], [new SourceCoverage("Application", req.WindowStartUtc, req.WindowEndUtc, 12, false, SourceCoverageStatus.Ok, null)], env),
            [ev],
            appVersion: "0.1.0-alpha",
            generatedAtUtc: nowUtc);

        return ShareReportProjector.Project(
            report,
            new ShareOptions(IncludeScrubbedErrorExcerpt: includeExcerpt),
            ScrubContext.Empty,
            TimeZoneInfo.Utc);
    }
}
