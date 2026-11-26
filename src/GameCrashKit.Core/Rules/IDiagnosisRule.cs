using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Rules;

public sealed record RuleEvaluationContext(
    ScanRequest Request,
    CrashIncident? SelectedIncident,
    IReadOnlyList<BackgroundEventRecord> SystemBackgroundEvents,
    IReadOnlyList<SourceCoverage> Coverage,
    EnvironmentSnapshot? Environment,
    UserConfirmedEngineLog? StandaloneLog = null,
    ReportSubjectKind SubjectKind = ReportSubjectKind.InsufficientEvidence,
    string? WindowsScanStateLabel = null)
{
    public UserConfirmedEngineLog? ActiveLog => SelectedIncident?.ConfirmedEngineLog ?? StandaloneLog;
}

public interface IDiagnosisRule
{
    string RuleId { get; }
    string RuleVersion { get; }
    int SpecificityRank { get; }
    DiagnosisFinding? Evaluate(RuleEvaluationContext context);
}
