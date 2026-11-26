using System.Runtime.InteropServices;
using GameCrashKit.Core.Models;
using Microsoft.Win32;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Read-only probe for OS build label, physical RAM size, ANSI code page (<c>GetACP</c>),
/// Beta UTF-8 mode, and HVCI registry state.
/// </summary>
public static class SystemLocaleAndSecurityProbe
{
    private const string HvciSubKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

    public static SystemLocaleAndSecurityFact Probe()
    {
        var os = HostOsVersionHelper.ReadCurrentHost();
        var ramMb = ReadTotalPhysicalMemoryMb();
        var acp = (int)GetACP();
        var isBetaUtf8 = acp == 65001;
        var hvciEnabled = ReadHvciEnabled();

        return new SystemLocaleAndSecurityFact(
            OsDisplayLabel: os.DisplayLabel,
            BuildNumber: os.BuildNumber,
            TotalPhysicalMemoryMb: ramMb,
            AcpCodePage: acp,
            IsBetaUtf8Enabled: isBetaUtf8,
            HvciEnabled: hvciEnabled);
    }

    private static int ReadTotalPhysicalMemoryMb()
    {
        var mem = new MemoryStatusEx
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (GlobalMemoryStatusEx(ref mem))
        {
            return (int)(mem.ullTotalPhys / (1024UL * 1024UL));
        }

        return 0;
    }

    private static bool? ReadHvciEnabled()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(HvciSubKey, writable: false);
            var enabled = key?.GetValue("Enabled");
            return enabled is int i ? i == 1 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
