using Microsoft.Win32;

namespace GameCrashKit.Windows.Environment;

public sealed record VcRuntimeEntry(
    string Architecture,
    bool IsInstalled,
    string? Version,
    string RegistryViewUsed);

public sealed record VcRuntimeProbeResult(
    VcRuntimeEntry X64,
    VcRuntimeEntry X86);

/// <summary>
/// Read-only Visual C++ 2015-2022 Redistributable probe.
/// Queries both 64-bit and 32-bit (<c>WOW6432Node</c>) registry views so x86 runtimes on 64-bit Windows are detected accurately.
/// </summary>
public static class VcRuntimeProbe
{
    private const string RuntimeBaseKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes";

    public static VcRuntimeProbeResult Probe()
    {
        var x64 = ProbeArchitecture("x64", RegistryView.Registry64, RegistryView.Registry32);
        var x86 = ProbeArchitecture("x86", RegistryView.Registry32, RegistryView.Registry64);
        return new VcRuntimeProbeResult(x64, x86);
    }

    private static VcRuntimeEntry ProbeArchitecture(string arch, RegistryView primaryView, RegistryView fallbackView)
    {
        if (TryReadRuntime(arch, primaryView, out var version))
        {
            return new VcRuntimeEntry(arch, true, version, primaryView.ToString());
        }

        if (TryReadRuntime(arch, fallbackView, out version))
        {
            return new VcRuntimeEntry(arch, true, version, fallbackView.ToString());
        }

        return new VcRuntimeEntry(arch, false, null, primaryView.ToString());
    }

    private static bool TryReadRuntime(string arch, RegistryView view, out string? version)
    {
        version = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey($@"{RuntimeBaseKey}\{arch}", writable: false);
            if (key is null)
            {
                return false;
            }

            var installedObj = key.GetValue("Installed");
            var installed = installedObj is int i && i == 1;
            if (!installed)
            {
                return false;
            }

            version = key.GetValue("Version")?.ToString();
            return true;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
