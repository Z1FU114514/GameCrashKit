using System.Text.RegularExpressions;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Correlation;

/// <summary>
/// Pure path normalization, game-directory classification, system-noise filtering,
/// and sanitized path-suffix formatting helpers.
/// </summary>
public static partial class PathClassifier
{
    private static readonly string[] GamePathMarkers =
    [
        @"\steamapps\",
        @"\Epic Games\",
        @"\WeGame\",
        @"\WeGameApps\",
        @"\XboxGames\",
        @"\GOG Galaxy\",
        @"\miHoYo\",
        @"\HoYoPlay\",
        @"\Riot Games\",
        @"\Battle.net\",
        @"\EA Games\",
        @"\Origin Games\",
        @"\Ubisoft Game Launcher\",
        @"\Games\"
    ];

    private static readonly HashSet<string> SystemNoiseProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost.exe",
        "Widgets.exe",
        "WidgetService.exe",
        "explorer.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "RuntimeBroker.exe",
        "DllHost.exe",
        "backgroundTaskHost.exe",
        "TextInputHost.exe",
        "PhoneExperienceHost.exe",
        "ApplicationFrameHost.exe",
        "svchost.exe",
        "SystemSettings.exe",
        "ctfmon.exe"
    };

    [GeneratedRegex(@"^[A-Za-z]:\\", RegexOptions.CultureInvariant)]
    private static partial Regex RootedWindowsPathRegex();

    [GeneratedRegex(@"^(?<drive>[A-Za-z]:\\Users\\)[^\\]+(?<rest>\\.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePathRegex();

    public static string? NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var trimmed = rawPath.Trim().Trim('"').Replace('/', '\\');
        while (trimmed.Length > 3 && trimmed.EndsWith('\\'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsRootedWindowsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return RootedWindowsPathRegex().IsMatch(path.Trim());
    }

    public static bool IsLikelyGamePath(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        foreach (var marker in GamePathMarkers)
        {
            if (normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSystemNoiseProcess(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        var fileName = Path.GetFileName(appName.Trim());
        return SystemNoiseProcesses.Contains(fileName);
    }

    public static bool IsWindowsSystemDirectoryPath(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        return normalizedPath.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a privacy-safe trailing path suffix (up to 3 segments, scrubbing <c>Users\&lt;name&gt;</c>)
    /// suitable for candidate disambiguation and whitelisted sharing.
    /// </summary>
    public static string? BuildDisambiguatedPathSuffix(string? fullPath)
    {
        var normalized = NormalizePath(fullPath);
        if (normalized is null)
        {
            return null;
        }

        var scrubbed = UserProfilePathRegex().Replace(normalized, @"<USER_HOME>${rest}");
        var segments = scrubbed.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        if (segments.Length <= 3)
        {
            // Strip drive letter prefix like "D:" when <= 3 segments so root drive layout isn't exposed unnecessarily
            var startIdx = segments[0].EndsWith(':') ? 1 : 0;
            return startIdx < segments.Length
                ? string.Join('\\', segments[startIdx..])
                : segments[^1];
        }

        return @"...\" + string.Join('\\', segments[^3..]);
    }

    public static TargetPathFact EvaluateTargetPathFact(string? fullPath)
    {
        var normalized = NormalizePath(fullPath);
        if (normalized is null)
        {
            return new TargetPathFact(
                Checked: false,
                HasNonAscii: false,
                ExceedsMaxPath260: false,
                PathLength: null,
                DisambiguatedSuffix: null);
        }

        var hasNonAscii = normalized.Any(static c => c > 127);
        var exceeds260 = normalized.Length > 260;

        return new TargetPathFact(
            Checked: true,
            HasNonAscii: hasNonAscii,
            ExceedsMaxPath260: exceeds260,
            PathLength: normalized.Length,
            DisambiguatedSuffix: BuildDisambiguatedPathSuffix(normalized));
    }
}
