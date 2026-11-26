using System.Xml;
using GameCrashKit.Core.Parsers;
using Xunit;

namespace GameCrashKit.Core.Tests;

public sealed class EventXmlParserAndNormalizerTests
{
    private static string LoadFixture(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);
        Assert.True(File.Exists(fullPath), $"Fixture not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    [Fact]
    public void ParseAndNormalize_Win11App1000Named_ExtractsAllFieldsAndCanonicalHex()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "win11-25h2-app-1000-named.xml"));
        var parsed = EventXmlParser.Parse(xml);
        var normalized = WindowsEventNormalizer.NormalizeAppCrash1000(parsed);

        Assert.Equal("Application", parsed.Channel);
        Assert.Equal("Application Error", parsed.ProviderName);
        Assert.Equal(1000, parsed.EventId);
        Assert.Equal(0, parsed.Version);
        Assert.Equal(10421, parsed.RecordId);
        Assert.False(parsed.HasUnnamedData);

        Assert.True(normalized.IsSupportedVersion);
        Assert.False(normalized.UsedPositionalFallback);
        Assert.Equal("SampleGame.exe", normalized.AppName);
        Assert.Equal("SampleGame.exe", normalized.ModuleName);
        Assert.Equal("0xc0000005", normalized.ExceptionCode);
        Assert.Equal("0x2a10", normalized.FaultingProcessIdHex);
        Assert.Equal("11111111-2222-3333-4444-555555555555", normalized.IntegratorReportId);
        Assert.Empty(normalized.Warnings);
    }

    [Fact]
    public void ParseAndNormalize_Win10App1000Positional_UsesVerifiedPositionalFallback()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "win10-22h2-app-1000-positional.xml"));
        var parsed = EventXmlParser.Parse(xml);
        var normalized = WindowsEventNormalizer.NormalizeAppCrash1000(parsed);

        Assert.True(parsed.HasUnnamedData);
        Assert.True(normalized.IsSupportedVersion);
        Assert.True(normalized.UsedPositionalFallback);
        Assert.Equal("LegacyGame.exe", normalized.AppName);
        Assert.Equal("d3d11.dll", normalized.ModuleName);
        Assert.Equal("0xc0000005", normalized.ExceptionCode);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", normalized.IntegratorReportId);
    }

    [Fact]
    public void NormalizeAppCrash1000_UnknownVersionWithUnnamedData_DoesNotGuessFields()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Application Error" />
                <EventID>1000</EventID>
                <Version>99</Version>
                <Channel>Application</Channel>
              </System>
              <EventData>
                <Data>SomeGame.exe</Data>
                <Data>1.0</Data>
                <Data>0</Data>
                <Data>mod.dll</Data>
                <Data>1.0</Data>
                <Data>0</Data>
                <Data>c0000005</Data>
                <Data>0</Data>
                <Data>0x10</Data>
                <Data>0x1</Data>
                <Data>C:\SomeGame.exe</Data>
                <Data>C:\mod.dll</Data>
                <Data>guid</Data>
              </EventData>
            </Event>
            """;

        var parsed = EventXmlParser.Parse(xml);
        var normalized = WindowsEventNormalizer.NormalizeAppCrash1000(parsed);

        Assert.False(normalized.IsSupportedVersion);
        Assert.False(normalized.UsedPositionalFallback);
        Assert.Null(normalized.AppName);
        Assert.Null(normalized.ExceptionCode);
        Assert.NotEmpty(normalized.Warnings);
    }

    [Fact]
    public void ParseAndNormalize_Win11App1002Hang_ExtractsHangMetadata()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "win11-25h2-app-1002-hang.xml"));
        var parsed = EventXmlParser.Parse(xml);
        var normalized = WindowsEventNormalizer.NormalizeAppHang1002(parsed);

        Assert.Equal(1002, parsed.EventId);
        Assert.True(normalized.IsSupportedVersion);
        Assert.False(normalized.UsedPositionalFallback);
        Assert.Equal("SampleGame.exe", normalized.AppName);
        Assert.Equal(@"D:\Games\SteamLibrary\steamapps\common\SampleGame\SampleGame.exe", normalized.ExeFileName);
        Assert.Equal("22222222-3333-4444-5555-666666666666", normalized.ReportId);
        Assert.Equal("Unknown", normalized.HangType);
    }

    [Fact]
    public void ParseAndNormalize_Win11App1001AppCrash_IsSupportedAndMatchesApp1000ReportId()
    {
        var xml1000 = LoadFixture(Path.Combine("EventXml", "win11-25h2-app-1000-named.xml"));
        var xml1001 = LoadFixture(Path.Combine("EventXml", "win11-25h2-app-1001-wer-appcrash.xml"));

        var crash = WindowsEventNormalizer.NormalizeAppCrash1000(EventXmlParser.Parse(xml1000));
        var wer = WindowsEventNormalizer.NormalizeWerReport1001(EventXmlParser.Parse(xml1001));

        Assert.True(wer.IsSupportedVersion);
        Assert.True(wer.IsSupportedReportType);
        Assert.Equal("APPCRASH", wer.EventName);
        Assert.Equal("SampleGame.exe", wer.P1);
        Assert.Equal(crash.IntegratorReportId, wer.ReportId);
    }

    [Fact]
    public void ParseAndNormalize_Win11App1001LiveKernelEvent_IsRejectedAsUnsupportedNoise()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "win11-25h2-app-1001-wer-noise-livekernelevent.xml"));
        var wer = WindowsEventNormalizer.NormalizeWerReport1001(EventXmlParser.Parse(xml));

        Assert.True(wer.IsSupportedVersion);
        Assert.False(wer.IsSupportedReportType);
        Assert.Equal("LiveKernelEvent", wer.EventName);
    }

    [Fact]
    public void ParseAndNormalize_Win11KernelPower41V10_MarkedAsSystemBackgroundOnly()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "win11-25h2-sys-kp41-v10.xml"));
        var parsed = EventXmlParser.Parse(xml);
        var kp41 = WindowsEventNormalizer.NormalizeKernelPower41(parsed);

        Assert.Equal(41, parsed.EventId);
        Assert.Equal(10, parsed.Version);
        Assert.True(kp41.IsSystemBackgroundOnly);
        Assert.True(kp41.IsSupportedVersion);
        Assert.Equal(0, kp41.BugcheckCode);
    }

    [Fact]
    public void Parse_Nvlddmkm153WithQualifiersAndUnnamedData_Succeeds()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "win11-25h2-sys-nvlddmkm-153.xml"));
        var parsed = EventXmlParser.Parse(xml);

        Assert.Equal("nvlddmkm", parsed.ProviderName);
        Assert.Equal(153, parsed.EventId);
        Assert.True(parsed.HasUnnamedData);
        Assert.Equal(2, parsed.PositionalFields.Count);
        Assert.Equal(@"\Device\Video3", parsed.PositionalFields[0]);
    }

    [Fact]
    public void Parse_MaliciousDtdXml_ThrowsXmlException()
    {
        var xml = LoadFixture(Path.Combine("EventXml", "security-malicious-dtd.xml"));
        Assert.Throws<XmlException>(() => EventXmlParser.Parse(xml));
    }
}
