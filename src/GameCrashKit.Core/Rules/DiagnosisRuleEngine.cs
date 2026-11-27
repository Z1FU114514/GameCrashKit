using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Rules;

/// <summary>
/// Deterministic evaluator for v0.1 pure C# versioned diagnosis rules.
/// </summary>
public sealed class DiagnosisRuleEngine
{
    public const string ReportSchemaVersion = "1.0.0";
    public const string RulesetVersion = "v0.2-alpha.1";
    public const int MaxPrimaryFindings = 3;

    private readonly IReadOnlyList<IDiagnosisRule> _rules;

    public DiagnosisRuleEngine(IReadOnlyList<IDiagnosisRule>? rules = null)
    {
        _rules = rules ?? BuiltinRules.All;
    }

    public DiagnosisReport Evaluate(
        RuleEvaluationContext context,
        IReadOnlyList<EvidenceItem> allEvidence,
        string? appVersion = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allEvidence);

        var resolvedVersion = string.IsNullOrWhiteSpace(appVersion)
            ? AppVersionInfo.Current
            : appVersion.Trim();

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
        // then sort by SpecificityRank desc and RuleId ordinal asc, cap primary findings at MaxPrimaryFindings (3),
        // and preserve any remaining clues in AdditionalHints.
        var orderedFindings = rawFindings
            .GroupBy(static f => f.RuleId, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static f => StrengthSortOrder(f.EvidenceStrength))
            .ThenByDescending(static f => f.SpecificityRank)
            .ThenBy(static f => f.RuleId, StringComparer.Ordinal)
            .ToList();

        var primaryFindings = orderedFindings.Take(MaxPrimaryFindings).ToList();
        var additionalHints = orderedFindings.Skip(MaxPrimaryFindings).ToList();

        var effectiveSubjectKind = context.SubjectKind != ReportSubjectKind.InsufficientEvidence
            ? context.SubjectKind
            : context.SelectedIncident is not null
                ? ReportSubjectKind.WindowsIncident
                : context.StandaloneLog is not null
                    ? ReportSubjectKind.StandaloneLogOnly
                    : ReportSubjectKind.InsufficientEvidence;

        return new DiagnosisReport(
            SchemaVersion: ReportSchemaVersion,
            AppVersion: resolvedVersion,
            RulesetVersion: RulesetVersion,
            GeneratedAtUtc: generatedAtUtc ?? DateTimeOffset.UtcNow,
            ScanRequest: context.Request,
            SelectedIncident: context.SelectedIncident,
            PrimaryFindings: primaryFindings,
            SystemBackgroundEvents: context.SystemBackgroundEvents,
            Coverage: context.Coverage,
            Environment: context.Environment,
            AllEvidence: allEvidence,
            AdditionalHints: additionalHints,
            SubjectKind: effectiveSubjectKind,
            StandaloneLog: context.StandaloneLog,
            WindowsScanStateLabel: context.WindowsScanStateLabel);
    }

    private static int StrengthSortOrder(EvidenceStrength strength) => strength switch
    {
        EvidenceStrength.ExplicitRecord => 0,
        EvidenceStrength.LimitedClue => 1,
        EvidenceStrength.Insufficient => 2,
        _ => 3
    };
}
