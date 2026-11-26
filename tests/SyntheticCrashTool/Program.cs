using System.Runtime.CompilerServices;

namespace SyntheticCrashTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        string? mode = null;
        bool confirmed = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                mode = args[++i];
            }
            else if (string.Equals(args[i], "--confirm-crash", StringComparison.OrdinalIgnoreCase))
            {
                confirmed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(mode))
        {
            Console.Error.WriteLine("Missing --mode <av|stackoverflow|unhandled>. Use --help for usage.");
            return 2;
        }

        if (!confirmed)
        {
            Console.Error.WriteLine("SAFETY GUARD: --confirm-crash is required. Refusing to trigger a real process crash without explicit confirmation.");
            return 2;
        }

        return mode.ToLowerInvariant() switch
        {
            "av" => TriggerAccessViolation(),
            "stackoverflow" => TriggerStackOverflow(0),
            "unhandled" => TriggerUnhandledException(),
            _ => UnknownMode(mode)
        };
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SyntheticCrashTool - Isolated VM Crash Fixture Generator (DO NOT distribute in release zip)");
        Console.WriteLine("Usage:");
        Console.WriteLine("  SyntheticCrashTool --mode <av|stackoverflow|unhandled> --confirm-crash");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --help, -h         Show this help and exit 0");
        Console.WriteLine("  --mode <mode>      Crash mode: av (0xc0000005), stackoverflow (0xc00000fd), unhandled");
        Console.WriteLine("  --confirm-crash    Required safety flag to actually trigger the crash");
    }

    private static unsafe int TriggerAccessViolation()
    {
        int* nullPtr = (int*)0;
        *nullPtr = 0x1234;
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int TriggerStackOverflow(int depth)
    {
        return TriggerStackOverflow(depth + 1) + 1;
    }

    private static int TriggerUnhandledException()
    {
        throw new InvalidOperationException("SyntheticCrashTool intentional unhandled exception for WER/EventLog verification.");
    }

    private static int UnknownMode(string mode)
    {
        Console.Error.WriteLine($"Unknown mode '{mode}'. Supported: av, stackoverflow, unhandled.");
        return 2;
    }
}
