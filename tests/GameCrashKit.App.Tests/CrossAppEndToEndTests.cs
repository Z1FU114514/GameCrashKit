using System.IO;
using System.Security.Cryptography;
using GameCrashKit.App.ViewModels;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;
using Xunit;

namespace GameCrashKit.App.Tests;

[Collection("WpfApp")]
public sealed class CrossAppEndToEndTests
{
    public static TheoryData<string, string, int> CrossAppMatrix() => new()
    {
        { "obs-d3d11-device-removed.txt", "OBS Studio 日志", 1 },
        { "obs-normal-session.txt", "OBS Studio 日志", 0 },
        { "blender-4.2-av.crash.txt", "Blender 崩溃日志", 1 },
        { "blender-4.2-cycles-oom.log", "Blender 崩溃日志", 1 },
        { "comfyui-desktop-cuda-oom.log", "ComfyUI 日志", 1 },
        { "comfyui-desktop-python-av.log", "ComfyUI 日志", 1 }
    };

    private static (string Markdown, byte[] PngBytes) RenderShareOutputsOnSta(DiagnosisReport report, ScrubContext scrubContext)
    {
        string markdown = string.Empty;
        byte[] pngBytes = [];
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                WpfTestHost.EnsureResourcesLoaded();
                var preview = new SharePreviewViewModel(report, scrubContext)
                {
                    HideAppName = true,
                    HideExactTimestamp = true,
                    IncludeScrubbedErrorExcerpt = true
                };
                Assert.True(preview.HasValidPng, preview.PngRenderErrorMessage);
                markdown = preview.MarkdownText;
                pngBytes = preview.GetCurrentPngBytes()!;
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
            throw new InvalidOperationException("STA cross-app share render failed.", captured);
        }

        return (markdown, pngBytes);
    }

    [Theory]
    [MemberData(nameof(CrossAppMatrix))]
    public async Task ColdStartCrossAppFixture_ProducesExportablePiiFreeCardsWithoutMutatingSource(
        string fixtureName,
        string expectedFormatLabel,
        int expectedMinSignals)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CrossAppLogs", fixtureName);
        Assert.True(File.Exists(fixturePath), $"Cross-app fixture missing: {fixturePath}");
        var hashBefore = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixturePath)));

        using var vm = new MainViewModel();
        await vm.AttachManualLogFileAsync(fixturePath);

        Assert.True(vm.HasReport);
        Assert.NotNull(vm.CurrentReport);
        Assert.Equal(ReportSubjectKind.StandaloneLogOnly, vm.CurrentReport!.SubjectKind);
        Assert.NotNull(vm.CurrentReport.StandaloneLog);
        Assert.Equal(expectedFormatLabel, vm.CurrentReport.StandaloneLog!.EffectiveFormatLabel);
        Assert.True(vm.CurrentReport.StandaloneLog.EffectiveSignals.Count >= expectedMinSignals);

        var scrub = new ScrubContext("SecretStreamer", "X", @"C:\Users\SecretStreamer");
        var (md, png) = RenderShareOutputsOnSta(vm.CurrentReport, scrub);

        Assert.Contains("来源：手选日志（独立诊断）", md);
        Assert.Contains("扫描范围", md);
        Assert.DoesNotContain("SecretStreamer", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretArtist", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretDev", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("obs_secret_tok", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret_comfy_key", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("my_private_addon", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretClientScene", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.168.1.55", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a9326cb1a8cb", md, StringComparison.OrdinalIgnoreCase);

        Assert.True(png.Length > 1000);
        Assert.Equal((byte)0x89, png[0]);

        var hashAfter = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixturePath)));
        Assert.Equal(hashBefore, hashAfter);
    }
}
