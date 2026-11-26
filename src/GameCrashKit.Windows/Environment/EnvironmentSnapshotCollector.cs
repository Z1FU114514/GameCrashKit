using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Windows.Environment;

/// <summary>
/// Runs all 7 read-only neutral environment probes concurrently with per-probe timeout and exception isolation.
/// </summary>
public static class EnvironmentSnapshotCollector
{
    public const int DefaultPerProbeTimeoutMs = 2_000;

    public static async Task<EnvironmentSnapshot> CollectAsync(
        string? targetAppPath = null,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var lockObj = new object();

        void AddWarning(string msg)
        {
            lock (lockObj)
            {
                warnings.Add(msg);
            }
        }

        var targetPathFact = PathClassifier.EvaluateTargetPathFact(targetAppPath);

        var gpuTask = RunWithIsolationAsync<IReadOnlyList<GpuAdapterFact>>(
            "显卡信息",
            GpuAdapterProbe.Probe,
            [],
            AddWarning,
            cancellationToken);

        var oneDriveTask = RunWithIsolationAsync(
            "OneDrive 文档检查",
            OneDriveDocsProbe.Probe,
            new OneDriveDocsFact(false, false, false, EnvironmentNoticeLevel.Unavailable, "检查超时或失败。"),
            AddWarning,
            cancellationToken);

        var vcTask = RunWithIsolationAsync(
            "VC++ 运行库检查",
            static () =>
            {
                var r = VcRuntimeProbe.Probe();
                var level = (r.X64.IsInstalled && r.X86.IsInstalled)
                    ? EnvironmentNoticeLevel.Info
                    : EnvironmentNoticeLevel.Attention;
                return new VcRuntimeFact(r.X64.IsInstalled, r.X64.Version, r.X86.IsInstalled, r.X86.Version, level);
            },
            new VcRuntimeFact(false, null, false, null, EnvironmentNoticeLevel.Unavailable),
            AddWarning,
            cancellationToken);

        var pageFileTask = RunWithIsolationAsync(
            "页面文件检查",
            PageFileProbe.Probe,
            new PageFileFact(false, false, 0, 0, false, EnvironmentNoticeLevel.Unavailable, "页面文件探测失败或超时。"),
            AddWarning,
            cancellationToken);

        var overlaysTask = RunWithIsolationAsync(
            "覆盖层进程快照",
            RunningOverlaysProbe.Probe,
            new RunningOverlaysFact(false, [], "覆盖层快照探测失败或超时。"),
            AddWarning,
            cancellationToken);

        var sysInfoTask = RunWithIsolationAsync(
            "系统与区域设置",
            SystemLocaleAndSecurityProbe.Probe,
            new SystemLocaleAndSecurityFact("Windows", 0, 0, 0, false, null),
            AddWarning,
            cancellationToken);

        await Task.WhenAll(gpuTask, oneDriveTask, vcTask, pageFileTask, overlaysTask, sysInfoTask).ConfigureAwait(false);

        return new EnvironmentSnapshot(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            GpuAdapters: await gpuTask.ConfigureAwait(false),
            TargetPath: targetPathFact,
            OneDriveDocs: await oneDriveTask.ConfigureAwait(false),
            VcRuntime: await vcTask.ConfigureAwait(false),
            PageFile: await pageFileTask.ConfigureAwait(false),
            RunningOverlays: await overlaysTask.ConfigureAwait(false),
            SystemInfo: await sysInfoTask.ConfigureAwait(false),
            ProbeWarnings: warnings);
    }

    private static async Task<T> RunWithIsolationAsync<T>(
        string probeName,
        Func<T> probeAction,
        T fallbackValue,
        Action<string> addWarning,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultPerProbeTimeoutMs);

            var workTask = Task.Run(probeAction, timeoutCts.Token);
            var completed = await Task.WhenAny(workTask, Task.Delay(DefaultPerProbeTimeoutMs, cancellationToken)).ConfigureAwait(false);
            if (completed != workTask)
            {
                addWarning($"{probeName} 超时（>{DefaultPerProbeTimeoutMs}ms），已跳过。");
                return fallbackValue;
            }

            return await workTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            addWarning($"{probeName} 已取消。");
            return fallbackValue;
        }
        catch (Exception ex)
        {
            addWarning($"{probeName} 降级跳过：{ex.Message}");
            return fallbackValue;
        }
    }
}
