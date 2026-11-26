using System.Xml;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Parsers;
using Xunit;

namespace GameCrashKit.Core.Tests;

public sealed class EngineLogParserTests
{
    private static string LoadFixture(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);
        Assert.True(File.Exists(fullPath), $"Fixture not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    [Fact]
    public void ParseUnityPlayerLog_GpuLostFixture_ExtractsVersionRhiAndToken()
    {
        var log = LoadFixture(Path.Combine("EngineLogs", "unity-player-gpu-lost.log"));
        var parsed = EngineLogParser.ParseUnityPlayerLog(
            log,
            "Player.log",
            "Player.log (Bytes 0..380 / 380)",
            DateTimeOffset.UtcNow,
            isTruncated: false,
            mayBeOverwritten: true);

        Assert.Equal(EngineLogKind.Unity, parsed.Kind);
        Assert.Equal("2022.3.20f1c1 (1234567890ab)", parsed.EngineVersion);
        Assert.Equal("Direct3D 11.0 [level 11.1]", parsed.RhiBackend);
        Assert.True(parsed.HasGpuDeviceLostSignal);
        Assert.Equal("DXGI_ERROR_DEVICE_REMOVED", parsed.GpuLostToken);
        Assert.False(parsed.HasOutOfMemorySignal);
        Assert.True(parsed.MayBeOverwritten);
        Assert.InRange(parsed.RawErrorExcerptLines.Count, 1, EngineLogParser.MaxExcerptLines);
    }

    [Fact]
    public void ParseUnrealCrashContextXml_OomFixture_ExtractsMetadataAndExcludesCallstack()
    {
        var xml = LoadFixture(Path.Combine("EngineLogs", "unreal-crashcontext-oom.xml"));
        var parsed = EngineLogParser.ParseUnrealCrashContextXml(
            xml,
            "CrashContext.runtime-xml",
            "CrashContext.runtime-xml (Bytes 0..400 / 400)",
            DateTimeOffset.UtcNow,
            isTruncated: false);

        Assert.Equal(EngineLogKind.Unreal, parsed.Kind);
        Assert.Equal("5.4.4-0+++UE5+Release-5.4", parsed.EngineVersion);
        Assert.Equal("D3D12", parsed.RhiBackend);
        Assert.False(parsed.HasGpuDeviceLostSignal);
        Assert.True(parsed.HasOutOfMemorySignal);
        Assert.Equal("Out of video memory", parsed.OomToken);
        Assert.Single(parsed.RawErrorExcerptLines);

        // Verify callstack from an Unreal XML with <PCallStack> NEVER leaks into excerpts
        const string xmlWithCallstack = """
            <?xml version="1.0" encoding="UTF-8"?>
            <FGenericCrashContext>
              <RuntimeProperties>
                <CrashVersion>3</CrashVersion>
                <CrashType>Crash</CrashType>
                <ErrorMessage>Fatal error: DXGI_ERROR_DEVICE_HUNG</ErrorMessage>
                <PCallStack>MySecretFunction 0x12345678</PCallStack>
                <RHI>D3D12</RHI>
              </RuntimeProperties>
            </FGenericCrashContext>
            """;

        var withStack = EngineLogParser.ParseUnrealCrashContextXml(xmlWithCallstack, "CrashContext.runtime-xml", "loc", null, false);
        Assert.True(withStack.HasGpuDeviceLostSignal);
        Assert.All(withStack.RawErrorExcerptLines, static line => Assert.DoesNotContain("MySecretFunction", line));
    }

    [Fact]
    public void ParseUnrealCrashContextXml_MaliciousDtd_ThrowsXmlException()
    {
        const string dtdXml = """
            <?xml version="1.0"?>
            <!DOCTYPE FGenericCrashContext [ <!ENTITY xxe "bad"> ]>
            <FGenericCrashContext><RuntimeProperties><ErrorMessage>&xxe;</ErrorMessage></RuntimeProperties></FGenericCrashContext>
            """;

        Assert.Throws<XmlException>(() => EngineLogParser.ParseUnrealCrashContextXml(dtdXml, "bad.xml", "bad.xml", null, false));
    }

    [Fact]
    public void ParseGenericManualLog_NormalCleanLog_FindsZeroCrashSignals()
    {
        const string cleanLog = """
            Initialize engine version: 2022.3.20f1c1
            GfxDevice: creating device client
            Direct3D:
                Version: Direct3D 11.0
            Game initialized cleanly.
            Exiting normally by user request.
            """;

        var parsed = EngineLogParser.ParseGenericManualLog(cleanLog, "Player.log", "Player.log", null, false);
        Assert.Equal(EngineLogKind.Unity, parsed.Kind);
        Assert.False(parsed.HasGpuDeviceLostSignal);
        Assert.False(parsed.HasOutOfMemorySignal);
    }

    [Fact]
    public void ParseGenericManualLog_OverlongLines_StaysBoundedWithinExcerptLimits()
    {
        var hugeLine = "Error: " + new string('A', 12_000);
        var content = string.Join("\n", Enumerable.Repeat(hugeLine, 12));

        var parsed = EngineLogParser.ParseGenericManualLog(content, "huge.log", "huge.log", null, true);

        Assert.True(parsed.RawErrorExcerptLines.Count <= EngineLogParser.MaxExcerptLines);
        Assert.True(parsed.RawErrorExcerptLines.Sum(static l => l.Length) <= EngineLogParser.MaxExcerptTotalChars);
        Assert.All(parsed.RawErrorExcerptLines, static l => Assert.True(l.Length <= EngineLogParser.MaxExcerptLineLength));
    }
}
