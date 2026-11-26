using System.Globalization;
using System.Management;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Read-only WMI probe for GPU adapters and driver release dates.
/// Explicitly reports WMI <c>DriverDate</c> as driver release date, never local install date.
/// </summary>
public static class GpuAdapterProbe
{
    private static readonly string[] VirtualAdapterKeywords =
    [
        "Virtual",
        "Remote",
        "ToDesk",
        "OrayIdd",
        "Parsec",
        "AnyDesk",
        "TeamViewer",
        "Microsoft Basic Display",
        "Mirage"
    ];

    public static IReadOnlyList<GpuAdapterFact> Probe()
    {
        var list = new List<GpuAdapterFact>();

        using var searcher = new ManagementObjectSearcher(
            "root\\CIMV2",
            "SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController");

        using var collection = searcher.Get();
        foreach (ManagementBaseObject obj in collection)
        {
            using (obj)
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var version = obj["DriverVersion"]?.ToString()?.Trim();
                var rawDate = obj["DriverDate"]?.ToString();
                var releaseDateIso = TryParseDmtfDateIso(rawDate);
                var isVirtual = VirtualAdapterKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

                list.Add(new GpuAdapterFact(name, version, releaseDateIso, isVirtual));
            }
        }

        return list;
    }

    internal static string? TryParseDmtfDateIso(string? dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf) || dmtf.Length < 8)
        {
            return null;
        }

        var datePart = dmtf[..8];
        if (DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return null;
    }
}
