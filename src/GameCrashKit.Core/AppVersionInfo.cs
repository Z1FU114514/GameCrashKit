using System.Reflection;

namespace GameCrashKit.Core;

/// <summary>
/// Single-source semantic version helper resolved from build-time assembly metadata.
/// </summary>
public static class AppVersionInfo
{
    public const string FallbackVersion = "0.2.0-alpha.1";

    public static string Current { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var infoAttr = typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoAttr))
        {
            var plusIdx = infoAttr.IndexOf('+');
            var clean = (plusIdx > 0 ? infoAttr[..plusIdx] : infoAttr).Trim();
            if (!string.IsNullOrWhiteSpace(clean))
            {
                return clean;
            }
        }

        var ver = typeof(AppVersionInfo).Assembly.GetName().Version;
        if (ver is not null)
        {
            return $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }

        return FallbackVersion;
    }
}
