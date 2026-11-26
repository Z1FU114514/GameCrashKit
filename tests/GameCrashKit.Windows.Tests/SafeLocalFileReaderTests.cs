using System.Diagnostics;
using System.Text;
using GameCrashKit.Core.Models;
using GameCrashKit.Windows.FileSystem;
using GameCrashKit.Windows.Scanning;
using Xunit;

namespace GameCrashKit.Windows.Tests;

public sealed class SafeLocalFileReaderTests
{
    [Fact]
    public void ReadTailUtf8_WithAndWithoutBom_ReadsAccurately()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_bom_");
        try
        {
            var bomPath = Path.Combine(tempDir.FullName, "with_bom.log");
            var noBomPath = Path.Combine(tempDir.FullName, "no_bom.log");

            File.WriteAllText(bomPath, "第一行中文\nError: test line", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(noBomPath, "Line1\nLine2", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var r1 = SafeLocalFileReader.ReadTailUtf8(bomPath);
            var r2 = SafeLocalFileReader.ReadTailUtf8(noBomPath);

            Assert.True(r1.Success);
            Assert.False(r1.IsTruncated);
            Assert.NotNull(r1.Content);
            Assert.StartsWith("第一行中文", r1.Content);
            Assert.DoesNotContain('\uFEFF', r1.Content);

            Assert.True(r2.Success);
            Assert.Equal("Line1\nLine2", r2.Content);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadTailUtf8_Large2_5MiBChineseFile_TruncatesToTail2MiBOnCleanLineBoundary()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_large_");
        try
        {
            var largePath = Path.Combine(tempDir.FullName, "large_player.log");
            var lineBytes = Encoding.UTF8.GetBytes("2026-11-26 [INFO] 游戏引擎日志填充行测试中文多字节边界对齐检查。\n");

            using (var fs = new FileStream(largePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var targetBytes = (long)(2.5 * 1024 * 1024);
                long written = 0;
                while (written < targetBytes)
                {
                    fs.Write(lineBytes, 0, lineBytes.Length);
                    written += lineBytes.Length;
                }

                var tailMarker = Encoding.UTF8.GetBytes("DXGI_ERROR_DEVICE_REMOVED_TAIL_MARKER\n");
                fs.Write(tailMarker, 0, tailMarker.Length);
            }

            var result = SafeLocalFileReader.ReadTailUtf8(largePath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.IsTruncated);
            Assert.True(result.ReadEndByteOffset - result.ReadStartByteOffset <= SafeLocalFileReader.MaxTailReadBytes);
            Assert.NotNull(result.Content);
            Assert.Contains("DXGI_ERROR_DEVICE_REMOVED_TAIL_MARKER", result.Content);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadTailUtf8_InvalidUtf8Bytes_RejectsWithInvalidUtf8Encoding()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_badutf8_");
        try
        {
            var badPath = Path.Combine(tempDir.FullName, "ansi_gbk.log");
            // Invalid UTF-8 bytes without any UTF-16 BOM (GBK "你好")
            File.WriteAllBytes(badPath, [0xC4, 0xE3, 0xBA, 0xC3]);

            var result = SafeLocalFileReader.ReadTailUtf8(badPath);

            Assert.False(result.Success);
            Assert.Equal(SafeFileReadErrorKind.InvalidUtf8Encoding, result.ErrorKind);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadTailUtf8_Utf16LeWithBom_DecodesAccurately()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_utf16_");
        try
        {
            var path = Path.Combine(tempDir.FullName, "utf16.log");
            var payload = Encoding.Unicode.GetPreamble()
                .Concat(Encoding.Unicode.GetBytes("Initialize engine version: 2022.3.20f1c1\n中文崩溃测试"))
                .ToArray();
            File.WriteAllBytes(path, payload);

            var result = SafeLocalFileReader.ReadTailUtf8(path);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Content);
            Assert.Contains("Initialize engine version: 2022.3.20f1c1", result.Content);
            Assert.Contains("中文崩溃测试", result.Content);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadAndParseUserSelectedFile_TruncatedRuntimeXml_RejectsWithoutGenericFallback()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_trunc_xml_");
        try
        {
            var xmlPath = Path.Combine(tempDir.FullName, "CrashContext.runtime-xml");
            var filler = new string('A', SafeLocalFileReader.MaxTailReadBytes + 1024);
            File.WriteAllText(xmlPath, $"<FGenericCrashContext><ErrorMessage>{filler}</ErrorMessage></FGenericCrashContext>");

            var result = EngineLogDiscovery.LoadAndParseUserSelectedFile(xmlPath);

            Assert.False(result.Success);
            Assert.Contains("不完整", result.ErrorMessage);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"\\192.168.1.10\share\Player.log")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public void ReadTailUtf8_UncAndDevicePaths_AreRejected(string unsafePath)
    {
        var result = SafeLocalFileReader.ReadTailUtf8(unsafePath);

        Assert.False(result.Success);
        Assert.Equal(SafeFileReadErrorKind.NetworkOrDevicePathRejected, result.ErrorKind);
    }

    [Fact]
    public void ReadTailUtf8_RealNtfsDirectoryJunction_RejectsReparsePoint()
    {
        var tempRoot = Directory.CreateTempSubdirectory("gck_junction_");
        var targetDir = Path.Combine(tempRoot.FullName, "target");
        var junctionDir = Path.Combine(tempRoot.FullName, "junction_link");
        Directory.CreateDirectory(targetDir);
        var logFile = Path.Combine(targetDir, "Player.log");
        File.WriteAllText(logFile, "Initialize engine version: 2022.3.20f1c1\n");

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionDir}\" \"{targetDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi)!;
            proc.WaitForExit(5_000);
            Assert.Equal(0, proc.ExitCode);

            var pathThroughJunction = Path.Combine(junctionDir, "Player.log");
            var result = SafeLocalFileReader.ReadTailUtf8(pathThroughJunction);

            Assert.False(result.Success);
            Assert.Equal(SafeFileReadErrorKind.ReparsePointRejected, result.ErrorKind);
        }
        finally
        {
            if (Directory.Exists(junctionDir))
            {
                Directory.Delete(junctionDir, recursive: false);
            }

            if (Directory.Exists(tempRoot.FullName))
            {
                tempRoot.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public void EngineLogDiscovery_DiscoverInWindow_RunsReadOnlyWithoutThrowing()
    {
        var candidates = EngineLogDiscovery.DiscoverInWindow(DateTimeOffset.UtcNow.AddDays(-7));
        Assert.NotNull(candidates);
    }

    [Fact]
    public async Task EngineLogDiscovery_LoadUnityFixtureAndEvaluate_TriggersEngineGpuLostRule()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = Path.Combine(repoRoot, "tests", "Fixtures", "EngineLogs", "unity-player-gpu-lost.log");

        var loadResult = EngineLogDiscovery.LoadAndParseUserSelectedFile(fixturePath, EngineLogKind.Unity, mayBeOverwritten: true);
        Assert.True(loadResult.Success, loadResult.ErrorMessage);
        Assert.NotNull(loadResult.EngineLog);

        var coordinator = new WindowsScanCoordinator();
        var session = await coordinator.ScanAsync(ScanWindowPreset.Last7Days);

        var anchor = session.Correlated.Candidates.FirstOrDefault() ?? new CrashIncident(
            IncidentId: "synthetic-anchor",
            AnchorKind: IncidentAnchorKind.ApplicationCrash,
            OccurredAtUtc: DateTimeOffset.UtcNow,
            AppName: "SampleUnityGame.exe",
            NormalizedAppPath: @"D:\Games\steamapps\common\SampleUnityGame\SampleUnityGame.exe",
            DisambiguatedPathSuffix: @"...\common\SampleUnityGame\SampleUnityGame.exe",
            ModuleName: "d3d11.dll",
            ModulePath: @"C:\Windows\System32\d3d11.dll",
            ExceptionCode: "0xc0000005",
            FaultingProcessIdHex: "0x100",
            ProcessCreationTimeHex: "0x1",
            ReportId: null,
            HangType: null,
            IsLikelyGame: true,
            IsSystemNoise: false,
            IsSupportedSchemaVersion: true,
            OccurrenceCountInWindow: 1,
            OccurrenceTimestampsUtc: [DateTimeOffset.UtcNow],
            WerLinkAttribution: AttributionStrength.Unlinked,
            PrimaryEvidence: new EvidenceItem("ev-s", EvidenceSourceKind.WindowsApplicationCrash1000, DateTimeOffset.UtcNow, "EventSystemTimeUtc", "Application / 1000", "1.0.0", new Dictionary<string, string>(), false),
            LinkedWerEvidence: null,
            NearbySystemBackground: [],
            ConfirmedEngineLog: null,
            AmbiguityNotes: []);

        var report = coordinator.BuildDiagnosisReport(session, anchor, loadResult.EngineLog);

        var gpuFinding = Assert.Single(report.PrimaryFindings, static f => f.RuleId == "ENGINE-GPU-LOST");
        Assert.Equal(AttributionStrength.UserSelected, gpuFinding.Attribution);
        Assert.Equal(EvidenceStrength.ExplicitRecord, gpuFinding.EvidenceStrength);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GameCrashKit.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate GameCrashKit.sln.");
    }
}
