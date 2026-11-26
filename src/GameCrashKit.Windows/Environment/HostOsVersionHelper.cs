using System.Globalization;
using Microsoft.Win32;

namespace GameCrashKit.Windows.Environment;

public enum WindowsOsFamily
{
    Unknown = 0,
    Windows10,
    Windows11
}

public sealed record HostOsVersionInfo(
    WindowsOsFamily Family,
    int BuildNumber,
    int Revision,
    string? DisplayVersion,
    string DisplayLabel);

/// <summary>
/// Determines Windows OS family from <c>CurrentBuildNumber</c> rather than <c>ProductName</c>
/// (which remains "Windows 10 ..." on Windows 11).
/// </summary>
public static class HostOsVersionHelper
{
    private const string CurrentVersionSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public static WindowsOsFamily ClassifyWindowsVersion(int buildNumber)
    {
        if (buildNumber >= 22000)
        {
            return WindowsOsFamily.Windows11;
        }

        if (buildNumber >= 10240)
        {
            return WindowsOsFamily.Windows10;
        }

        return WindowsOsFamily.Unknown;
    }

    public static HostOsVersionInfo ReadCurrentHost()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(CurrentVersionSubKey, writable: false);

        var buildStr = key?.GetValue("CurrentBuildNumber")?.ToString();
        _ = int.TryParse(buildStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buildNumber);

        var ubrObj = key?.GetValue("UBR");
        var ubr = ubrObj is int i ? i : 0;

        var displayVersion = key?.GetValue("DisplayVersion")?.ToString();
        var family = ClassifyWindowsVersion(buildNumber);

        var familyLabel = family switch
        {
            WindowsOsFamily.Windows11 => "Windows 11",
            WindowsOsFamily.Windows10 => "Windows 10",
            _ => "Windows"
        };

        var label = string.IsNullOrWhiteSpace(displayVersion)
            ? $"{familyLabel} (Build {buildNumber}.{ubr})"
            : $"{familyLabel} {displayVersion} (Build {buildNumber}.{ubr})";

        return new HostOsVersionInfo(family, buildNumber, ubr, displayVersion, label);
    }
}
