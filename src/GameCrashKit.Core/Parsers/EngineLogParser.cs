using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Parsers;

/// <summary>
/// Pure, bounded-time parsers for Unity Player.log, Unreal CrashContext.runtime-xml,
/// and generic user-selected crash logs.
/// </summary>
public static class EngineLogParser
{
    public const int MaxScanLineLength = 2_048;
    public const int MaxExcerptLines = 5;
    public const int MaxExcerptLineLength = 160;
    public const int MaxExcerptTotalChars = 600;
    private const long MaxUnrealXmlCharacters = 2_097_152;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex UnityVersionRegex = new(
        @"Initialize engine version:\s*(?<ver>[^\r\n]{1,80})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex UnityRhiRegex = new(
        @"Version:\s*(?<rhi>(?:Direct3D\s*1[12]|Vulkan|OpenGL)[^\r\n]{0,60})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly string[] GpuLostTokens =
    [
        "DXGI_ERROR_DEVICE_REMOVED",
        "DXGI_ERROR_DEVICE_HUNG",
        "DXGI_ERROR_DEVICE_RESET",
        "0x887A0005",
        "0x887A0006",
        "0x887A0007",
        "VK_ERROR_DEVICE_LOST",
        "GPU Crash dump Triggered",
        "D3D device being lost"
    ];

    private static readonly string[] OomTokens =
    [
        "Out of video memory",
        "Ran out of memory",
        "Could not allocate memory",
        "E_OUTOFMEMORY",
        "VK_ERROR_OUT_OF_DEVICE_MEMORY"
    ];

    private static readonly string[] SignalLineKeywords =
    [
        "DXGI_ERROR_",
        "VK_ERROR_",
        "Out of video memory",
        "Ran out of memory",
        "Could not allocate memory",
        "GPU Crash dump Triggered",
        "D3D device being lost",
        "Crash!!!",
        "Fatal",
        "Exception",
        "Error"
    ];

    private static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxUnrealXmlCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = true
    };

    public static UserConfirmedEngineLog ParseUnityPlayerLog(
        string content,
        string displayLabel,
        string sourceLocation,
        DateTimeOffset? lastWriteUtc,
        bool isTruncated,
        bool mayBeOverwritten)
    {
        ArgumentNullException.ThrowIfNull(content);

        string? engineVersion = null;
        string? rhiBackend = null;

        var lines = SplitBoundedLines(content);
        foreach (var line in lines)
        {
            if (engineVersion is null)
            {
                var m = UnityVersionRegex.Match(line);
                if (m.Success)
                {
                    engineVersion = m.Groups["ver"].Value.Trim();
                }
            }

            if (rhiBackend is null)
            {
                var m = UnityRhiRegex.Match(line);
                if (m.Success)
                {
                    rhiBackend = m.Groups["rhi"].Value.Trim();
                }
            }

            if (engineVersion is not null && rhiBackend is not null)
            {
                break;
            }
        }

        var (hasGpuLost, gpuToken) = DetectFirstToken(lines, GpuLostTokens);
        var (hasOom, oomToken) = DetectFirstToken(lines, OomTokens);
        var excerpts = ExtractSignalExcerptLines(lines);

        return new UserConfirmedEngineLog(
            Kind: EngineLogKind.Unity,
            DisplayLabel: displayLabel,
            SourceLocation: sourceLocation,
            FileLastWriteUtc: lastWriteUtc,
            IsTruncated: isTruncated,
            MayBeOverwritten: mayBeOverwritten,
            EngineVersion: engineVersion,
            RhiBackend: rhiBackend,
            HasGpuDeviceLostSignal: hasGpuLost,
            GpuLostToken: gpuToken,
            HasOutOfMemorySignal: hasOom,
            OomToken: oomToken,
            RawErrorExcerptLines: excerpts);
    }

    public static UserConfirmedEngineLog ParseUnrealCrashContextXml(
        string xmlContent,
        string displayLabel,
        string sourceLocation,
        DateTimeOffset? lastWriteUtc,
        bool isTruncated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xmlContent);

        using var sr = new StringReader(xmlContent);
        using var reader = XmlReader.Create(sr, SafeXmlSettings);
        var doc = XDocument.Load(reader, LoadOptions.None);

        var root = doc.Root
            ?? throw new FormatException("Invalid Unreal CrashContext XML: missing root element.");

        var props = root.Elements().FirstOrDefault(e => e.Name.LocalName == "RuntimeProperties") ?? root;

        static string? GetElementValue(XElement parent, string localName)
        {
            var val = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }

        // Explicitly read ONLY safe metadata elements; NEVER copy <PCallStack> or <CallStack> into excerpts.
        var engineVersion = GetElementValue(props, "EngineVersion");
        var rhi = GetElementValue(props, "RHI");
        var crashType = GetElementValue(props, "CrashType");
        var errorMessage = GetElementValue(props, "ErrorMessage");

        var candidateLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(crashType))
        {
            candidateLines.Add($"CrashType: {BoundLine(crashType)}");
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            foreach (var l in SplitBoundedLines(errorMessage))
            {
                candidateLines.Add(l);
            }
        }

        var (hasGpuLost, gpuToken) = DetectFirstToken(candidateLines, GpuLostTokens);
        var (hasOom, oomToken) = DetectFirstToken(candidateLines, OomTokens);

        var excerptSource = !string.IsNullOrWhiteSpace(errorMessage)
            ? SplitBoundedLines(errorMessage)
            : candidateLines;
        var excerpts = BuildCappedExcerpts(excerptSource);

        return new UserConfirmedEngineLog(
            Kind: EngineLogKind.Unreal,
            DisplayLabel: displayLabel,
            SourceLocation: sourceLocation,
            FileLastWriteUtc: lastWriteUtc,
            IsTruncated: isTruncated,
            MayBeOverwritten: false,
            EngineVersion: engineVersion,
            RhiBackend: rhi,
            HasGpuDeviceLostSignal: hasGpuLost,
            GpuLostToken: gpuToken,
            HasOutOfMemorySignal: hasOom,
            OomToken: oomToken,
            RawErrorExcerptLines: excerpts);
    }

    public static UserConfirmedEngineLog ParseGenericManualLog(
        string content,
        string displayLabel,
        string sourceLocation,
        DateTimeOffset? lastWriteUtc,
        bool isTruncated,
        bool mayBeOverwritten = false)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Contains("<FGenericCrashContext", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUnrealCrashContextXml(content, displayLabel, sourceLocation, lastWriteUtc, isTruncated);
        }

        if (content.Contains("Initialize engine version:", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("GfxDevice:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseUnityPlayerLog(content, displayLabel, sourceLocation, lastWriteUtc, isTruncated, mayBeOverwritten);
        }

        var lines = SplitBoundedLines(content);
        var (hasGpuLost, gpuToken) = DetectFirstToken(lines, GpuLostTokens);
        var (hasOom, oomToken) = DetectFirstToken(lines, OomTokens);
        var excerpts = ExtractSignalExcerptLines(lines);

        return new UserConfirmedEngineLog(
            Kind: EngineLogKind.GenericManual,
            DisplayLabel: displayLabel,
            SourceLocation: sourceLocation,
            FileLastWriteUtc: lastWriteUtc,
            IsTruncated: isTruncated,
            MayBeOverwritten: mayBeOverwritten,
            EngineVersion: null,
            RhiBackend: null,
            HasGpuDeviceLostSignal: hasGpuLost,
            GpuLostToken: gpuToken,
            HasOutOfMemorySignal: hasOom,
            OomToken: oomToken,
            RawErrorExcerptLines: excerpts);
    }

    private static List<string> SplitBoundedLines(string content)
    {
        var result = new List<string>();
        using var sr = new StringReader(content);
        while (sr.ReadLine() is { } rawLine)
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            result.Add(BoundLine(trimmed));
        }

        return result;
    }

    private static string BoundLine(string line) =>
        line.Length <= MaxScanLineLength ? line : line[..MaxScanLineLength];

    private static (bool Found, string? Token) DetectFirstToken(IReadOnlyList<string> lines, string[] tokens)
    {
        foreach (var token in tokens)
        {
            foreach (var line in lines)
            {
                if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, token);
                }
            }
        }

        return (false, null);
    }

    private static List<string> ExtractSignalExcerptLines(IReadOnlyList<string> lines)
    {
        // Walk from tail to prioritize the latest crash-time lines, then restore chronological order.
        var selected = new List<string>();
        for (var i = lines.Count - 1; i >= 0 && selected.Count < MaxExcerptLines; i--)
        {
            var line = lines[i];
            if (SignalLineKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(line);
            }
        }

        if (selected.Count == 0 && lines.Count > 0)
        {
            var start = Math.Max(0, lines.Count - MaxExcerptLines);
            return BuildCappedExcerpts(lines.Skip(start).ToList());
        }

        selected.Reverse();
        return BuildCappedExcerpts(selected);
    }

    private static List<string> BuildCappedExcerpts(IReadOnlyList<string> lines)
    {
        var result = new List<string>(MaxExcerptLines);
        var totalChars = 0;

        foreach (var raw in lines)
        {
            if (result.Count >= MaxExcerptLines || totalChars >= MaxExcerptTotalChars)
            {
                break;
            }

            var line = raw.Length <= MaxExcerptLineLength
                ? raw
                : raw[..(MaxExcerptLineLength - 3)] + "...";

            var remaining = MaxExcerptTotalChars - totalChars;
            if (line.Length > remaining)
            {
                if (remaining <= 4)
                {
                    break;
                }

                line = line[..(remaining - 3)] + "...";
            }

            result.Add(line);
            totalChars += line.Length;
        }

        return result;
    }
}
