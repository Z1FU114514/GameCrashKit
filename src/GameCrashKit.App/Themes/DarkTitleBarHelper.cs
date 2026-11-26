using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameCrashKit.App.Themes;

/// <summary>
/// Enables native Windows dark title bar and matches caption color to #0B0E14 (极夜深空) when supported.
/// Safe no-op on unsupported builds or off-screen tests.
/// </summary>
public static class DarkTitleBarHelper
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    // COLORREF is 0x00BBGGRR -> #0B0E14 => R=0x0B, G=0x0E, B=0x14 => 0x00140E0B
    private const int AbyssBackgroundColorRef = 0x00140E0B;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) => ApplyToHandle(window);
    }

    private static void ApplyToHandle(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }

            var captionColor = AbyssBackgroundColorRef;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
        }
        catch (Exception)
        {
            // Ignore cleanly on unsupported OS versions.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
