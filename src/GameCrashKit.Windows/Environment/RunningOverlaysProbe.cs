using System.Runtime.InteropServices;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Read-only global process name snapshot via <c>CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)</c>.
/// Anti-cheat safe: NEVER calls OpenProcess, ReadProcessMemory, EnumProcessModules, or injects/debugs any process.
/// </summary>
public static class RunningOverlaysProbe
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly IReadOnlyDictionary<string, string> KnownOverlayProcesses =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RTSS.exe"] = "RivaTuner Statistics Server (RTSS)",
            ["MSIAfterburner.exe"] = "MSI Afterburner",
            ["Discord.exe"] = "Discord",
            ["DiscordPTB.exe"] = "Discord PTB",
            ["NVIDIA Overlay.exe"] = "NVIDIA Overlay",
            ["NVIDIA Share.exe"] = "NVIDIA Share",
            ["GameBar.exe"] = "Xbox Game Bar",
            ["obs64.exe"] = "OBS Studio",
            ["Overwolf.exe"] = "Overwolf",
            ["Medal.exe"] = "Medal",
            ["CapFrameX.exe"] = "CapFrameX",
            ["Fraps.exe"] = "Fraps"
        };

    public static RunningOverlaysFact Probe()
    {
        var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return new RunningOverlaysFact(
                Checked: false,
                DetectedOverlayNames: [],
                Note: "无法获取全局进程名快照。");
        }

        try
        {
            var entry = new ProcessEntry32W
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32W>()
            };

            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    if (!string.IsNullOrWhiteSpace(entry.szExeFile) &&
                        KnownOverlayProcesses.TryGetValue(entry.szExeFile, out var friendlyName))
                    {
                        detected.Add(friendlyName);
                    }
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        var ordered = detected.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return new RunningOverlaysFact(
            Checked: true,
            DetectedOverlayNames: ordered,
            Note: "仅反映扫描瞬间正在运行的常见覆盖层/录屏进程，不代表其参与了历史崩溃。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
