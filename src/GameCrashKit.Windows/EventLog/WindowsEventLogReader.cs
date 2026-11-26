using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace GameCrashKit.Windows.EventLog;

public enum EventChannelErrorKind
{
    None = 0,
    AccessDenied,
    ChannelNotFound,
    Cancelled,
    ReadFailed
}

public sealed record EventChannelReadResult(
    string ChannelName,
    IReadOnlyList<string> RawXmlRecords,
    int TotalRead,
    bool Truncated,
    EventChannelErrorKind ErrorKind,
    string? ErrorMessage);

/// <summary>
/// Read-only Windows Event Log reader using XPath time-window filtering and <see cref="EventRecord.ToXml"/>.
/// </summary>
public static class WindowsEventLogReader
{
    public const int DefaultMaxEventsPerChannel = 1_000;

    /// <summary>
    /// Builds an XPath query for Application Error (1000), WER (1001), and Application Hang (1002).
    /// </summary>
    public static string BuildApplicationCrashXPath(DateTime utcSince)
    {
        var isoUtc = utcSince.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $"*[System[(Provider[@Name='Application Error' or @Name='Application Hang' or @Name='Windows Error Reporting']) and (EventID=1000 or EventID=1001 or EventID=1002) and TimeCreated[@SystemTime>='{isoUtc}']]]";
    }

    /// <summary>
    /// Builds an XPath query for System Kernel-Power (41) background events.
    /// </summary>
    public static string BuildSystemBackgroundXPath(DateTime utcSince)
    {
        var isoUtc = utcSince.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[@SystemTime>='{isoUtc}']]]";
    }

    /// <summary>
    /// Queries a live Windows Event Log channel in reverse chronological order with read-only graceful degradation.
    /// </summary>
    public static Task<EventChannelReadResult> QueryChannelAsync(
        string channelName,
        string xpath,
        int maxEvents = DefaultMaxEventsPerChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(xpath);
        if (maxEvents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be positive.");
        }

        return Task.Run(() => ReadInternal(channelName, PathType.LogName, xpath, maxEvents, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Test-only helper for reading local <c>.evtx</c> files with the same reader pipeline.
    /// Not exposed in the production WPF UI.
    /// </summary>
    internal static EventChannelReadResult ReadEvtxFile(
        string evtxPath,
        string xpath,
        int maxEvents = DefaultMaxEventsPerChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evtxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(xpath);
        return ReadInternal(evtxPath, PathType.FilePath, xpath, maxEvents, cancellationToken);
    }

    private static EventChannelReadResult ReadInternal(
        string path,
        PathType pathType,
        string xpath,
        int maxEvents,
        CancellationToken cancellationToken)
    {
        var records = new List<string>();
        var truncated = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = new EventLogQuery(path, pathType, xpath)
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new EventChannelReadResult(
                        path,
                        records,
                        records.Count,
                        truncated,
                        EventChannelErrorKind.Cancelled,
                        "Scan cancelled.");
                }

                using var ev = reader.ReadEvent();
                if (ev is null)
                {
                    break;
                }

                if (records.Count >= maxEvents)
                {
                    truncated = true;
                    break;
                }

                records.Add(ev.ToXml());
            }

            return new EventChannelReadResult(
                path,
                records,
                records.Count,
                truncated,
                EventChannelErrorKind.None,
                null);
        }
        catch (OperationCanceledException)
        {
            return new EventChannelReadResult(
                path,
                records,
                records.Count,
                truncated,
                EventChannelErrorKind.Cancelled,
                "Scan cancelled.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new EventChannelReadResult(
                path,
                records,
                records.Count,
                truncated,
                EventChannelErrorKind.AccessDenied,
                ex.Message);
        }
        catch (EventLogNotFoundException ex)
        {
            return new EventChannelReadResult(
                path,
                records,
                records.Count,
                truncated,
                EventChannelErrorKind.ChannelNotFound,
                ex.Message);
        }
        catch (EventLogException ex)
        {
            return new EventChannelReadResult(
                path,
                records,
                records.Count,
                truncated,
                EventChannelErrorKind.ReadFailed,
                ex.Message);
        }
    }
}
