using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Rules;

/// <summary>
/// Deterministic evaluator for v0.1 pure C# versioned diagnosis rules.
/// </summary>
public sealed class DiagnosisRuleEngine
{
    public const string ReportSchemaVersion = "1.0.0";
    public const string RulesetVersion = "v0.1-m1.1";
    public const int MaxPrimaryFindings = 3;

    private readonly IReadOnlyList<IDiagnosisRule> _rules;

    public DiagnosisRuleEngine(IReadOnlyList<IDiagnosisRule>? rules = null)
    {
        _rules = rules ?? BuiltinRules.All;
    }

    public DiagnosisReport Evaluate(
        RuleEvaluationContext context,
        IReadOnlyList<EvidenceItem> allEvidence,
        string appVersion = "0.1.0-alpha",
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allEvidence);

        var rawFindings = new List<DiagnosisFinding>();
        foreach (var rule in _rules)
        {
            var finding = rule.Evaluate(context);
            if (finding is not null)
            {
                rawFindings.Add(finding);
            }
        }

        // Deduplicate by RuleId, sort ExplicitRecord facts before LimitedClue hints so low-strength clues never evict direct facts,
        // then sort by SpecificityRank desc and RuleId ordinal asc, and cap at MaxPrimaryFindings (3).
        var primaryFindings = rawFindings
            .GroupBy(static f => f.RuleId, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static f => StrengthSortOrder(f.EvidenceStrength))
            .ThenByDescending(static f => f.SpecificityRank)
            .ThenBy(static f => f.RuleId, StringComparer.Ordinal)
            .Take(MaxPrimaryFindings)
            .ToList();

        return new DiagnosisReport(
            SchemaVersion: ReportSchemaVersion,
            AppVersion: appVersion,
            RulesetVersion: RulesetVersion,
            GeneratedAtUtc: generatedAtUtc ?? DateTimeOffset.UtcNow,
            ScanRequest: context.Request,
            SelectedIncident: context.SelectedIncident,
            PrimaryFindings: primaryFindings,
            SystemBackgroundEvents: context.SystemBackgroundEvents,
            Coverage: context.Coverage,
            Environment: context.Environment,
            AllEvidence: allEvidence);
    }

    private static int StrengthSortOrder(EvidenceStrength strength) => strength switch
    {
        EvidenceStrength.ExplicitRecord => 0,
        EvidenceStrength.LimitedClue => 1,
        EvidenceStrength.Insufficient => 2,
        _ => 3
    };
}
