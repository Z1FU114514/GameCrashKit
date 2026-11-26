using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameCrashKit.App.Rendering;
using GameCrashKit.Core.Exporters;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;
using GameCrashKit.Windows.Environment;
using GameCrashKit.Windows.FileSystem;
using GameCrashKit.Windows.Scanning;

namespace SandboxAutoVerifier;

internal static class Program
{
    private sealed record VerifierOptions(
        string AppDir,
        string SampleLogPath,
        string OutputDir,
        string ExpectedVersion);

    private sealed class CheckRecorder
    {
        private readonly List<string> _lines = [];

        public bool HasFailure { get; private set; }

        public void LogInfo(string message)
        {
            var line = $"[INFO] {message}";
            Console.WriteLine(line);
            _lines.Add(line);
        }

        public void Record(string name, bool passed, string detail)
        {
            var status = passed ? "PASS" : "FAIL";
            if (!passed)
            {
                HasFailure = true;
            }

            var line = $"[{status}] {name}: {detail}";
            Console.WriteLine(line);
            _lines.Add(line);
        }

        public string BuildReportText() => string.Join(Environment.NewLine, _lines) + Environment.NewLine;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var recorder = new CheckRecorder();
        VerifierOptions? options = null;

        try
        {
            options = ParseArgs(args);
            Directory.CreateDirectory(options.OutputDir);
            RunVerification(options, recorder);
        }
        catch (Exception ex)
        {
            recorder.Record("FATAL", false, $"{ex.GetType().Name}: {ex.Message}");
            recorder.LogInfo(ex.ToString());
        }

        if (options is not null)
        {
            try
            {
                Directory.CreateDirectory(options.OutputDir);
                var reportPath = Path.Combine(options.OutputDir, "sandbox-report.txt");
                File.WriteAllText(reportPath, recorder.BuildReportText(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                var flagPath = Path.Combine(options.OutputDir, "sandbox-done.flag");
                var flagStatus = recorder.HasFailure ? "FAIL" : "PASS";
                File.WriteAllText(
                    flagPath,
                    $"{flagStatus} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception writeEx)
            {
                Console.Error.WriteLine($"Failed to write sandbox output files: {writeEx}");
                return 2;
            }
        }

        return recorder.HasFailure ? 1 : 0;
    }

    private static VerifierOptions ParseArgs(string[] args)
    {
        string? appDir = null;
        string? sampleLog = null;
        string? outputDir = null;
        var expectedVersion = "0.1.0-alpha.1";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app-dir" when i + 1 < args.Length:
                    appDir = args[++i];
                    break;
                case "--sample-log" when i + 1 < args.Length:
                    sampleLog = args[++i];
                    break;
                case "--output-dir" when i + 1 < args.Length:
                    outputDir = args[++i];
                    break;
                case "--expected-version" when i + 1 < args.Length:
                    expectedVersion = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(appDir) ||
            string.IsNullOrWhiteSpace(sampleLog) ||
            string.IsNullOrWhiteSpace(outputDir))
        {
            throw new ArgumentException(
                "Usage: SandboxAutoVerifier --app-dir <dir> --sample-log <file> --output-dir <dir> [--expected-version <ver>]");
        }

        return new VerifierOptions(
            Path.GetFullPath(appDir),
            Path.GetFullPath(sampleLog),
            Path.GetFullPath(outputDir),
            expectedVersion);
    }

    private static void RunVerification(VerifierOptions options, CheckRecorder recorder)
    {
        recorder.LogInfo($"GameCrashKit Unattended Sandbox Verifier | Target Version: {options.ExpectedVersion}");
        recorder.LogInfo($"TimestampUtc: {DateTimeOffset.UtcNow:O}");

        // Check 1: Sandbox environment & clean artifact gate
        RunEnvironmentAndArtifactChecks(options, recorder);

        // Check 2: Real Event 1000 capture & read-only sample log invariance
        var coordinator = new WindowsScanCoordinator();
        var scanSession = coordinator
            .ScanAsync(ScanWindowPreset.Last2Hours, includeAllApplications: true, softDeadlineMs: 10_000)
            .GetAwaiter()
            .GetResult();

        var syntheticIncident = scanSession.Correlated.Candidates.FirstOrDefault(c =>
            string.Equals(c.AppName, "SyntheticCrashTool.exe", StringComparison.OrdinalIgnoreCase) &&
            c.AnchorKind == IncidentAnchorKind.ApplicationCrash &&
            !string.IsNullOrWhiteSpace(c.ExceptionCode) &&
            c.ExceptionCode.Contains("0xc0000005", StringComparison.OrdinalIgnoreCase));

        recorder.Record(
            "Check2.RealCrashCapture",
            !scanSession.IsBusy &&
            !scanSession.WasCancelled &&
            !scanSession.TimedOut &&
            syntheticIncident is not null,
            syntheticIncident is not null
                ? $"Captured SyntheticCrashTool.exe AnchorKind={syntheticIncident.AnchorKind}, ExceptionCode={syntheticIncident.ExceptionCode}, OccurredAtUtc={syntheticIncident.OccurredAtUtc:O}, TotalCandidates={scanSession.Correlated.Candidates.Count}"
                : $"SyntheticCrashTool.exe (0xc0000005) not found. Candidates=[{string.Join(", ", scanSession.Correlated.Candidates.Select(c => $"{c.AppName}:{c.ExceptionCode}"))}], Warnings=[{string.Join("; ", scanSession.ParseWarnings)}]");

        var confirmedLog = VerifyReadOnlySampleLog(options.SampleLogPath, recorder);

        // Check 3: Rule engine coverage (ENGINE-GPU-LOST@1, EXCEPTION-AV@1, APP-CRASH@1)
        DiagnosisReport? reportWithLog = null;
        if (syntheticIncident is not null)
        {
            reportWithLog = coordinator.BuildDiagnosisReport(
                scanSession,
                syntheticIncident,
                confirmedLog,
                options.ExpectedVersion);

            var reportWithoutLog = coordinator.BuildDiagnosisReport(
                scanSession,
                syntheticIncident,
                confirmedEngineLog: null,
                options.ExpectedVersion);

            var firedWithLog = reportWithLog.PrimaryFindings
                .Select(f => $"{f.RuleId}@{f.RuleVersion}")
                .ToArray();
            var firedWithoutLog = reportWithoutLog.PrimaryFindings
                .Select(f => $"{f.RuleId}@{f.RuleVersion}")
                .ToArray();
            var allFired = firedWithLog.Concat(firedWithoutLog).Distinct(StringComparer.Ordinal).ToArray();

            var hasGpuLost = firedWithLog.Contains("ENGINE-GPU-LOST@1", StringComparer.Ordinal);
            var hasAv = allFired.Contains("EXCEPTION-AV@1", StringComparer.Ordinal);
            var hasAppCrash = allFired.Contains("APP-CRASH@1", StringComparer.Ordinal);

            recorder.Record(
                "Check3.RuleEngineCoverage",
                hasGpuLost && hasAv && hasAppCrash,
                $"WithLog=[{string.Join(", ", firedWithLog)}], WithoutLog=[{string.Join(", ", firedWithoutLog)}]");
        }
        else
        {
            recorder.Record("Check3.RuleEngineCoverage", false, "Skipped because SyntheticCrashTool.exe incident was not captured.");
        }

        // Check 4: Export fidelity & privacy scrubbing (Markdown + 96 DPI 900px white-canvas PNG)
        if (reportWithLog is not null)
        {
            VerifyExportsAndPrivacy(reportWithLog, options.OutputDir, recorder);
        }
        else
        {
            recorder.Record("Check4.ExportAndPrivacy", false, "Skipped because diagnosis report was not generated.");
        }

        // Check 5: 20-run real-host latency benchmark (P95 <= 5000 ms)
        RunPerformanceBenchmark(coordinator, recorder);

        // Check 6: Portable GUI smoke launch (leaves GUI open on Sandbox desktop for visual review)
        VerifyPortableGuiLaunch(options.AppDir, recorder);
    }

    private static void RunEnvironmentAndArtifactChecks(VerifierOptions options, CheckRecorder recorder)
    {
        var osInfo = HostOsVersionHelper.ReadCurrentHost();
        var userName = Environment.UserName;
        var machineName = Environment.MachineName;

        bool isOffline;
        string networkDetail;
        try
        {
            var addresses = Dns.GetHostAddresses("example.com");
            isOffline = false;
            networkDetail = $"DNS unexpectedly resolved example.com ({addresses.Length} address(es))";
        }
        catch (Exception ex)
        {
            isOffline = true;
            networkDetail = $"DNS resolution blocked/offline ({ex.GetType().Name})";
        }

        var desktopRuntimeBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "shared",
            "Microsoft.WindowsDesktop.App");
        var hasPreinstalledNet10 = Directory.Exists(desktopRuntimeBase) &&
            Directory.EnumerateDirectories(desktopRuntimeBase, "10.*").Any();

        var isSandboxUser = string.Equals(userName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase);
        recorder.LogInfo($"Environment: User={userName}, Machine={machineName}, OS={osInfo.DisplayLabel}, Offline={isOffline} ({networkDetail}), HostNet10Preinstalled={hasPreinstalledNet10}");

        recorder.Record(
            "Check1a.OfflineNoNet10Environment",
            isOffline && !hasPreinstalledNet10,
            $"SandboxUser={isSandboxUser}, Offline={isOffline}, HostNet10Preinstalled={hasPreinstalledNet10}, OS={osInfo.DisplayLabel}");

        var appExe = Path.Combine(options.AppDir, "GameCrashKit.App.exe");
        var appExists = File.Exists(appExe);
        var leakedFiles = Directory.Exists(options.AppDir)
            ? Directory.EnumerateFiles(options.AppDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) &&
                    (name.Contains("SyntheticCrashTool", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("SandboxAutoVerifier", StringComparison.OrdinalIgnoreCase)))
                .ToArray()
            : ["<appDir missing>"];

        recorder.Record(
            "Check1b.CleanPortableArtifact",
            appExists && leakedFiles.Length == 0,
            $"AppDir={options.AppDir}, ExeExists={appExists}, LeakedTestBinaries=[{string.Join(", ", leakedFiles)}]");
    }

    private static UserConfirmedEngineLog? VerifyReadOnlySampleLog(string sampleLogPath, CheckRecorder recorder)
    {
        if (!File.Exists(sampleLogPath))
        {
            recorder.Record("Check2b.SampleLogReadOnly", false, $"Sample log does not exist: {sampleLogPath}");
            return null;
        }

        var hashBefore = ComputeSha256(sampleLogPath);
        var mtimeBefore = File.GetLastWriteTimeUtc(sampleLogPath);
        var attrsBefore = File.GetAttributes(sampleLogPath);

        var loadResult = EngineLogDiscovery.LoadAndParseUserSelectedFile(sampleLogPath);

        var hashAfter = ComputeSha256(sampleLogPath);
        var mtimeAfter = File.GetLastWriteTimeUtc(sampleLogPath);
        var attrsAfter = File.GetAttributes(sampleLogPath);

        var invariantsPreserved =
            string.Equals(hashBefore, hashAfter, StringComparison.Ordinal) &&
            mtimeBefore == mtimeAfter &&
            attrsBefore == attrsAfter;

        recorder.Record(
            "Check2b.SampleLogReadOnly",
            loadResult.Success && loadResult.EngineLog is not null && loadResult.EngineLog.HasGpuDeviceLostSignal && invariantsPreserved,
            $"Success={loadResult.Success}, Kind={loadResult.EngineLog?.Kind}, GpuLost={loadResult.EngineLog?.HasGpuDeviceLostSignal}, ExcerptLines={loadResult.EngineLog?.RawErrorExcerptLines.Count ?? 0}, Sha256Preserved={hashBefore == hashAfter}, MtimePreserved={mtimeBefore == mtimeAfter}, Error={loadResult.ErrorMessage ?? "None"}");

        return loadResult.EngineLog;
    }

    private static void VerifyExportsAndPrivacy(DiagnosisReport report, string outputDir, CheckRecorder recorder)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var scrubContext = new ScrubContext(
            CurrentUserName: Environment.UserName,
            CurrentMachineName: Environment.MachineName,
            UserProfilePath: userProfile);

        var shareModel = ShareReportProjector.Project(
            report,
            new ShareOptions(
                HideAppName: false,
                HideExactTimestamp: false,
                IncludeScrubbedErrorExcerpt: true),
            scrubContext,
            TimeZoneInfo.Local);

        var markdown = MarkdownReportExporter.Render(shareModel);
        var mdPath = Path.Combine(outputDir, "sandbox-report.md");
        File.WriteAllText(mdPath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var piiViolations = FindPiiLeaks(markdown, Environment.UserName, Environment.MachineName, userProfile);
        recorder.Record(
            "Check4a.MarkdownPrivacyScrub",
            markdown.Length > 100 && piiViolations.Count == 0,
            $"Path={mdPath}, Bytes={Encoding.UTF8.GetByteCount(markdown)}, Leaks=[{string.Join(", ", piiViolations)}]");

        EnsureWpfThemeResourcesLoaded();
        var pngResult = EmergencyCardPngRenderer.RenderToPngBytes(shareModel);
        var pngPath = Path.Combine(outputDir, "sandbox-emergency-card.png");
        File.WriteAllBytes(pngPath, pngResult.PngBytes);

        var hasValidSignature = HasPngSignature(pngResult.PngBytes);
        var decoded = EmergencyCardPngRenderer.CreateFrozenBitmapImage(pngResult.PngBytes);
        var dpiOk = Math.Abs(decoded.DpiX - EmergencyCardPngRenderer.TargetDpi) < 0.1 &&
                    Math.Abs(decoded.DpiY - EmergencyCardPngRenderer.TargetDpi) < 0.1;
        var bodySampleY = Math.Clamp(200, 10, Math.Max(10, decoded.PixelHeight - 10));
        var isLightBodyCanvas = SamplePixelIsLightCanvas(decoded, 10, bodySampleY, out var bodyRgba);

        recorder.Record(
            "Check4b.PngEmergencyCard",
            hasValidSignature &&
            pngResult.PixelWidth == EmergencyCardPngRenderer.CardWidth &&
            pngResult.PixelHeight > 0 &&
            dpiOk &&
            isLightBodyCanvas,
            $"Path={pngPath}, Size={pngResult.PixelWidth}x{pngResult.PixelHeight}, DPI={decoded.DpiX:F1}x{decoded.DpiY:F1}, BodyCanvasRGBA(10,{bodySampleY})={bodyRgba}, Bytes={pngResult.PngBytes.Length}");
    }

    private static void RunPerformanceBenchmark(WindowsScanCoordinator coordinator, CheckRecorder recorder)
    {
        const int runs = 20;
        var timingsMs = new List<double>(runs);

        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            var session = coordinator
                .ScanAsync(ScanWindowPreset.Last2Hours, includeAllApplications: true, softDeadlineMs: 10_000)
                .GetAwaiter()
                .GetResult();
            sw.Stop();

            if (session.IsBusy || session.WasCancelled)
            {
                recorder.Record("Check5.LatencyBenchmark20Runs", false, $"Run {i + 1} unexpected state Busy={session.IsBusy}, Cancelled={session.WasCancelled}");
                return;
            }

            timingsMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        timingsMs.Sort();
        var p95Index = (int)Math.Ceiling(0.95 * runs) - 1;
        var p95Ms = timingsMs[Math.Clamp(p95Index, 0, timingsMs.Count - 1)];
        var minMs = timingsMs[0];
        var maxMs = timingsMs[^1];
        var avgMs = timingsMs.Average();

        recorder.Record(
            "Check5.LatencyBenchmark20Runs",
            p95Ms <= 5_000.0,
            $"Runs={runs}, Min={minMs:F1}ms, Avg={avgMs:F1}ms, P95={p95Ms:F1}ms, Max={maxMs:F1}ms (Target <= 5000ms)");
    }

    private static void VerifyPortableGuiLaunch(string appDir, CheckRecorder recorder)
    {
        var exePath = Path.Combine(appDir, "GameCrashKit.App.exe");
        if (!File.Exists(exePath))
        {
            recorder.Record("Check6.PortableGuiSmokeLaunch", false, $"Executable not found: {exePath}");
            return;
        }

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = appDir,
            UseShellExecute = true
        });

        if (proc is null)
        {
            recorder.Record("Check6.PortableGuiSmokeLaunch", false, "Process.Start returned null.");
            return;
        }

        try
        {
            proc.WaitForInputIdle(5_000);
        }
        catch (InvalidOperationException)
        {
            // Ignore if process has no message queue yet; polling loop below handles window readiness.
        }

        var deadline = Stopwatch.StartNew();
        var hasMainWindow = false;
        while (deadline.ElapsedMilliseconds < 10_000)
        {
            proc.Refresh();
            if (proc.HasExited)
            {
                break;
            }

            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                hasMainWindow = true;
                break;
            }

            Thread.Sleep(200);
        }

        proc.Refresh();
        var isAlive = !proc.HasExited;
        recorder.Record(
            "Check6.PortableGuiSmokeLaunch",
            isAlive && hasMainWindow,
            $"PID={proc.Id}, HasExited={proc.HasExited}, MainWindowHandle=0x{proc.MainWindowHandle.ToInt64():X}, StartupWaitMs={deadline.ElapsedMilliseconds} (left open for inspection)");
    }

    private static List<string> FindPiiLeaks(string text, string userName, string machineName, string userProfile)
    {
        var leaks = new List<string>();
        var candidates = new[]
        {
            ("UserName", userName),
            ("MachineName", machineName),
            ("UserProfile", userProfile)
        };

        foreach (var (label, value) in candidates)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                value.Length >= 3 &&
                text.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                leaks.Add($"{label}({value})");
            }
        }

        return leaks;
    }

    private static void EnsureWpfThemeResourcesLoaded()
    {
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var resources = Application.Current!.Resources;
        const string themeUriString = "pack://application:,,,/GameCrashKit.App;component/Themes/CyberDarkTheme.xaml";
        var alreadyLoaded = resources.MergedDictionaries.Any(d =>
            d.Source is not null &&
            string.Equals(d.Source.OriginalString, themeUriString, StringComparison.OrdinalIgnoreCase));

        if (!alreadyLoaded)
        {
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(themeUriString, UriKind.Absolute)
            });
        }

        if (!resources.Contains("BoolToVis"))
        {
            resources["BoolToVis"] = new BooleanToVisibilityConverter();
        }
    }

    private static bool HasPngSignature(byte[] bytes)
    {
        ReadOnlySpan<byte> expected = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length >= expected.Length && bytes.AsSpan(0, expected.Length).SequenceEqual(expected);
    }

    private static bool SamplePixelIsLightCanvas(BitmapSource bitmap, int x, int y, out string rgbaText)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        // Bgra32 layout: [B, G, R, A] - accept white (#FFFFFF) or light canvas (#F6F8FA), rejecting dark theme (#0B0E14/#131822)
        rgbaText = string.Create(CultureInfo.InvariantCulture, $"({pixel[2]},{pixel[1]},{pixel[0]},{pixel[3]})");
        return pixel[2] >= 240 && pixel[1] >= 240 && pixel[0] >= 240 && pixel[3] == 255;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
