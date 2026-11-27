using System.Security.Cryptography;
using System.Text;
using GameCrashKit.Core.Exporters;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;
using GameCrashKit.Core.Rules;
using GameCrashKit.Windows.FileSystem;
using Xunit;

namespace GameCrashKit.Windows.Tests;

public sealed class M4AcceptanceAuditTests
{
    [Fact]
    public void SourceLogFile_HashAndTimestampUnchangedAfterRead()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_m4_hash_");
        try
        {
            var path = Path.Combine(tempDir.FullName, "Player.log");
            File.WriteAllText(
                path,
                "Initialize engine version: 2022.3.20f1c1\nD3D11: DXGI_ERROR_DEVICE_REMOVED\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var beforeWriteUtc = File.GetLastWriteTimeUtc(path);

            var result = EngineLogDiscovery.LoadAndParseUserSelectedFile(path, EngineLogKind.Unity, mayBeOverwritten: true);

            var afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var afterWriteUtc = File.GetLastWriteTimeUtc(path);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(beforeHash, afterHash);
            Assert.Equal(beforeWriteUtc, afterWriteUtc);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void StaticAudit_SrcDirectoryHasZeroOutboundNetworkApis()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        string[] forbidden =
        [
            "HttpClient",
            "WebClient",
            "HttpWebRequest",
            "TcpClient",
            "UdpClient",
            "System.Net.Http",
            "System.Net.Sockets"
        ];

        AssertNoForbiddenTokens(srcDir, forbidden);
    }

    [Fact]
    public void StaticAudit_CoreAndWindowsLayersHaveZeroWriteOrRegistryMutationApis()
    {
        var repoRoot = FindRepoRoot();
        string[] forbidden =
        [
            "File.WriteAll",
            "File.Create",
            "FileMode.Create",
            "FileMode.OpenOrCreate",
            "CreateSubKey",
            "SetValue(",
            "DeleteSubKey",
            "DeleteValue("
        ];

        AssertNoForbiddenTokens(Path.Combine(repoRoot, "src", "GameCrashKit.Core"), forbidden);
        AssertNoForbiddenTokens(Path.Combine(repoRoot, "src", "GameCrashKit.Windows"), forbidden);
    }

    [Fact]
    public void SafeLocalFileReader_LockedOrMissingFile_DegradesWithoutThrowing()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_m4_lock_");
        try
        {
            var missing = SafeLocalFileReader.ReadTailUtf8(Path.Combine(tempDir.FullName, "missing.log"));
            Assert.False(missing.Success);
            Assert.Equal(SafeFileReadErrorKind.FileNotFound, missing.ErrorKind);

            var lockedPath = Path.Combine(tempDir.FullName, "locked.log");
            File.WriteAllText(lockedPath, "locked content");
            using var exclusiveLock = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var locked = SafeLocalFileReader.ReadTailUtf8(lockedPath);
            Assert.False(locked.Success);
            Assert.True(locked.ErrorKind is SafeFileReadErrorKind.ReadFailed or SafeFileReadErrorKind.AccessDenied);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void EndToEndDeterminism_FixedReportProducesByteIdenticalMarkdown()
    {
        var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var req = new ScanRequest(now.AddHours(-2), now);
        var ev = new EvidenceItem(
            "ev-1",
            EvidenceSourceKind.WindowsApplicationCrash1000,
            now.AddMinutes(-10),
            "EventSystemTimeUtc",
            "Application / Application Error / ID 1000 (v0) / RecordId 12",
            "1.0.0",
            new Dictionary<string, string> { ["AppName"] = "DeterministicGame.exe" },
            false);

        var inc = new CrashIncident(
            "inc-1",
            IncidentAnchorKind.ApplicationCrash,
            now.AddMinutes(-10),
            "DeterministicGame.exe",
            @"D:\Games\steamapps\common\Det\DeterministicGame.exe",
            @"...\common\Det\DeterministicGame.exe",
            "DeterministicGame.exe",
            @"D:\Games\steamapps\common\Det\DeterministicGame.exe",
            "0xc0000005",
            "0x100",
            "0x1",
            null,
            null,
            true,
            false,
            true,
            1,
            [now.AddMinutes(-10)],
            AttributionStrength.Unlinked,
            ev,
            null,
            [],
            null,
            []);

        var engine = new DiagnosisRuleEngine();
        var r1 = engine.Evaluate(new RuleEvaluationContext(req, inc, [], [], null), [ev], "0.1.0-alpha.1", now);
        var r2 = engine.Evaluate(new RuleEvaluationContext(req, inc, [], [], null), [ev], "0.1.0-alpha.1", now);

        var md1 = MarkdownReportExporter.Render(ShareReportProjector.Project(r1, new ShareOptions(), ScrubContext.Empty, TimeZoneInfo.Utc));
        var md2 = MarkdownReportExporter.Render(ShareReportProjector.Project(r2, new ShareOptions(), ScrubContext.Empty, TimeZoneInfo.Utc));

        Assert.Equal(md1, md2);
    }

    [Fact]
    public void TestFirstRunTrustScript_AbortsWithZeroExtractionWhenSha256Mismatches()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "Test-FirstRunTrust.ps1");
        Assert.True(File.Exists(scriptPath), $"Script missing: {scriptPath}");

        var tempDir = Directory.CreateTempSubdirectory("gck_trust_mismatch_");
        try
        {
            var zipPath = Path.Combine(tempDir.FullName, "GameCrashKit-v0.2.0-alpha.3-win-x64.zip");
            File.WriteAllBytes(zipPath, [0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
            var badSumPath = Path.Combine(tempDir.FullName, "GameCrashKit-v0.2.0-alpha.3-win-x64.SHA256SUMS.txt");
            File.WriteAllText(badSumPath, $"{new string('0', 64)}  GameCrashKit-v0.2.0-alpha.3-win-x64.zip\n");

            var outputDir = Path.Combine(tempDir.FullName, "out");
            var workDir = Path.Combine(tempDir.FullName, "work");

            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -VersionLabel \"v0.2.0-alpha.3\" -OutputDir \"{outputDir}\" -WorkDir \"{workDir}\" -LocalZipPath \"{zipPath}\" -LocalChecksumPath \"{badSumPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(proc);
            Assert.True(proc!.WaitForExit(30_000));
            Assert.NotEqual(0, proc.ExitCode);

            var reportPath = Path.Combine(outputDir, "firstrun-trust-report.txt");
            var flagPath = Path.Combine(outputDir, "firstrun-trust-done.flag");
            Assert.True(File.Exists(reportPath));
            Assert.True(File.Exists(flagPath));
            Assert.StartsWith("FAIL ", File.ReadAllText(flagPath), StringComparison.Ordinal);

            var report = File.ReadAllText(reportPath);
            Assert.True(report.Contains("Match=False", StringComparison.Ordinal), $"Report missing Match=False:\n{report}");
            Assert.DoesNotContain("User=", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($@"Users\{System.Environment.UserName}", report, StringComparison.OrdinalIgnoreCase);
            if (!"IsAdmin".Contains(System.Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain(System.Environment.UserName, report, StringComparison.OrdinalIgnoreCase);
            }
            Assert.False(Directory.Exists(Path.Combine(workDir, "extracted-explorer")));
            Assert.False(Directory.Exists(Path.Combine(workDir, "extracted-pwsh")));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static void AssertNoForbiddenTokens(string directory, string[] forbiddenTokens)
    {
        Assert.True(Directory.Exists(directory), $"Directory not found: {directory}");
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(static p =>
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var token in forbiddenTokens)
                {
                    Assert.False(
                        lines[i].Contains(token, StringComparison.Ordinal),
                        $"Forbidden token '{token}' found in {file}:{i + 1}");
                }
            }
        }
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
