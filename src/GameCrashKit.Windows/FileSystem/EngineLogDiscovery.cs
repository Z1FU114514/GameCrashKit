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
/// </summary>
public static class EngineLogDiscovery
{
    public static IReadOnlyList<DiscoveredEngineLogCandidate> DiscoverInWindow(
        DateTimeOffset windowStartUtc,
        int maxCandidates = 20)
    {
        var results = new List<DiscoveredEngineLogCandidate>();

        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var localLow = Path.Combine(userProfile, "AppData", "LocalLow");
            DiscoverUnityLogs(localLow, windowStartUtc, results);
        }

        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            DiscoverUnrealCrashes(localAppData, windowStartUtc, results);
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
            var parsed = kindHint switch
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
        List<DiscoveredEngineLogCandidate> results)
    {
        if (!IsSafeExistingDirectory(localLowDir))
        {
            return;
        }

        foreach (var companyDir in SafeEnumerateDirectories(localLowDir))
        {
            foreach (var productDir in SafeEnumerateDirectories(companyDir))
            {
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
        List<DiscoveredEngineLogCandidate> results)
    {
        if (!IsSafeExistingDirectory(localAppDataDir))
        {
            return;
        }

        foreach (var appDir in SafeEnumerateDirectories(localAppDataDir))
        {
            var crashesDir = Path.Combine(appDir, "Saved", "Crashes");
            if (!IsSafeExistingDirectory(crashesDir))
            {
                continue;
            }

            foreach (var crashSubDir in SafeEnumerateDirectories(crashesDir))
            {
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

    private static bool IsSafeExistingDirectory(string dirPath)
    {
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

    private static IEnumerable<string> SafeEnumerateDirectories(string parentDir)
    {
        try
        {
            return Directory.EnumerateDirectories(parentDir)
                .Where(IsSafeExistingDirectory)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
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
