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

    /// <summary>
    /// Builds a coarse, zero-PII install source category suitable for shared reports.
    /// Never exposes private folder names, drive letters, usernames, or LAN IP addresses.
    /// </summary>
    public static string BuildShareSafeInstallSource(string? fullPath, bool isLikelyGame)
    {
        var normalized = NormalizePath(fullPath);
        if (normalized is null)
        {
            return "未知位置";
        }

        if (normalized.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase))
        {
            return "Steam 游戏库";
        }

        if (normalized.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase))
        {
            return "Epic Games 目录";
        }

        if (normalized.Contains(@"\XboxGames\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            return "Xbox / Microsoft Store 目录";
        }

        if (normalized.Contains(@"\WeGame\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\WeGameApps\", StringComparison.OrdinalIgnoreCase))
        {
            return "WeGame 游戏目录";
        }

        if (normalized.Contains(@"\GOG Galaxy\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\EA Games\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\Origin Games\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\Ubisoft Game Launcher\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\Battle.net\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\Riot Games\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\miHoYo\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\HoYoPlay\", StringComparison.OrdinalIgnoreCase))
        {
            return "主流游戏启动器目录";
        }

        if (IsWindowsSystemDirectoryPath(normalized))
        {
            return "Windows 系统目录";
        }

        if (normalized.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase))
        {
            return "Program Files 安装目录";
        }

        if (isLikelyGame || normalized.Contains(@"\Games\", StringComparison.OrdinalIgnoreCase))
        {
            return "独立游戏目录（详细路径已脱敏）";
        }

        return "本地应用目录（详细路径已脱敏）";
    }

    public static bool IsInSameDirectoryOrSubtree(string? candidateFilePath, string? appExePath)
    {
        var normMod = NormalizePath(candidateFilePath);
        var normApp = NormalizePath(appExePath);
        if (normMod is null || normApp is null || !IsRootedWindowsPath(normMod) || !IsRootedWindowsPath(normApp))
        {
            return false;
        }

        var appDir = Path.GetDirectoryName(normApp);
        if (string.IsNullOrWhiteSpace(appDir))
        {
            return false;
        }

        var prefix = appDir.TrimEnd('\\') + @"\";
        return normMod.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
