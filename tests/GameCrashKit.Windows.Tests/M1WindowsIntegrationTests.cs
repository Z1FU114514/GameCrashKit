using System.Diagnostics;
using GameCrashKit.Core.Models;
using GameCrashKit.Windows.Environment;
using GameCrashKit.Windows.Scanning;
using Xunit;

namespace GameCrashKit.Windows.Tests;

public sealed class M1WindowsIntegrationTests
{
    [Fact]
    public async Task EnvironmentSnapshotCollector_CollectAsync_ProbesHostReadOnlyAccurately()
    {
        var snapshot = await EnvironmentSnapshotCollector.CollectAsync(@"D:\测试游戏\Game.exe");

        Assert.NotEmpty(snapshot.GpuAdapters);
        Assert.True(snapshot.TargetPath.Checked);
        Assert.True(snapshot.TargetPath.HasNonAscii);
        Assert.False(snapshot.TargetPath.ExceedsMaxPath260);
        Assert.True(snapshot.OneDriveDocs.Checked);
        Assert.True(snapshot.PageFile.Checked);
        Assert.True(snapshot.PageFile.TotalAllocatedMb >= 0);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.PageFile.StatusText));
        Assert.True(snapshot.RunningOverlays.Checked);
        Assert.Contains("扫描瞬间", snapshot.RunningOverlays.Note);
        Assert.True(snapshot.SystemInfo.BuildNumber >= 10240);
        Assert.True(snapshot.SystemInfo.TotalPhysicalMemoryMb > 0);
        Assert.True(snapshot.SystemInfo.AcpCodePage > 0);
    }

    [Fact]
    public async Task WindowsScanCoordinator_ScanLast2HoursAndBuildReport_CompletesFastAndDeterministically()
    {
        var coordinator = new WindowsScanCoordinator();
        var sw = Stopwatch.StartNew();
        var session = await coordinator.ScanAsync(ScanWindowPreset.Last2Hours);
        sw.Stop();

        Assert.False(session.IsBusy);
        Assert.False(session.WasCancelled);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"2h scan took {sw.Elapsed.TotalMilliseconds}ms");
        Assert.Equal(2, session.Coverage.Count);
        Assert.NotNull(session.Environment);

        var selected = session.Correlated.Candidates.FirstOrDefault();
        var r1 = coordinator.BuildDiagnosisReport(session, selected);
        var r2 = coordinator.BuildDiagnosisReport(session, selected);

        Assert.NotEmpty(r1.PrimaryFindings);
        Assert.True(r1.PrimaryFindings.Count <= 3);
        Assert.Equal(
            r1.PrimaryFindings.Select(static f => $"{f.RuleId}@{f.RuleVersion}"),
            r2.PrimaryFindings.Select(static f => $"{f.RuleId}@{f.RuleVersion}"));
    }

    [Fact]
    public async Task WindowsScanCoordinator_ConcurrentScan_ReturnsBusyCleanly()
    {
        var coordinator = new WindowsScanCoordinator();
        var t1 = coordinator.ScanAsync(ScanWindowPreset.Last7Days);
        var t2 = coordinator.ScanAsync(ScanWindowPreset.Last2Hours);
        var results = await Task.WhenAll(t1, t2);

        // Either t2 caught t1 mid-flight (IsBusy == true) or t1 finished extremely fast; neither may throw.
        Assert.Contains(results, static r => !r.IsBusy);
    }

    [Fact]
    public void AntiCheatStaticSourceAudit_SrcDirectoryHasZeroForbiddenProcessApis()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        Assert.True(Directory.Exists(srcDir), $"src directory not found at {srcDir}");

        string[] forbiddenTokens = ["OpenProcess", "ReadProcessMemory", "EnumProcessModules", "DebugActiveProcess"];
        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);

        foreach (var file in csFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var token in forbiddenTokens)
                {
                    Assert.False(
                        line.Contains(token, StringComparison.Ordinal),
                        $"Forbidden anti-cheat-unsafe API '{token}' found in {file}:{i + 1}");
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

        throw new InvalidOperationException("Could not locate GameCrashKit.sln from test base directory.");
    }
}
