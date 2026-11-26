using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Parsers;
using GameCrashKit.Core.Rules;
using GameCrashKit.Windows.Environment;
using GameCrashKit.Windows.EventLog;

namespace GameCrashKit.Windows.Scanning;

public sealed record ScanSessionResult(
    bool IsBusy,
    bool WasCancelled,
    bool TimedOut,
    ScanRequest Request,
    CorrelateResult Correlated,
    IReadOnlyList<SourceCoverage> Coverage,
    EnvironmentSnapshot? Environment,
    IReadOnlyList<string> ParseWarnings);

/// <summary>
/// Coordinates single-flight, read-only Windows scanning with a 10-second total soft deadline,
/// 1,000 events/channel limit, and per-source graceful degradation.
/// </summary>
public sealed class WindowsScanCoordinator
{
    public const int SoftDeadlineMs = 10_000;

    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly DiagnosisRuleEngine _ruleEngine = new();

    public async Task<ScanSessionResult> ScanAsync(
        ScanWindowPreset windowPreset = ScanWindowPreset.Last2Hours,
        bool includeAllApplications = false,
        int softDeadlineMs = SoftDeadlineMs,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var request = new ScanRequest(
            WindowStartUtc: nowUtc - windowPreset.ToTimeSpan(),
            WindowEndUtc: nowUtc,
            IncludeAllApplications: includeAllApplications,
            MaxEventsPerChannel: WindowsEventLogReader.DefaultMaxEventsPerChannel);

        if (!await _scanLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ScanSessionResult(
                IsBusy: true,
                WasCancelled: false,
                TimedOut: false,
                Request: request,
                Correlated: new CorrelateResult([], 0, [], []),
                Coverage: [],
                Environment: null,
                ParseWarnings: ["已有扫描任务正在运行，请等待当前扫描完成或先取消。"]);
        }

        try
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadlineCts.CancelAfter(softDeadlineMs);

            var appXPath = WindowsEventLogReader.BuildApplicationCrashXPath(request.WindowStartUtc.UtcDateTime);
            var sysXPath = WindowsEventLogReader.BuildSystemBackgroundXPath(request.WindowStartUtc.UtcDateTime);

            var appTask = WindowsEventLogReader.QueryChannelAsync("Application", appXPath, request.MaxEventsPerChannel, deadlineCts.Token);
            var sysTask = WindowsEventLogReader.QueryChannelAsync("System", sysXPath, request.MaxEventsPerChannel, deadlineCts.Token);
            var envTask = EnvironmentSnapshotCollector.CollectAsync(targetAppPath: null, deadlineCts.Token);

            var allTasks = Task.WhenAll(appTask, sysTask, envTask);
            var timedOut = false;

            try
            {
                await allTasks.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadlineCts.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User cancelled; collect whatever finished cleanly below.
            }

            if (!cancellationToken.IsCancellationRequested && deadlineCts.IsCancellationRequested)
            {
                timedOut = true;
            }

            var appResult = await ExtractChannelResultAsync(appTask, "Application", timedOut, cancellationToken).ConfigureAwait(false);
            var sysResult = await ExtractChannelResultAsync(sysTask, "System", timedOut, cancellationToken).ConfigureAwait(false);

            EnvironmentSnapshot? envSnapshot = envTask.IsCompletedSuccessfully
                ? await envTask.ConfigureAwait(false)
                : null;

            var parsedRecords = new List<ParsedEventRecord>();
            var parseWarnings = new List<string>();

            ParseChannelRecords(appResult.RawXmlRecords, parsedRecords, parseWarnings);
            ParseChannelRecords(sysResult.RawXmlRecords, parsedRecords, parseWarnings);

            var correlated = IncidentCorrelator.Correlate(request, parsedRecords);

            var coverage = new List<SourceCoverage>
            {
                MapCoverage("Windows Application 事件 (1000/1001/1002)", request, appResult, timedOut),
                MapCoverage("Windows System 背景 (Kernel-Power 41)", request, sysResult, timedOut)
            };

            return new ScanSessionResult(
                IsBusy: false,
                WasCancelled: cancellationToken.IsCancellationRequested,
                TimedOut: timedOut,
                Request: request,
                Correlated: correlated,
                Coverage: coverage,
                Environment: envSnapshot,
                ParseWarnings: parseWarnings);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public DiagnosisReport BuildDiagnosisReport(
        ScanSessionResult session,
        CrashIncident? selectedIncident,
        UserConfirmedEngineLog? confirmedEngineLog = null,
        string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var incidentWithEngine = selectedIncident is not null && confirmedEngineLog is not null
            ? selectedIncident with { ConfirmedEngineLog = confirmedEngineLog }
            : selectedIncident;

        var environment = session.Environment;
        if (environment is not null && incidentWithEngine is not null)
        {
            environment = environment with
            {
                TargetPath = PathClassifier.EvaluateTargetPathFact(incidentWithEngine.NormalizedAppPath)
            };
        }

        var context = new RuleEvaluationContext(
            Request: session.Request,
            SelectedIncident: incidentWithEngine,
            SystemBackgroundEvents: session.Correlated.SystemBackgroundEvents,
            Coverage: session.Coverage,
            Environment: environment);

        return _ruleEngine.Evaluate(context, session.Correlated.AllEvidence, appVersion);
    }

    private static async Task<EventChannelReadResult> ExtractChannelResultAsync(
        Task<EventChannelReadResult> task,
        string channelName,
        bool timedOut,
        CancellationToken userToken)
    {
        if (task.IsCompletedSuccessfully)
        {
            return await task.ConfigureAwait(false);
        }

        if (userToken.IsCancellationRequested)
        {
            return new EventChannelReadResult(channelName, [], 0, false, EventChannelErrorKind.Cancelled, "用户已取消扫描。");
        }

        if (timedOut)
        {
            return new EventChannelReadResult(channelName, [], 0, true, EventChannelErrorKind.ReadFailed, "超过 10 秒软超时限制。");
        }

        return new EventChannelReadResult(channelName, [], 0, false, EventChannelErrorKind.ReadFailed, task.Exception?.GetBaseException().Message ?? "读取失败。");
    }

    private static void ParseChannelRecords(
        IReadOnlyList<string> rawXmlRecords,
        List<ParsedEventRecord> output,
        List<string> warnings)
    {
        foreach (var xml in rawXmlRecords)
        {
            try
            {
                output.Add(EventXmlParser.Parse(xml));
            }
            catch (Exception ex)
            {
                warnings.Add($"跳过畸形事件 XML：{ex.Message}");
            }
        }
    }

    private static SourceCoverage MapCoverage(
        string sourceName,
        ScanRequest request,
        EventChannelReadResult result,
        bool softTimedOut)
    {
        var status = result.ErrorKind switch
        {
            EventChannelErrorKind.None when result.Truncated => SourceCoverageStatus.Truncated,
            EventChannelErrorKind.None => SourceCoverageStatus.Ok,
            EventChannelErrorKind.AccessDenied => SourceCoverageStatus.AccessDenied,
            EventChannelErrorKind.ChannelNotFound => SourceCoverageStatus.NotFound,
            EventChannelErrorKind.Cancelled => SourceCoverageStatus.Cancelled,
            _ when softTimedOut => SourceCoverageStatus.PartialTimeout,
            _ => SourceCoverageStatus.Failed
        };

        return new SourceCoverage(
            SourceName: sourceName,
            RequestedStartUtc: request.WindowStartUtc,
            RequestedEndUtc: request.WindowEndUtc,
            RecordsRead: result.TotalRead,
            Truncated: result.Truncated,
            Status: status,
            Detail: result.ErrorMessage);
    }
}
