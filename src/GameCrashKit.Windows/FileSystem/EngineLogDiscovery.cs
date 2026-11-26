using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml;
using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Parsers;

namespace GameCrashKit.Windows.FileSystem;

public sealed record DiscoveredEngineLogCandidate(
    EngineLogKind Kind,
    string DisplayName,
    string SanitizedLocationHint,
    string FullPath,
    DateTimeOffset LastWriteUtc,
    long FileSizeBytes,
    bool MayBeOverwritten);

public sealed record EngineLogLoadResult(
    bool Success,
    SafeFileReadErrorKind ErrorKind,
    string? ErrorMessage,
    UserConfirmedEngineLog? EngineLog,
    SafeFileReadResult? FileReadResult);

/// <summary>
/// Read-only, bounded-depth discovery for Unity (<c>Player.log</c>/<c>Player-prev.log</c>)
/// and Unreal (<c>CrashContext.runtime-xml</c>) crash logs, plus safe user-confirmed file loading.
/// Strictly rejects network/non-fixed directories and reparse-point ancestors before enumeration.
/// </summary>
public static class EngineLogDiscovery
{
    private const int DefaultMaxDirectoriesVisited = 400;
    private const int DefaultTimeBudgetMs = 1_500;

    public static IReadOnlyList<DiscoveredEngineLogCandidate> DiscoverInWindow(
        DateTimeOffset windowStartUtc,
        int maxCandidates = 20,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DiscoveredEngineLogCandidate>();
        var budget = new DiscoveryBudget(DefaultMaxDirectoriesVisited, DefaultTimeBudgetMs, cancellationToken);

        var userProfile = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.UserProfile,
            System.Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var localLow = Path.Combine(userProfile, "AppData", "LocalLow");
            DiscoverUnityLogs(localLow, windowStartUtc, results, budget);
        }

        if (!budget.IsExhausted)
        {
            var localAppData = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData,
                System.Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                DiscoverUnrealCrashes(localAppData, windowStartUtc, results, budget);
            }
        }

        return results
            .OrderByDescending(static c => c.LastWriteUtc)
            .ThenBy(static c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(maxCandidates)
            .ToList();
    }

    public static EngineLogLoadResult LoadAndParseUserSelectedFile(
        string filePath,
        EngineLogKind? kindHint = null,
        bool mayBeOverwritten = false)
    {
        var readResult = SafeLocalFileReader.ReadTailUtf8(filePath);
        if (!readResult.Success || readResult.Content is null)
        {
            return new EngineLogLoadResult(
                Success: false,
                ErrorKind: readResult.ErrorKind,
                ErrorMessage: readResult.ErrorMessage ?? "无法读取所选日志文件。",
                EngineLog: null,
                FileReadResult: readResult);
        }

        var fileName = Path.GetFileName(filePath);
        var isUnrealXmlFile = kindHint == EngineLogKind.Unreal ||
            fileName.EndsWith(".runtime-xml", StringComparison.OrdinalIgnoreCase);

        if (isUnrealXmlFile && readResult.IsTruncated)
        {
            return new EngineLogLoadResult(
                Success: false,
                ErrorKind: SafeFileReadErrorKind.ReadFailed,
                ErrorMessage: "CrashContext.runtime-xml 超出 2 MiB 安全读取上限，XML 上下文不完整，已中止解析（未回退到自由文本解析）。",
                EngineLog: null,
                FileReadResult: readResult);
        }

        var byteRange = string.Format(
            CultureInfo.InvariantCulture,
            "{0} (Bytes {1}..{2} / {3}{4})",
            fileName,
            readResult.ReadStartByteOffset,
            readResult.ReadEndByteOffset,
            readResult.FileSizeBytes,
            readResult.IsTruncated ? ", truncated" : string.Empty);

        try
        {
            var effectiveKind = isUnrealXmlFile ? EngineLogKind.Unreal : kindHint;
            var parsed = effectiveKind switch
            {
                EngineLogKind.Unity => EngineLogParser.ParseUnityPlayerLog(
                    readResult.Content,
                    fileName,
                    byteRange,
                    readResult.LastWriteTimeUtc,
                    readResult.IsTruncated,
                    mayBeOverwritten),
                EngineLogKind.Unreal => EngineLogParser.ParseUnrealCrashContextXml(
                    readResult.Content,
                    fileName,
                    byteRange,
                    readResult.LastWriteTimeUtc,
                    readResult.IsTruncated),
                _ => EngineLogParser.ParseGenericManualLog(
                    readResult.Content,
                    fileName,
                    byteRange,
                    readResult.LastWriteTimeUtc,
                    readResult.IsTruncated,
                    mayBeOverwritten)
            };

            return new EngineLogLoadResult(
                Success: true,
                ErrorKind: SafeFileReadErrorKind.None,
                ErrorMessage: null,
                EngineLog: parsed,
                FileReadResult: readResult);
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            return new EngineLogLoadResult(
                Success: false,
                ErrorKind: SafeFileReadErrorKind.ReadFailed,
                ErrorMessage: $"日志格式解析失败：{ex.Message}",
                EngineLog: null,
                FileReadResult: readResult);
        }
    }

    private static void DiscoverUnityLogs(
        string localLowDir,
        DateTimeOffset windowStartUtc,
        List<DiscoveredEngineLogCandidate> results,
        DiscoveryBudget budget)
    {
        if (!IsSafeExistingDirectory(localLowDir, budget))
        {
            return;
        }

        foreach (var companyDir in SafeEnumerateDirectories(localLowDir, budget))
        {
            if (budget.IsExhausted) break;
            foreach (var productDir in SafeEnumerateDirectories(companyDir, budget))
            {
                if (budget.IsExhausted) break;
                CheckUnityFile(productDir, "Player.log", mayBeOverwritten: true, windowStartUtc, results);
                CheckUnityFile(productDir, "Player-prev.log", mayBeOverwritten: false, windowStartUtc, results);
            }
        }
    }

    private static void CheckUnityFile(
        string productDir,
        string fileName,
        bool mayBeOverwritten,
        DateTimeOffset windowStartUtc,
        List<DiscoveredEngineLogCandidate> results)
    {
        var fullPath = Path.Combine(productDir, fileName);
        if (!TryGetCandidateInfo(fullPath, windowStartUtc, out var info))
        {
            return;
        }

        var productName = Path.GetFileName(productDir);
        var suffix = PathClassifier.BuildDisambiguatedPathSuffix(fullPath) ?? fileName;
        results.Add(new DiscoveredEngineLogCandidate(
            Kind: EngineLogKind.Unity,
            DisplayName: $"Unity · {productName} ({fileName})",
            SanitizedLocationHint: suffix,
            FullPath: fullPath,
            LastWriteUtc: new DateTimeOffset(info!.LastWriteTimeUtc, TimeSpan.Zero),
            FileSizeBytes: info.Length,
            MayBeOverwritten: mayBeOverwritten));
    }

    private static void DiscoverUnrealCrashes(
        string localAppDataDir,
        DateTimeOffset windowStartUtc,
        List<DiscoveredEngineLogCandidate> results,
        DiscoveryBudget budget)
    {
        if (!IsSafeExistingDirectory(localAppDataDir, budget))
        {
            return;
        }

        foreach (var appDir in SafeEnumerateDirectories(localAppDataDir, budget))
        {
            if (budget.IsExhausted) break;

            var savedDir = Path.Combine(appDir, "Saved");
            if (!IsSafeExistingChildDirectory(savedDir, budget))
            {
                continue;
            }

            var crashesDir = Path.Combine(savedDir, "Crashes");
            if (!IsSafeExistingChildDirectory(crashesDir, budget))
            {
                continue;
            }

            foreach (var crashSubDir in SafeEnumerateDirectories(crashesDir, budget))
            {
                if (budget.IsExhausted) break;

                var xmlPath = Path.Combine(crashSubDir, "CrashContext.runtime-xml");
                if (!TryGetCandidateInfo(xmlPath, windowStartUtc, out var info))
                {
                    continue;
                }

                var gameName = Path.GetFileName(appDir);
                var suffix = PathClassifier.BuildDisambiguatedPathSuffix(xmlPath) ?? "CrashContext.runtime-xml";
                results.Add(new DiscoveredEngineLogCandidate(
                    Kind: EngineLogKind.Unreal,
                    DisplayName: $"Unreal · {gameName}",
                    SanitizedLocationHint: suffix,
                    FullPath: xmlPath,
                    LastWriteUtc: new DateTimeOffset(info!.LastWriteTimeUtc, TimeSpan.Zero),
                    FileSizeBytes: info.Length,
                    MayBeOverwritten: false));
            }
        }
    }

    private static bool IsSafeExistingDirectory(string dirPath, DiscoveryBudget budget)
    {
        if (budget.IsExhausted || !budget.TryConsumeDirectory())
        {
            return false;
        }

        if (!SafeLocalFileReader.TryValidateLocalDirectoryWithoutNetworkAccess(dirPath, out var normalized, out _, out _))
        {
            return false;
        }

        return normalized is not null && Directory.Exists(normalized);
    }

    private static bool IsSafeExistingChildDirectory(string dirPath, DiscoveryBudget budget)
    {
        if (budget.IsExhausted || !budget.TryConsumeDirectory())
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(dirPath))
            {
                return false;
            }

            var attrs = File.GetAttributes(dirPath);
            return (attrs & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string parentDir, DiscoveryBudget budget)
    {
        if (budget.IsExhausted)
        {
            return [];
        }

        try
        {
            var list = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(parentDir))
            {
                if (budget.IsExhausted)
                {
                    break;
                }

                if (IsSafeExistingChildDirectory(dir, budget))
                {
                    list.Add(dir);
                }
            }

            return list;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private sealed class DiscoveryBudget
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly int _maxDirectories;
        private readonly int _timeBudgetMs;
        private readonly CancellationToken _ct;
        private int _visitedDirectories;

        public DiscoveryBudget(int maxDirectories, int timeBudgetMs, CancellationToken ct)
        {
            _maxDirectories = maxDirectories;
            _timeBudgetMs = timeBudgetMs;
            _ct = ct;
        }

        public bool IsExhausted =>
            _ct.IsCancellationRequested ||
            _visitedDirectories >= _maxDirectories ||
            _sw.ElapsedMilliseconds >= _timeBudgetMs;

        public bool TryConsumeDirectory()
        {
            if (IsExhausted)
            {
                return false;
            }

            _visitedDirectories++;
            return true;
        }
    }

    private static bool TryGetCandidateInfo(string fullPath, DateTimeOffset windowStartUtc, out FileInfo? fileInfo)
    {
        fileInfo = null;
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            var lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWriteUtc < windowStartUtc)
            {
                return false;
            }

            fileInfo = info;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
