using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GameCrashKit.App.Rendering;
using GameCrashKit.App.ViewModels;
using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Exporters;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;
using GameCrashKit.Windows.FileSystem;
using GameCrashKit.Windows.Scanning;
using Xunit;

namespace GameCrashKit.App.Tests;

[Collection("WpfApp")]
public sealed class V02StandaloneLogAcceptanceTests
{
    private static void EnsureWpfResources() => WpfTestHost.EnsureResourcesLoaded();

    private static TResult RunOnSta<TResult>(Func<TResult> action)
    {
        TResult? result = default;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureWpfResources();
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured is not null)
        {
            throw new InvalidOperationException("STA execution failed", captured);
        }

        return result!;
    }

    private static (string Markdown, byte[] PngBytes) RenderBothShareOutputs(
        DiagnosisReport report,
        ScrubContext scrubContext,
        ShareOptions options) => RunOnSta(() =>
        {
            var vm = new SharePreviewViewModel(report, scrubContext);
            vm.HideAppName = options.HideAppName;
            vm.HideExactTimestamp = options.HideExactTimestamp;
            vm.IncludeScrubbedErrorExcerpt = options.IncludeScrubbedErrorExcerpt;
            Assert.True(vm.HasValidPng, vm.PngRenderErrorMessage);
            return (vm.MarkdownText, vm.GetCurrentPngBytes()!);
        });

    [Fact]
    public async Task Row1_FullUeFatalWithDxgiTokenAndZeroWindowsEvents_ProducesStandaloneExportableCardWithoutFabricatedWindowsFields()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_v02_row1_");
        try
        {
            var xmlPath = Path.Combine(tempDir.FullName, "CrashContext.runtime-xml");
            File.WriteAllText(xmlPath, """
                <FGenericCrashContext>
                  <RuntimeProperties>
                    <EngineVersion>5.4.4-0+++UE5+PrivateTestBranch-CL-4242</EngineVersion>
                    <RHI>D3D12</RHI>
                    <CrashType>Fatal</CrashType>
                    <ErrorMessage>Fatal error: D3D12 DXGI_ERROR_DEVICE_REMOVED at 21:05:11</ErrorMessage>
                  </RuntimeProperties>
                </FGenericCrashContext>
                """);

            using var vm = new MainViewModel();
            // Cold start: never pressed F5.
            await vm.AttachManualLogFileAsync(xmlPath);

            Assert.True(vm.HasReport);
            Assert.NotNull(vm.CurrentReport);
            Assert.Equal(ReportSubjectKind.StandaloneLogOnly, vm.CurrentReport!.SubjectKind);
            Assert.Contains(vm.PrimaryFindings, static f => f.RuleTag.StartsWith("ENGINE-GPU-LOST", StringComparison.Ordinal));
            Assert.Contains(vm.PrimaryFindings, static f => f.RuleTag.StartsWith("LOG-ONLY-SUMMARY", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.PrimaryFindings, static f => f.RuleTag.StartsWith("UNKNOWN", StringComparison.Ordinal));

            var (md, png) = RenderBothShareOutputs(
                vm.CurrentReport!,
                new ScrubContext("张三", "X", @"C:\Users\张三"),
                new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true));

            Assert.Contains("来源：手选日志（独立诊断）", md);
            Assert.Contains("扫描范围", md);
            Assert.Contains("未扫描", md);
            Assert.Contains("采集受限与数据边界说明", md);
            Assert.Contains("致命崩溃", md);
            Assert.Contains("DXGI_ERROR_DEVICE_REMOVED", md);
            Assert.Contains("不适用（无 Windows 事件锚点）", md);
            Assert.DoesNotContain("PrivateTestBranch", md);
            Assert.DoesNotContain("Event 1000", md);
            Assert.True(png.Length > 1000);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Row2_UeNonFatalEnsureWithOomToken_LabelsEnsureAndEmitsZeroSystemSettingAdvice()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_v02_row2_");
        try
        {
            var xmlPath = Path.Combine(tempDir.FullName, "CrashContext.runtime-xml");
            File.WriteAllText(xmlPath, """
                <FGenericCrashContext>
                  <RuntimeProperties>
                    <CrashType>Ensure</CrashType>
                    <IsEnsure>true</IsEnsure>
                    <ErrorMessage>Ensure condition failed: Out of video memory trying to allocate</ErrorMessage>
                  </RuntimeProperties>
                </FGenericCrashContext>
                """);

            using var vm = new MainViewModel();
            await vm.AttachManualLogFileAsync(xmlPath);

            Assert.NotNull(vm.CurrentReport);
            Assert.Equal(ReportSubjectKind.StandaloneLogOnly, vm.CurrentReport!.SubjectKind);
            Assert.Contains(vm.PrimaryFindings, static f => f.Title.Contains("非致命 Ensure", StringComparison.Ordinal));
            Assert.All(vm.PrimaryFindings, static f => Assert.DoesNotContain("降低高显存纹理", f.NextActionText));

            var (md, png) = RenderBothShareOutputs(
                vm.CurrentReport!,
                ScrubContext.Empty,
                new ShareOptions(IncludeScrubbedErrorExcerpt: true));

            Assert.Contains("非致命 Ensure", md);
            Assert.Contains("Out of video memory", md);
            Assert.DoesNotContain("建议恢复系统托管页面文件", md);
            Assert.True(png.Length > 1000);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Row3_OversizedUnityNumericCodePlusOversizedAndEmptyUeXml_HandleCleanly()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_v02_row3_");
        try
        {
            // >2 MiB Unity log with numeric code 0x887A0005 only at tail
            var largeUnityPath = Path.Combine(tempDir.FullName, "Player.log");
            var filler = Encoding.UTF8.GetBytes("2026-11-27 [INFO] Padding line for 2.2 MiB truncation boundary test.\n");
            using (var fs = new FileStream(largeUnityPath, FileMode.Create, FileAccess.Write))
            {
                long written = 0;
                while (written < SafeLocalFileReader.MaxTailReadBytes + 200_000)
                {
                    fs.Write(filler, 0, filler.Length);
                    written += filler.Length;
                }
                var tail = Encoding.UTF8.GetBytes("GfxDevice error code 0x887A0005 at tail\n");
                fs.Write(tail, 0, tail.Length);
            }

            using var vm = new MainViewModel();
            await vm.AttachManualLogFileAsync(largeUnityPath);

            Assert.NotNull(vm.CurrentReport);
            var (md, _) = RenderBothShareOutputs(vm.CurrentReport!, ScrubContext.Empty, new ShareOptions(IncludeScrubbedErrorExcerpt: true));
            Assert.Contains("0x887A0005", md);
            Assert.Contains("仅检查尾段 2 MiB", md);
            Assert.Contains("采集受限与数据边界说明", md);
            Assert.Contains("超出 2 MiB 只读上限", md);

            // Empty UE XML & oversized UE XML -> controlled failure without throwing or clearing UI readiness
            var emptyXmlPath = Path.Combine(tempDir.FullName, "EmptyCrashContext.runtime-xml");
            File.WriteAllText(emptyXmlPath, "   ");
            await vm.AttachManualLogFileAsync(emptyXmlPath);
            Assert.Contains("日志未加载", vm.EngineLogFeedbackMessage);

            var largeXmlPath = Path.Combine(tempDir.FullName, "TooBigCrashContext.runtime-xml");
            var xmlBody = new string('B', SafeLocalFileReader.MaxTailReadBytes + 512);
            File.WriteAllText(largeXmlPath, $"<FGenericCrashContext><ErrorMessage>{xmlBody}</ErrorMessage></FGenericCrashContext>");
            await vm.AttachManualLogFileAsync(largeXmlPath);
            Assert.Contains("不完整", vm.EngineLogFeedbackMessage);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Row4_GenericNonGameLogs_WithSignalAndClean_BothProduceExportableMinimalReports()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_v02_row4_");
        try
        {
            var signalLog = Path.Combine(tempDir.FullName, "blender_gpu.log");
            File.WriteAllText(signalLog, "2026-11-27 18:00:00 [ERROR] Vulkan device error: VK_ERROR_DEVICE_LOST");

            var cleanLog = Path.Combine(tempDir.FullName, "plain_app.log");
            File.WriteAllText(cleanLog, "2026-11-27 18:05:00 [INFO] User account=player123 exited normally on host X");

            using var vm = new MainViewModel();
            await vm.AttachManualLogFileAsync(signalLog);
            var (mdSignal, pngSignal) = RenderBothShareOutputs(vm.CurrentReport!, ScrubContext.Empty, new ShareOptions(IncludeScrubbedErrorExcerpt: true));
            Assert.Contains("通用文本日志", mdSignal);
            Assert.Contains("VK_ERROR_DEVICE_LOST", mdSignal);
            Assert.Contains("致命性未知", mdSignal);
            Assert.True(pngSignal.Length > 1000);

            await vm.AttachManualLogFileAsync(cleanLog);
            var (mdClean, pngClean) = RenderBothShareOutputs(vm.CurrentReport!, new ScrubContext("张三", "X", @"C:\Users\张三"), new ShareOptions(IncludeScrubbedErrorExcerpt: true));
            Assert.Contains("未命中已知标记", mdClean);
            Assert.DoesNotContain("player123", mdClean);
            Assert.True(pngClean.Length > 1000);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Row5_SwitchingBetweenIncidentAIncidentBAndStandaloneLog_PreventsLeakageAndPreservesSourceHash()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_v02_row5_");
        try
        {
            var logPath = Path.Combine(tempDir.FullName, "HostilePlayer.log");
            File.WriteAllText(logPath, """
                Initialize engine version: 2022.3.20f1c1 (PrivateSecretCommit99)
                Version: Direct3D 11 [level 11.1]
                host X user 张三 account="my_secret_acct" token='tok_999' 21:44:55 D3D11: DXGI_ERROR_DEVICE_REMOVED
                """);
            var hashBefore = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(logPath)));

            var now = DateTimeOffset.UtcNow;
            var req = new ScanRequest(now.AddHours(-2), now);
            var evA = new EvidenceItem("ev-a", EvidenceSourceKind.WindowsApplicationCrash1000, now.AddMinutes(-2), "EventSystemTimeUtc", "App/1000/1", "1.0.0", new Dictionary<string, string>(), false);
            var evB = new EvidenceItem("ev-b", EvidenceSourceKind.WindowsApplicationCrash1000, now.AddMinutes(-8), "EventSystemTimeUtc", "App/1000/2", "1.0.0", new Dictionary<string, string>(), false);

            var incA = new CrashIncident("inc-A", IncidentAnchorKind.ApplicationCrash, now.AddMinutes(-2), "AppA.exe", @"D:\Users\张三\AppA\AppA.exe", @"...\AppA\AppA.exe", "AppA.exe", null, "0xc0000005", "0x1", "0x1", null, null, false, false, true, 1, [now.AddMinutes(-2)], AttributionStrength.Unlinked, evA, null, [], null, []);
            var incB = new CrashIncident("inc-B", IncidentAnchorKind.ApplicationCrash, now.AddMinutes(-8), "AppB.exe", @"D:\Users\张三\AppB\AppB.exe", @"...\AppB\AppB.exe", "AppB.exe", null, "0xc0000005", "0x2", "0x2", null, null, false, false, true, 1, [now.AddMinutes(-8)], AttributionStrength.Unlinked, evB, null, [], null, []);

            var session = new ScanSessionResult(false, false, false, req, new CorrelateResult([incA, incB], 0, [], [evA, evB]), [], null, []);
            using var vm = new MainViewModel();
            vm.LoadSessionForTest(session);

            // Attach log to A
            await vm.AttachManualLogFileAsync(logPath);
            var scrub = new ScrubContext("张三", "X", @"C:\Users\张三");
            var shareOpts = new ShareOptions(HideAppName: true, HideExactTimestamp: true, IncludeScrubbedErrorExcerpt: true);

            var (mdA, pngA) = RenderBothShareOutputs(vm.CurrentReport!, scrub, shareOpts);
            Assert.Contains("DXGI_ERROR_DEVICE_REMOVED", mdA);

            // Switch to B -> B must NOT inherit A's log or DXGI token
            vm.SelectedCandidate = vm.Candidates[1];
            var (mdB, pngB) = RenderBothShareOutputs(vm.CurrentReport!, scrub, shareOpts);
            Assert.DoesNotContain("DXGI_ERROR_DEVICE_REMOVED", mdB);
            Assert.DoesNotContain("AppB", mdB);

            // Switch to Standalone Log view -> standalone report has log token, zero AppA/AppB fabrication, zero PII
            vm.ViewStandaloneLogCommand.Execute(null);
            Assert.True(vm.IsViewingStandaloneLog);
            var (mdStandalone, pngStandalone) = RenderBothShareOutputs(vm.CurrentReport!, scrub, shareOpts);
            Assert.Contains("DXGI_ERROR_DEVICE_REMOVED", mdStandalone);
            Assert.DoesNotContain("AppA", mdStandalone);
            Assert.DoesNotContain("AppB", mdStandalone);

            foreach (var doc in new[] { mdA, mdB, mdStandalone })
            {
                Assert.DoesNotContain("PrivateSecretCommit99", doc);
                Assert.DoesNotContain("my_secret_acct", doc);
                Assert.DoesNotContain("tok_999", doc);
                Assert.DoesNotContain("张三", doc);
                Assert.DoesNotContain("21:44:55", doc);
            }

            Assert.True(pngA.Length > 1000 && pngB.Length > 1000 && pngStandalone.Length > 1000);
            var hashAfter = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(logPath)));
            Assert.Equal(hashBefore, hashAfter);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
