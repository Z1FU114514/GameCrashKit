using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Rules;

public sealed record RuleEvaluationContext(
    ScanRequest Request,
    CrashIncident? SelectedIncident,
    IReadOnlyList<BackgroundEventRecord> SystemBackgroundEvents,
    IReadOnlyList<SourceCoverage> Coverage,
    EnvironmentSnapshot? Environment);

public interface IDiagnosisRule
{
    string RuleId { get; }
    string RuleVersion { get; }
    int SpecificityRank { get; }
    DiagnosisFinding? Evaluate(RuleEvaluationContext context);
}
