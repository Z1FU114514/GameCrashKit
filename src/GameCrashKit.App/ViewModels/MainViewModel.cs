using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using GameCrashKit.Core;
using GameCrashKit.Core.Models;
using GameCrashKit.Windows.FileSystem;
using GameCrashKit.Windows.Scanning;

namespace GameCrashKit.App.ViewModels;

public enum AppUiState
{
    Idle = 0,
    Scanning,
    ReadyWithReport,
    PartialCompleted,
    NoRecords,
    Failed,
    Cancelled
}

public sealed record WindowPresetOption(ScanWindowPreset Preset, string DisplayName);

public sealed record CandidateItemViewModel(
    CrashIncident Model,
    string AnchorBadgeText,
    string DisplayTitle,
    string TimeDisplayText,
    string SubtitleText,
    string OccurrenceBadgeText,
    bool HasMultipleOccurrences,
    bool IsLikelyGame);

public sealed record FindingCardViewModel(
    string RuleTag,
    string Title,
    string AttributionText,
    string EvidenceStrengthText,
    EvidenceStrength EvidenceStrength,
    string Statement,
    string AmbiguityText,
    bool HasAmbiguity,
    string NextActionText,
    string? OfficialReferenceLabel,
    string? OfficialReferenceUrl,
    bool HasOfficialReference);

public sealed record EvidenceRowViewModel(
    string SourceKindText,
    string TimestampText,
    string SourceLocation,
    string SummaryText);

public sealed record BackgroundRowViewModel(
    string TimestampText,
    string SummaryText,
    string AttributionText);

public sealed record CoverageRowViewModel(
    string SourceName,
    string StatusText,
    int RecordsRead,
    string DetailText);

public sealed record EnvironmentFactRowViewModel(
    string Name,
    string ValueText,
    string NoticeBadgeText,
    EnvironmentNoticeLevel NoticeLevel);

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly WindowsScanCoordinator _coordinator = new();
    private CancellationTokenSource? _activeScanCts;
    private ScanSessionResult? _currentSession;
    private readonly Dictionary<string, (UserConfirmedEngineLog Log, string FullPath)> _incidentEngineLogs = new(StringComparer.Ordinal);
    private (UserConfirmedEngineLog Log, string FullPath)? _standaloneEngineLog;
    private bool _isViewingStandaloneLog;

    private WindowPresetOption _selectedWindowPreset;
    private bool _includeAllApplications;
    private AppUiState _uiState = AppUiState.Idle;
    private bool _isScanning;
    private string _statusHeadline = "准备就绪 — 纯本地只读诊断";
    private string _statusDetail = "点击「开始只读诊断 (F5)」扫描最近应用故障与系统背景；不会改动系统配置，也不会访问游戏进程内存。";
    private bool _hasPartialWarning;
    private string _partialWarningText = string.Empty;
    private string _filteredNoiseHint = string.Empty;
    private CandidateItemViewModel? _selectedCandidate;
    private bool _hasAttachedEngineLog;
    private string _attachedEngineLogSummary = "尚未确认关联任何游戏引擎日志";
    private string _engineLogFeedbackMessage = string.Empty;
    private string _reportHeaderSummary = "尚未生成诊断报告";
    private DiagnosisReport? _currentReport;

    public MainViewModel()
    {
        WindowPresets =
        [
            new WindowPresetOption(ScanWindowPreset.Last2Hours, "最近 2 小时（急诊推荐）"),
            new WindowPresetOption(ScanWindowPreset.Last24Hours, "最近 24 小时"),
            new WindowPresetOption(ScanWindowPreset.Last7Days, "最近 7 天")
        ];
        _selectedWindowPreset = WindowPresets[0];

        StartScanCommand = new AsyncRelayCommand(ExecuteScanAsync, () => !IsScanning);
        CancelScanCommand = new RelayCommand(ExecuteCancelScan, () => IsScanning);
        AttachDiscoveredLogCommand = new RelayCommand(OnAttachDiscoveredLog);
        ClearEngineLogCommand = new RelayCommand(OnClearEngineLog, () => HasAttachedEngineLog);
        ViewStandaloneLogCommand = new RelayCommand(OnViewStandaloneLog, () => HasAnyStandaloneOrActiveLog);
        BindStandaloneLogToSelectedCandidateCommand = new RelayCommand(OnBindStandaloneLogToSelectedCandidate, () => HasCandidates && HasAnyStandaloneOrActiveLog);
        OpenReferenceUrlCommand = new RelayCommand(OnOpenReferenceUrl);
    }

    public string AppVersionDisplay => $"v{AppVersionInfo.Current}";
    public string WindowTitle => $"GameCrash Kit（游戏闪退急诊室）v{AppVersionInfo.Current}";

    public string? ActiveAttachedEngineLogFullPath => GetActiveAttachedEngineLogEntry()?.FullPath;

    public bool IsViewingStandaloneLog
    {
        get => _isViewingStandaloneLog;
        private set
        {
            if (SetProperty(ref _isViewingStandaloneLog, value))
            {
                OnPropertyChanged(nameof(CanSwitchLogViewMode));
            }
        }
    }

    public bool HasAnyStandaloneOrActiveLog => _standaloneEngineLog is not null || HasAttachedEngineLog;
    public bool CanSwitchLogViewMode => HasCandidates && HasAnyStandaloneOrActiveLog;

    public IReadOnlyList<WindowPresetOption> WindowPresets { get; }

    public WindowPresetOption SelectedWindowPreset
    {
        get => _selectedWindowPreset;
        set => SetProperty(ref _selectedWindowPreset, value);
    }

    public bool IncludeAllApplications
    {
        get => _includeAllApplications;
        set => SetProperty(ref _includeAllApplications, value);
    }

    public AppUiState UiState
    {
        get => _uiState;
        private set
        {
            if (SetProperty(ref _uiState, value))
            {
                OnPropertyChanged(nameof(ShowStatusBanner));
                OnPropertyChanged(nameof(ShowIdleWelcome));
            }
        }
    }

    public bool ShowStatusBanner => UiState is AppUiState.Scanning or AppUiState.PartialCompleted or AppUiState.NoRecords or AppUiState.Failed or AppUiState.Cancelled;
    public bool ShowIdleWelcome => UiState == AppUiState.Idle;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                StartScanCommand.RaiseCanExecuteChanged();
                CancelScanCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowNoCandidatesEmptyState));
            }
        }
    }

    public string StatusHeadline
    {
        get => _statusHeadline;
        private set => SetProperty(ref _statusHeadline, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public bool HasPartialWarning
    {
        get => _hasPartialWarning;
        private set => SetProperty(ref _hasPartialWarning, value);
    }

    public string PartialWarningText
    {
        get => _partialWarningText;
        private set => SetProperty(ref _partialWarningText, value);
    }

    public string FilteredNoiseHint
    {
        get => _filteredNoiseHint;
        private set
        {
            if (SetProperty(ref _filteredNoiseHint, value))
            {
                OnPropertyChanged(nameof(HasFilteredNoiseHint));
            }
        }
    }

    public bool HasFilteredNoiseHint => !string.IsNullOrWhiteSpace(FilteredNoiseHint);

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = [];

    public CandidateItemViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value))
            {
                IsViewingStandaloneLog = false;
                SyncAttachedLogUiForSelectedCandidate();
                RebuildReport();
            }
        }
    }

    public ObservableCollection<DiscoveredEngineLogCandidate> DiscoveredEngineLogs { get; } = [];

    public bool HasAttachedEngineLog
    {
        get => _hasAttachedEngineLog;
        private set
        {
            if (SetProperty(ref _hasAttachedEngineLog, value))
            {
                ClearEngineLogCommand.RaiseCanExecuteChanged();
                ViewStandaloneLogCommand.RaiseCanExecuteChanged();
                BindStandaloneLogToSelectedCandidateCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(HasAnyStandaloneOrActiveLog));
                OnPropertyChanged(nameof(CanSwitchLogViewMode));
            }
        }
    }

    public string AttachedEngineLogSummary
    {
        get => _attachedEngineLogSummary;
        private set => SetProperty(ref _attachedEngineLogSummary, value);
    }

    public string EngineLogFeedbackMessage
    {
        get => _engineLogFeedbackMessage;
        private set
        {
            if (SetProperty(ref _engineLogFeedbackMessage, value))
            {
                OnPropertyChanged(nameof(HasEngineLogFeedback));
            }
        }
    }

    public bool HasEngineLogFeedback => !string.IsNullOrWhiteSpace(EngineLogFeedbackMessage);

    public string ReportHeaderSummary
    {
        get => _reportHeaderSummary;
        private set => SetProperty(ref _reportHeaderSummary, value);
    }

    public DiagnosisReport? CurrentReport
    {
        get => _currentReport;
        private set
        {
            if (SetProperty(ref _currentReport, value))
            {
                OnPropertyChanged(nameof(HasReport));
            }
        }
    }

    public bool HasReport => CurrentReport is not null;
    public bool HasCandidates => Candidates.Count > 0;
    public bool ShowNoCandidatesEmptyState => !IsScanning && Candidates.Count == 0;
    public bool HasDiscoveredEngineLogs => DiscoveredEngineLogs.Count > 0;
    public bool HasFindings => PrimaryFindings.Count > 0;
    public bool HasEnvironmentFacts => EnvironmentItems.Count > 0;
    public bool HasSystemBackground => SystemBackgroundItems.Count > 0;
    public bool HasEvidenceItems => EvidenceItems.Count > 0;
    public bool HasCoverageItems => CoverageItems.Count > 0;

    public ObservableCollection<FindingCardViewModel> PrimaryFindings { get; } = [];
    public ObservableCollection<EvidenceRowViewModel> EvidenceItems { get; } = [];
    public ObservableCollection<BackgroundRowViewModel> SystemBackgroundItems { get; } = [];
    public ObservableCollection<CoverageRowViewModel> CoverageItems { get; } = [];
    public ObservableCollection<EnvironmentFactRowViewModel> EnvironmentItems { get; } = [];

    public AsyncRelayCommand StartScanCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public RelayCommand AttachDiscoveredLogCommand { get; }
    public RelayCommand ClearEngineLogCommand { get; }
    public RelayCommand ViewStandaloneLogCommand { get; }
    public RelayCommand BindStandaloneLogToSelectedCandidateCommand { get; }
    public RelayCommand OpenReferenceUrlCommand { get; }

    public Task AttachManualLogFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.CompletedTask;
        }

        var loadResult = EngineLogDiscovery.LoadAndParseUserSelectedFile(filePath);
        ApplyEngineLogLoadResult(loadResult, filePath);
        return Task.CompletedTask;
    }

    internal void LoadSessionForTest(ScanSessionResult session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _currentSession = session;
        _incidentEngineLogs.Clear();
        IsViewingStandaloneLog = false;
        PopulateCandidates(session);
        SelectedCandidate = Candidates.FirstOrDefault();
        SyncAttachedLogUiForSelectedCandidate();
        RebuildReport();
    }

    private async Task ExecuteScanAsync()
    {
        _activeScanCts?.Dispose();
        _activeScanCts = new CancellationTokenSource();
        var ct = _activeScanCts.Token;

        IsScanning = true;
        UiState = AppUiState.Scanning;
        HasPartialWarning = false;
        PartialWarningText = string.Empty;
        EngineLogFeedbackMessage = string.Empty;
        StatusHeadline = $"正在只读扫描（{SelectedWindowPreset.Preset.ToDisplayText()}）...";
        StatusDetail = "正在安全读取 Windows 事件日志、7 项中性环境事实及近期引擎日志候选（软超时上限 10 秒）。";

        try
        {
            var preset = SelectedWindowPreset.Preset;
            var includeAll = IncludeAllApplications;
            var windowStartUtc = DateTimeOffset.UtcNow - preset.ToTimeSpan();

            var scanTask = _coordinator.ScanAsync(preset, includeAll, WindowsScanCoordinator.SoftDeadlineMs, ct);
            var discoveryTask = Task.Run(() => EngineLogDiscovery.DiscoverInWindow(windowStartUtc, cancellationToken: ct), ct);

            var session = await scanTask;
            IReadOnlyList<DiscoveredEngineLogCandidate> discovered = [];
            try
            {
                discovered = await discoveryTask;
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation on discovery when scan is cancelled.
            }

            if (session.IsBusy)
            {
                UiState = AppUiState.PartialCompleted;
                StatusHeadline = "已有扫描正在运行";
                StatusDetail = "同一窗口仅允许一个活跃扫描任务，请稍候。";
                return;
            }

            if (session.WasCancelled)
            {
                UiState = AppUiState.Cancelled;
                StatusHeadline = "已取消扫描";
                StatusDetail = "本次扫描已中止，未向磁盘写入任何数据。";
                return;
            }

            _currentSession = session;
            _incidentEngineLogs.Clear();

            PopulateDiscoveredLogs(discovered);
            PopulateCandidates(session);

            var degradedSources = session.Coverage
                .Where(static c => c.Status != SourceCoverageStatus.Ok)
                .ToList();

            if (session.TimedOut || degradedSources.Count > 0)
            {
                HasPartialWarning = true;
                PartialWarningText = session.TimedOut
                    ? "部分数据源在 10 秒软超时内未完全返回，当前展示已采集到的部分结果。"
                    : $"部分来源受限或截断：{string.Join("；", degradedSources.Select(static c => $"{c.SourceName} ({c.Status})"))}";
            }

            if (Candidates.Count == 0)
            {
                UiState = AppUiState.NoRecords;
                StatusHeadline = "未发现符合条件的应用闪退或无响应候选";
                StatusDetail = session.Correlated.FilteredNoiseCount > 0
                    ? $"已按默认策略过滤 {session.Correlated.FilteredNoiseCount} 条系统常见进程记录；可勾选「显示全部应用事件」或扩大时间窗、手选日志。"
                    : "请尝试切换为「最近 24 小时 / 7 天」，或直接拖入单个日志生成独立诊断报告。";
                IsViewingStandaloneLog = _standaloneEngineLog is not null;
                SelectedCandidate = null;
                SyncAttachedLogUiForSelectedCandidate();
                RebuildReport();
            }
            else
            {
                UiState = HasPartialWarning ? AppUiState.PartialCompleted : AppUiState.ReadyWithReport;
                StatusHeadline = $"扫描完成：共发现 {Candidates.Count} 个事故候选";
                StatusDetail = "请在左侧核对并确认对应程序与时间点；右侧结论与下一步已按当前选中事故实时生成。";
                IsViewingStandaloneLog = false;
                SelectedCandidate = Candidates[0];
                SyncAttachedLogUiForSelectedCandidate();
                RebuildReport();
            }
        }
        catch (OperationCanceledException)
        {
            UiState = AppUiState.Cancelled;
            StatusHeadline = "已取消扫描";
            StatusDetail = "本次扫描已由用户中止。";
        }
        catch (Exception ex)
        {
            UiState = AppUiState.Failed;
            StatusHeadline = "扫描出现意外异常";
            StatusDetail = $"无法完成扫描：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ExecuteCancelScan()
    {
        _activeScanCts?.Cancel();
        StatusHeadline = "正在取消扫描...";
    }

    private void OnAttachDiscoveredLog(object? parameter)
    {
        if (parameter is not DiscoveredEngineLogCandidate candidate)
        {
            return;
        }

        var loadResult = EngineLogDiscovery.LoadAndParseUserSelectedFile(
            candidate.FullPath,
            candidate.Kind,
            candidate.MayBeOverwritten);

        ApplyEngineLogLoadResult(loadResult, candidate.FullPath);
    }

    private void OnClearEngineLog()
    {
        if (IsViewingStandaloneLog || SelectedCandidate is null)
        {
            _standaloneEngineLog = null;
            IsViewingStandaloneLog = false;
        }
        else
        {
            _incidentEngineLogs.Remove(SelectedCandidate.Model.IncidentId);
        }

        SyncAttachedLogUiForSelectedCandidate();
        EngineLogFeedbackMessage = "已取消当前选中视图的日志关联。";
        RebuildReport();
    }

    private void OnViewStandaloneLog()
    {
        var active = GetActiveAttachedEngineLogEntry() ?? _standaloneEngineLog;
        if (active is null)
        {
            return;
        }

        _standaloneEngineLog = active;
        IsViewingStandaloneLog = true;
        SyncAttachedLogUiForSelectedCandidate();
        EngineLogFeedbackMessage = "已切换为「仅手选日志独立诊断」视图（不虚构 Windows 事件或进程归属）。";
        RebuildReport();
    }

    private void OnBindStandaloneLogToSelectedCandidate()
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        var active = GetActiveAttachedEngineLogEntry() ?? _standaloneEngineLog;
        if (active is null)
        {
            return;
        }

        _incidentEngineLogs[SelectedCandidate.Model.IncidentId] = active.Value;
        IsViewingStandaloneLog = false;
        SyncAttachedLogUiForSelectedCandidate();
        EngineLogFeedbackMessage = $"已将所选日志关联至当前事故 ({SelectedCandidate.DisplayTitle}) 并重新评估结论。";
        RebuildReport();
    }

    private void ApplyEngineLogLoadResult(EngineLogLoadResult result, string sourceFullPath)
    {
        if (!result.Success || result.EngineLog is null)
        {
            EngineLogFeedbackMessage = $"日志未加载：{result.ErrorMessage ?? "读取失败"}";
            return;
        }

        var normalizedPath = Path.GetFullPath(sourceFullPath.Trim());
        var entry = (result.EngineLog, normalizedPath);
        _standaloneEngineLog = entry;

        if (!IsViewingStandaloneLog && SelectedCandidate is not null)
        {
            _incidentEngineLogs[SelectedCandidate.Model.IncidentId] = entry;
            EngineLogFeedbackMessage = $"已将所选日志单独绑定至当前事故 ({SelectedCandidate.DisplayTitle}) 并重新评估结论。";
        }
        else
        {
            IsViewingStandaloneLog = true;
            EngineLogFeedbackMessage = _currentSession is null
                ? "已安全读取手选日志并生成独立诊断报告（Windows: 未扫描，可随时按 F5 补充事件扫描）。"
                : "已安全读取手选日志并生成仅日志独立诊断报告。";
        }

        SyncAttachedLogUiForSelectedCandidate();
        RebuildReport();
    }

    private (UserConfirmedEngineLog Log, string FullPath)? GetActiveAttachedEngineLogEntry()
    {
        if (IsViewingStandaloneLog || SelectedCandidate is null)
        {
            return _standaloneEngineLog;
        }

        return _incidentEngineLogs.TryGetValue(SelectedCandidate.Model.IncidentId, out var entry)
            ? entry
            : null;
    }

    private void SyncAttachedLogUiForSelectedCandidate()
    {
        var active = GetActiveAttachedEngineLogEntry();
        if (active is null)
        {
            HasAttachedEngineLog = false;
            AttachedEngineLogSummary = "尚未确认关联任何游戏引擎或应用日志";
            EngineLogFeedbackMessage = string.Empty;
            OnPropertyChanged(nameof(HasAnyStandaloneOrActiveLog));
            OnPropertyChanged(nameof(CanSwitchLogViewMode));
            ViewStandaloneLogCommand.RaiseCanExecuteChanged();
            BindStandaloneLogToSelectedCandidateCommand.RaiseCanExecuteChanged();
            return;
        }

        var log = active.Value.Log;
        HasAttachedEngineLog = true;
        var sigs = new List<string>();
        if (!string.IsNullOrWhiteSpace(log.EngineVersion)) sigs.Add($"引擎 {log.EngineVersion}");
        if (!string.IsNullOrWhiteSpace(log.RhiBackend)) sigs.Add(log.RhiBackend);
        if (log.Fatality == LogFatalityKind.NonFatalEnsure) sigs.Add("非致命 Ensure");
        else if (log.Fatality == LogFatalityKind.Fatal) sigs.Add("致命错误");
        if (log.HasGpuDeviceLostSignal) sigs.Add($"图形错误标记: {log.GpuLostToken}");
        if (log.HasOutOfMemorySignal) sigs.Add($"资源耗尽标记: {log.OomToken}");
        if (log.IsTruncated) sigs.Add("已截断末尾 2 MiB");

        AttachedEngineLogSummary = $"{log.SourceLocation}" + (sigs.Count > 0 ? $" · {string.Join(" | ", sigs)}" : string.Empty);
        OnPropertyChanged(nameof(HasAnyStandaloneOrActiveLog));
        OnPropertyChanged(nameof(CanSwitchLogViewMode));
        ViewStandaloneLogCommand.RaiseCanExecuteChanged();
        BindStandaloneLogToSelectedCandidateCommand.RaiseCanExecuteChanged();
    }

    private void PopulateDiscoveredLogs(IReadOnlyList<DiscoveredEngineLogCandidate> discovered)
    {
        DiscoveredEngineLogs.Clear();
        foreach (var item in discovered)
        {
            DiscoveredEngineLogs.Add(item);
        }

        OnPropertyChanged(nameof(HasDiscoveredEngineLogs));
    }

    private void PopulateCandidates(ScanSessionResult session)
    {
        Candidates.Clear();
        foreach (var inc in session.Correlated.Candidates)
        {
            var anchorBadge = inc.AnchorKind switch
            {
                IncidentAnchorKind.ApplicationCrash => "应用崩溃 1000",
                IncidentAnchorKind.ApplicationHang => "无响应 1002",
                IncidentAnchorKind.WerReportOnly => "仅有 WER 1001",
                _ => "未知"
            };

            var title = string.IsNullOrWhiteSpace(inc.AppName) ? "未知程序" : inc.AppName;
            var timeText = inc.OccurredAtUtc.HasValue
                ? inc.OccurredAtUtc.Value.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : "未知时间";

            var modPart = string.IsNullOrWhiteSpace(inc.ModuleName) ? string.Empty : $"模块: {inc.ModuleName}";
            var codePart = string.IsNullOrWhiteSpace(inc.ExceptionCode) ? string.Empty : $"异常: {inc.ExceptionCode}";
            var pathPart = string.IsNullOrWhiteSpace(inc.DisambiguatedPathSuffix) ? string.Empty : inc.DisambiguatedPathSuffix;
            var subtitle = string.Join(" · ", new[] { modPart, codePart, pathPart }.Where(static s => !string.IsNullOrEmpty(s)));
            if (string.IsNullOrWhiteSpace(subtitle))
            {
                subtitle = inc.PrimaryEvidence.SourceLocation;
            }

            Candidates.Add(new CandidateItemViewModel(
                Model: inc,
                AnchorBadgeText: anchorBadge,
                DisplayTitle: title,
                TimeDisplayText: timeText,
                SubtitleText: subtitle,
                OccurrenceBadgeText: $"本窗 {inc.OccurrenceCountInWindow} 次",
                HasMultipleOccurrences: inc.OccurrenceCountInWindow > 1,
                IsLikelyGame: inc.IsLikelyGame));
        }

        FilteredNoiseHint = session.Correlated.FilteredNoiseCount > 0
            ? $"已默认隐藏 {session.Correlated.FilteredNoiseCount} 条系统常见进程"
            : string.Empty;

        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(ShowNoCandidatesEmptyState));
        OnPropertyChanged(nameof(CanSwitchLogViewMode));
        ViewStandaloneLogCommand.RaiseCanExecuteChanged();
        BindStandaloneLogToSelectedCandidateCommand.RaiseCanExecuteChanged();
    }

    private void RebuildReport()
    {
        PrimaryFindings.Clear();
        EvidenceItems.Clear();
        SystemBackgroundItems.Clear();
        CoverageItems.Clear();
        EnvironmentItems.Clear();

        var activeLog = GetActiveAttachedEngineLogEntry()?.Log;
        var forceStandalone = IsViewingStandaloneLog && _standaloneEngineLog is not null;

        if (_currentSession is null && activeLog is null)
        {
            CurrentReport = null;
            ReportHeaderSummary = "尚未执行扫描";
            RaiseReportCollectionProperties();
            return;
        }

        var report = _coordinator.BuildDiagnosisReport(
            _currentSession,
            forceStandalone ? null : SelectedCandidate?.Model,
            activeLog,
            forceStandaloneLog: forceStandalone);
        CurrentReport = report;

        ReportHeaderSummary = report.SubjectKind switch
        {
            ReportSubjectKind.StandaloneLogOnly when report.StandaloneLog is not null =>
                $"仅手选日志诊断 · {report.StandaloneLog.EffectiveFormatLabel} · 规则集 {report.RulesetVersion}",
            ReportSubjectKind.WindowsIncident when report.SelectedIncident is not null =>
                $"{report.SelectedIncident.AppName ?? "未知程序"} · 规则集 {report.RulesetVersion}",
            _ => $"证据不足报告 · 规则集 {report.RulesetVersion}"
        };

        foreach (var f in report.PrimaryFindings)
        {
            var ambiguityText = f.AmbiguityNotes.Count > 0 ? string.Join(" ", f.AmbiguityNotes) : string.Empty;
            PrimaryFindings.Add(new FindingCardViewModel(
                RuleTag: $"{f.RuleId}@{f.RuleVersion}",
                Title: f.Title,
                AttributionText: $"归属: {f.Attribution.ToDisplayText()}",
                EvidenceStrengthText: $"证据: {f.EvidenceStrength.ToDisplayText()}",
                EvidenceStrength: f.EvidenceStrength,
                Statement: f.Statement,
                AmbiguityText: ambiguityText,
                HasAmbiguity: f.AmbiguityNotes.Count > 0,
                NextActionText: f.NextActionText,
                OfficialReferenceLabel: f.OfficialReferenceLabel,
                OfficialReferenceUrl: f.OfficialReferenceUrl,
                HasOfficialReference: !string.IsNullOrWhiteSpace(f.OfficialReferenceUrl)));
        }

        foreach (var ev in report.AllEvidence)
        {
            var ts = ev.TimestampUtc.HasValue
                ? ev.TimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : "未知";
            var summary = string.Join(" | ", ev.AllowedFields.Select(static kv => $"{kv.Key}={kv.Value}"));
            EvidenceItems.Add(new EvidenceRowViewModel(ev.SourceKind.ToString(), ts, ev.SourceLocation, summary));
        }

        foreach (var bg in report.SystemBackgroundEvents)
        {
            var ts = bg.Evidence.TimestampUtc.HasValue
                ? bg.Evidence.TimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
                : "未知";
            var summary = bg.SummaryKind == "KernelPower41"
                ? $"Kernel-Power 41 上一次异常关机背景（BugcheckCode={bg.BugcheckCode ?? 0}，记录时间为再次开机时刻）"
                : $"{bg.SummaryKind} ({bg.Evidence.SourceLocation})";
            SystemBackgroundItems.Add(new BackgroundRowViewModel(ts, summary, bg.Attribution.ToDisplayText()));
        }

        foreach (var cov in report.Coverage)
        {
            CoverageItems.Add(new CoverageRowViewModel(
                cov.SourceName,
                cov.Status.ToString(),
                cov.RecordsRead,
                cov.Detail ?? (cov.Truncated ? "达到单频道 1,000 条上限已截断" : "正常")));
        }

        PopulateEnvironmentFacts(report.Environment);
        RaiseReportCollectionProperties();
    }

    private void RaiseReportCollectionProperties()
    {
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasEnvironmentFacts));
        OnPropertyChanged(nameof(HasSystemBackground));
        OnPropertyChanged(nameof(HasEvidenceItems));
        OnPropertyChanged(nameof(HasCoverageItems));
    }

    private void PopulateEnvironmentFacts(EnvironmentSnapshot? env)
    {
        if (env is null)
        {
            return;
        }

        var gpuText = env.GpuAdapters.Count == 0
            ? "未检测到适配器"
            : string.Join("；", env.GpuAdapters.Select(static g =>
                $"{g.Name}（驱动 {g.DriverVersion ?? "未知"}，驱动包日期 {g.DriverReleaseDateIso ?? "未知"}{(g.IsLikelyVirtual ? "，虚拟适配器" : string.Empty)}）"));
        EnvironmentItems.Add(new EnvironmentFactRowViewModel("1. 显卡与驱动包日期", gpuText, "信息", EnvironmentNoticeLevel.Info));

        var pathText = !env.TargetPath.Checked
            ? "未选中具体故障程序，未检查路径特征"
            : $"后缀: {env.TargetPath.DisambiguatedSuffix ?? "未知"} · 非 ASCII: {(env.TargetPath.HasNonAscii ? "是" : "否")} · 长度 >260: {(env.TargetPath.ExceedsMaxPath260 ? "是" : "否")}";
        var pathLevel = env.TargetPath.Checked && (env.TargetPath.HasNonAscii || env.TargetPath.ExceedsMaxPath260)
            ? EnvironmentNoticeLevel.Attention
            : EnvironmentNoticeLevel.Info;
        EnvironmentItems.Add(new EnvironmentFactRowViewModel("2. 目标程序路径特征", pathText, pathLevel == EnvironmentNoticeLevel.Attention ? "留意" : "信息", pathLevel));

        EnvironmentItems.Add(new EnvironmentFactRowViewModel(
            "3. 文档目录 OneDrive 状态",
            env.OneDriveDocs.StatusText,
            ToNoticeLabel(env.OneDriveDocs.NoticeLevel),
            env.OneDriveDocs.NoticeLevel));

        var vcMissing = env.VcRuntime.NoticeLevel == EnvironmentNoticeLevel.Unavailable ? "探测不可用" : "未检测到登记";
        var vcText = $"x64: {(env.VcRuntime.X64Installed ? $"已装 ({env.VcRuntime.X64Version})" : vcMissing)} | x86: {(env.VcRuntime.X86Installed ? $"已装 ({env.VcRuntime.X86Version})" : vcMissing)}";
        EnvironmentItems.Add(new EnvironmentFactRowViewModel(
            "4. VC++ 2015-2022 运行库",
            vcText,
            ToNoticeLabel(env.VcRuntime.NoticeLevel),
            env.VcRuntime.NoticeLevel));

        EnvironmentItems.Add(new EnvironmentFactRowViewModel(
            "5. 虚拟内存 / 页面文件",
            env.PageFile.StatusText,
            ToNoticeLabel(env.PageFile.NoticeLevel),
            env.PageFile.NoticeLevel));

        var overlayText = env.RunningOverlays.DetectedOverlayNames.Count == 0
            ? $"未发现常见覆盖层进程（{env.RunningOverlays.Note}）"
            : $"{string.Join("、", env.RunningOverlays.DetectedOverlayNames)}（{env.RunningOverlays.Note}）";
        EnvironmentItems.Add(new EnvironmentFactRowViewModel(
            "6. 扫描瞬间常见覆盖层",
            overlayText,
            env.RunningOverlays.Checked ? "信息" : "不可用",
            env.RunningOverlays.Checked ? EnvironmentNoticeLevel.Info : EnvironmentNoticeLevel.Unavailable));

        var hvciStr = env.SystemInfo.HvciEnabled switch
        {
            true => "已开启",
            false => "未开启",
            null => "未知"
        };
        var sysText = $"{env.SystemInfo.OsDisplayLabel} · 内存 {env.SystemInfo.TotalPhysicalMemoryMb} MB · 代码页 {env.SystemInfo.AcpCodePage} (Beta UTF-8: {(env.SystemInfo.IsBetaUtf8Enabled ? "开" : "关")}) · HVCI: {hvciStr}";
        EnvironmentItems.Add(new EnvironmentFactRowViewModel("7. 系统与区域基线", sysText, "信息", EnvironmentNoticeLevel.Info));
    }

    private static string ToNoticeLabel(EnvironmentNoticeLevel level) => level switch
    {
        EnvironmentNoticeLevel.Attention => "留意",
        EnvironmentNoticeLevel.Unavailable => "不可用",
        _ => "信息"
    };

    private static void OnOpenReferenceUrl(object? parameter)
    {
        if (parameter is not string url ||
            !url.StartsWith("https://learn.microsoft.com/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Ignore shell launch error cleanly.
        }
    }

    public void Dispose()
    {
        _activeScanCts?.Cancel();
        _activeScanCts?.Dispose();
        _activeScanCts = null;
        _currentSession = null;
        _incidentEngineLogs.Clear();
        _standaloneEngineLog = null;
        CurrentReport = null;
        Candidates.Clear();
        DiscoveredEngineLogs.Clear();
        PrimaryFindings.Clear();
        EvidenceItems.Clear();
        SystemBackgroundItems.Clear();
        CoverageItems.Clear();
        EnvironmentItems.Clear();
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(ShowNoCandidatesEmptyState));
        OnPropertyChanged(nameof(HasDiscoveredEngineLogs));
        RaiseReportCollectionProperties();
    }
}
