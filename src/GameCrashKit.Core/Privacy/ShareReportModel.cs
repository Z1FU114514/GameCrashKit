using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Privacy;

public sealed record ShareOptions(
    bool HideAppName = false,
    bool HideExactTimestamp = false,
    bool IncludeScrubbedErrorExcerpt = false);

public sealed record ShareFindingItem(
    string RuleTag,
    string Title,
    string AttributionText,
    string EvidenceStrengthText,
    EvidenceStrength EvidenceStrength,
    string Statement,
    string NextActionText,
    IReadOnlyList<string>? AmbiguityNotes = null);

public sealed record ShareEnvironmentItem(
    string Label,
    string Value,
    string NoticeLabel,
    EnvironmentNoticeLevel NoticeLevel);

/// <summary>
/// Immutable, strictly whitelisted projection shared identically by Markdown and PNG exporters.
/// Never carries raw XML, unapproved free text, absolute paths, Report IDs, PIDs, command lines, or call stacks.
/// </summary>
public sealed record ShareReportModel(
    string SchemaVersion,
    string AppVersion,
    string RulesetVersion,
    string PrivacyBadgeText,
    string IncidentKindLabel,
    string AppDisplayName,
    string DisambiguatedPathSuffix,
    string ModuleName,
    string ExceptionCode,
    string OccurrenceSummary,
    string TimestampDisplay,
    IReadOnlyList<ShareFindingItem> PrimaryFindings,
    IReadOnlyList<string> BackgroundSummaryLines,
    IReadOnlyList<ShareEnvironmentItem> EnvironmentFacts,
    IReadOnlyList<string> CoverageSummaryLines,
    bool HasAvailableExcerptSource,
    bool HasOptInExcerpt,
    IReadOnlyList<string> OptInScrubbedExcerptLines,
    IReadOnlyList<ShareFindingItem>? AdditionalHints = null,
    string? ScanScopeSummary = null,
    IReadOnlyList<string>? SourceLimitationNotes = null);
