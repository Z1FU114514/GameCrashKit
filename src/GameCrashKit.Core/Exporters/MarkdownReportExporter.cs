using System.Text;
using GameCrashKit.Core.Privacy;

namespace GameCrashKit.Core.Exporters;

/// <summary>
/// Renders deterministic LF Markdown directly from a whitelisted <see cref="ShareReportModel"/>.
/// </summary>
public static class MarkdownReportExporter
{
    public static string Render(ShareReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var sb = new StringBuilder(1024);
        sb.Append("# GameCrash Kit 游戏闪退急诊单\n\n");
        sb.Append($"> 版本: `{model.AppVersion}` · 规则集: `{model.RulesetVersion}` · {model.PrivacyBadgeText}\n\n");

        sb.Append("## 1. 事故摘要\n");
        sb.Append($"- **事件类型**: {model.IncidentKindLabel}\n");
        sb.Append($"- **应用程序**: `{model.AppDisplayName}`\n");
        sb.Append($"- **故障模块**: `{model.ModuleName}`\n");
        sb.Append($"- **异常代码**: `{model.ExceptionCode}`\n");
        sb.Append($"- **发生时间**: {model.TimestampDisplay}\n");
        sb.Append($"- **安装来源**: `{model.DisambiguatedPathSuffix}`\n");
        sb.Append($"- **窗口频次**: {model.OccurrenceSummary}\n");
        if (!string.IsNullOrWhiteSpace(model.ScanScopeSummary))
        {
            sb.Append($"- **扫描范围**: {model.ScanScopeSummary}\n");
        }
        sb.Append('\n');

        sb.Append("## 2. 诊断结论与下一步建议\n");
        if (model.PrimaryFindings.Count == 0)
        {
            sb.Append("- 暂无诊断结论。\n\n");
        }
        else
        {
            foreach (var f in model.PrimaryFindings)
            {
                sb.Append($"### [{f.RuleTag}] {f.Title}\n");
                sb.Append($"- **可信度刻度**: `{f.AttributionText}` | `{f.EvidenceStrengthText}`\n");
                sb.Append($"- **结论**: {f.Statement}\n");
                if (f.AmbiguityNotes is { Count: > 0 })
                {
                    sb.Append($"- **限制提示**: {string.Join(" ", f.AmbiguityNotes)}\n");
                }
                sb.Append($"- **下一步**: {f.NextActionText}\n\n");
            }
        }

        if (model.AdditionalHints is { Count: > 0 })
        {
            sb.Append("### 补充有限线索（超出主结论 3 条上限）\n");
            foreach (var h in model.AdditionalHints)
            {
                sb.Append($"- **[{h.RuleTag}] {h.Title}**（{h.AttributionText} | {h.EvidenceStrengthText}）：{h.Statement} 建议：{h.NextActionText}\n");
            }
            sb.Append('\n');
        }

        if (model.HasOptInExcerpt && model.OptInScrubbedExcerptLines.Count > 0)
        {
            sb.Append("## 3. 附带受限结构化日志信号摘要（用户预览确认）\n");
            sb.Append("```text\n");
            foreach (var line in model.OptInScrubbedExcerptLines)
            {
                sb.Append(line).Append('\n');
            }
            sb.Append("```\n\n");
        }

        if (model.EnvironmentFacts.Count > 0)
        {
            sb.Append("## 本机环境事实快照（中性参考，非崩溃归因）\n");
            foreach (var env in model.EnvironmentFacts)
            {
                sb.Append($"- **[{env.NoticeLabel}] {env.Label}**: {env.Value}\n");
            }
            sb.Append('\n');
        }

        if (model.BackgroundSummaryLines.Count > 0)
        {
            sb.Append("## 系统背景事件（无进程归属）\n");
            foreach (var bg in model.BackgroundSummaryLines)
            {
                sb.Append($"- {bg}\n");
            }
            sb.Append('\n');
        }

        if (model.SourceLimitationNotes is { Count: > 0 })
        {
            sb.Append("## 采集受限与数据边界说明\n");
            foreach (var note in model.SourceLimitationNotes)
            {
                sb.Append($"- {note}\n");
            }
            sb.Append('\n');
        }

        if (model.CoverageSummaryLines.Count > 0)
        {
            sb.Append("## 采集覆盖范围\n");
            foreach (var cov in model.CoverageSummaryLines)
            {
                sb.Append($"- {cov}\n");
            }
        }

        return sb.ToString();
    }
}
