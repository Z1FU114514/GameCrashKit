using GameCrashKit.Core.Parsers;
using GameCrashKit.Windows.Environment;
using GameCrashKit.Windows.EventLog;
using Xunit;

namespace GameCrashKit.Windows.Tests;

public sealed class HostReadOnlyProbeTests
{
    [Theory]
    [InlineData(19045, WindowsOsFamily.Windows10)]
    [InlineData(22000, WindowsOsFamily.Windows11)]
    [InlineData(26200, WindowsOsFamily.Windows11)]
    [InlineData(9000, WindowsOsFamily.Unknown)]
    public void ClassifyWindowsVersion_MapsBuildNumbersCorrectly(int build, WindowsOsFamily expected)
    {
        Assert.Equal(expected, HostOsVersionHelper.ClassifyWindowsVersion(build));
    }

    [Fact]
    public void ReadCurrentHost_ReturnsValidBuildWithoutThrowing()
    {
        var info = HostOsVersionHelper.ReadCurrentHost();
        Assert.True(info.BuildNumber >= 10240);
        Assert.False(string.IsNullOrWhiteSpace(info.DisplayLabel));
    }

    [Fact]
    public void VcRuntimeProbe_QueriesBothViewsWithoutThrowing()
    {
        var result = VcRuntimeProbe.Probe();
        Assert.Equal("x64", result.X64.Architecture);
        Assert.Equal("x86", result.X86.Architecture);
    }

    [Fact]
    public async Task QueryChannelAsync_ApplicationAndSystem_ReadOnlyParsesCleanly()
    {
        var since = DateTime.UtcNow.AddDays(-7);

        var appResult = await WindowsEventLogReader.QueryChannelAsync(
            "Application",
            WindowsEventLogReader.BuildApplicationCrashXPath(since),
            maxEvents: 20);

        Assert.Equal(EventChannelErrorKind.None, appResult.ErrorKind);
        foreach (var rawXml in appResult.RawXmlRecords)
        {
            var parsed = EventXmlParser.Parse(rawXml);
            Assert.True(parsed.EventId is 1000 or 1001 or 1002);
        }

        var sysResult = await WindowsEventLogReader.QueryChannelAsync(
            "System",
            WindowsEventLogReader.BuildSystemBackgroundXPath(since),
            maxEvents: 20);

        Assert.Equal(EventChannelErrorKind.None, sysResult.ErrorKind);
        foreach (var rawXml in sysResult.RawXmlRecords)
        {
            var parsed = EventXmlParser.Parse(rawXml);
            Assert.Equal(41, parsed.EventId);
        }
    }

    [Fact]
    public async Task QueryChannelAsync_NonExistentChannel_DegradesGracefully()
    {
        var result = await WindowsEventLogReader.QueryChannelAsync(
            "GameCrashKit-NonExistent-Channel-Test",
            "*[System[EventID=1]]",
            maxEvents: 5);

        Assert.NotEqual(EventChannelErrorKind.None, result.ErrorKind);
        Assert.Empty(result.RawXmlRecords);
    }
}
