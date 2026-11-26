using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Privacy;

namespace GameCrashKit.App.Rendering;

public sealed record PngRenderResult(byte[] PngBytes, int PixelWidth, int PixelHeight);

/// <summary>
/// Off-screen fixed-width (900 px) 96-DPI WPF emergency card renderer.
/// Uses explicit Measure/Arrange on a detached visual tree so output is independent of host DPI scaling.
/// </summary>
public static class EmergencyCardPngRenderer
{
    public const int CardWidth = 900;
    public const double TargetDpi = 96.0;

    private static readonly FontFamily CardFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly Brush CanvasBg = FreezeBrush(Color.FromRgb(0xF6, 0xF8, 0xFA));
    private static readonly Brush CardBg = FreezeBrush(Colors.White);
    private static readonly Brush CardBorder = FreezeBrush(Color.FromRgb(0xD0, 0xD7, 0xDE));
    private static readonly Brush HeaderBg = FreezeBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
    private static readonly Brush AccentBlue = FreezeBrush(Color.FromRgb(0x09, 0x69, 0xDA));
    private static readonly Brush TextPrimary = FreezeBrush(Color.FromRgb(0x1F, 0x23, 0x28));
    private static readonly Brush TextSecondary = FreezeBrush(Color.FromRgb(0x57, 0x60, 0x6A));
    private static readonly Brush NextActionBg = FreezeBrush(Color.FromRgb(0xEE, 0xF6, 0xFF));
    private static readonly Brush SuccessBg = FreezeBrush(Color.FromRgb(0xDA, 0xFB, 0xE1));
    private static readonly Brush SuccessFg = FreezeBrush(Color.FromRgb(0x11, 0x63, 0x29));
    private static readonly Brush WarningBg = FreezeBrush(Color.FromRgb(0xFF, 0xF8, 0xC5));
    private static readonly Brush WarningFg = FreezeBrush(Color.FromRgb(0x7D, 0x4E, 0x00));
    private static readonly Brush NeutralBg = FreezeBrush(Color.FromRgb(0xEA, 0xEC, 0xEF));

    public static PngRenderResult RenderToPngBytes(ShareReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var root = BuildVisualTree(model);
        root.Measure(new Size(CardWidth, double.PositiveInfinity));
        var desiredHeight = Math.Max(400, (int)Math.Ceiling(root.DesiredSize.Height));
        root.Arrange(new Rect(0, 0, CardWidth, desiredHeight));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(CardWidth, desiredHeight, TargetDpi, TargetDpi, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return new PngRenderResult(ms.ToArray(), CardWidth, desiredHeight);
    }

    public static BitmapImage CreateFrozenBitmapImage(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        using var ms = new MemoryStream(pngBytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Border BuildVisualTree(ShareReportModel model)
    {
        var mainStack = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        // 1. Header Banner
        mainStack.Children.Add(BuildHeaderBanner(model));

        var bodyStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(24, 18, 24, 22)
        };

        // 2. Incident Summary Card
        bodyStack.Children.Add(BuildIncidentSummaryCard(model));

        // 3. Diagnosis Findings (max 3)
        bodyStack.Children.Add(BuildSectionHeading("二、核心诊断结论与下一步建议"));
        foreach (var finding in model.PrimaryFindings)
        {
            bodyStack.Children.Add(BuildFindingCard(finding));
        }

        // 4. Optional Opt-in Structured Signal Summary
        if (model.AdditionalHints is { Count: > 0 })
        {
            bodyStack.Children.Add(BuildSectionHeading("补充有限线索"));
            bodyStack.Children.Add(BuildAdditionalHintsCard(model.AdditionalHints));
        }

        if (model.HasOptInExcerpt && model.OptInScrubbedExcerptLines.Count > 0)
        {
            bodyStack.Children.Add(BuildSectionHeading("三、附带受限结构化日志信号摘要（用户预览确认）"));
            bodyStack.Children.Add(BuildExcerptCard(model.OptInScrubbedExcerptLines));
        }

        // 5. Compact 7-item Neutral Environment Facts
        if (model.EnvironmentFacts.Count > 0)
        {
            bodyStack.Children.Add(BuildSectionHeading("本机环境事实快照（中性参考，非崩溃归因）"));
            bodyStack.Children.Add(BuildEnvironmentCard(model.EnvironmentFacts));
        }

        // 6. System Background & Coverage Footer
        bodyStack.Children.Add(BuildFooterBlock(model));

        mainStack.Children.Add(bodyStack);

        var border = new Border
        {
            Width = CardWidth,
            Background = CanvasBg,
            Child = mainStack
        };
        TextOptions.SetTextFormattingMode(border, TextFormattingMode.Display);
        return border;
    }

    private static Border BuildHeaderBanner(ShareReportModel model)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        var titleRow = new DockPanel { LastChildFill = false };
        titleRow.Children.Add(new TextBlock
        {
            Text = "GameCrash Kit 游戏闪退急诊单",
            FontFamily = CardFont,
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });

        var verPill = new Border
        {
            Background = AccentBlue,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"v{model.AppVersion} · {model.RulesetVersion}",
                FontFamily = CardFont,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            }
        };
        DockPanel.SetDock(verPill, Dock.Right);
        titleRow.Children.Add(verPill);
        stack.Children.Add(titleRow);

        stack.Children.Add(new TextBlock
        {
            Text = model.PrivacyBadgeText,
            FontFamily = CardFont,
            FontSize = 12.5,
            Foreground = FreezeBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = HeaderBg,
            Padding = new Thickness(24, 18, 24, 16),
            Child = stack
        };
    }

    private static Border BuildIncidentSummaryCard(ShareReportModel model)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "一、事故核心摘要",
            FontFamily = CardFont,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        AddKvLine(left, "事件类型", model.IncidentKindLabel);
        AddKvLine(left, "应用程序", model.AppDisplayName);
        AddKvLine(left, "故障模块", model.ModuleName);
        AddKvLine(left, "异常代码", model.ExceptionCode);

        var right = new StackPanel();
        AddKvLine(right, "发生时间", model.TimestampDisplay);
        AddKvLine(right, "安装来源", model.DisambiguatedPathSuffix);
        AddKvLine(right, "窗口频次", model.OccurrenceSummary);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        stack.Children.Add(grid);

        return CreateCardBorder(stack, new Thickness(0, 0, 0, 14));
    }

    private static void AddKvLine(StackPanel panel, string key, string value)
    {
        var tb = new TextBlock
        {
            FontFamily = CardFont,
            FontSize = 13,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 5)
        };
        tb.Inlines.Add(new System.Windows.Documents.Run($"{key}：")
        {
            Foreground = TextSecondary,
            FontWeight = FontWeights.SemiBold
        });
        tb.Inlines.Add(new System.Windows.Documents.Run(value));
        panel.Children.Add(tb);
    }

    private static Border BuildFindingCard(ShareFindingItem finding)
    {
        var stack = new StackPanel();

        var top = new DockPanel { LastChildFill = false };
        var titleBlock = new TextBlock
        {
            Text = finding.Title,
            FontFamily = CardFont,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 650
        };
        top.Children.Add(titleBlock);

        var ruleTagBlock = new TextBlock
        {
            Text = finding.RuleTag,
            FontFamily = CardFont,
            FontSize = 11.5,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(ruleTagBlock, Dock.Right);
        top.Children.Add(ruleTagBlock);
        stack.Children.Add(top);

        var badgeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 8)
        };
        badgeRow.Children.Add(CreatePill(finding.AttributionText, NeutralBg, TextPrimary));

        var (strBg, strFg) = finding.EvidenceStrength switch
        {
            EvidenceStrength.ExplicitRecord => (SuccessBg, SuccessFg),
            EvidenceStrength.LimitedClue => (WarningBg, WarningFg),
            _ => (NeutralBg, TextPrimary)
        };
        badgeRow.Children.Add(CreatePill(finding.EvidenceStrengthText, strBg, strFg));
        stack.Children.Add(badgeRow);

        stack.Children.Add(new TextBlock
        {
            Text = finding.Statement,
            FontFamily = CardFont,
            FontSize = 13.5,
            Foreground = TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });

        if (finding.AmbiguityNotes is { Count: > 0 })
        {
            stack.Children.Add(new TextBlock
            {
                Text = "限制提示：" + string.Join(" ", finding.AmbiguityNotes),
                FontFamily = CardFont,
                FontSize = 12,
                Foreground = WarningFg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }

        stack.Children.Add(new Border
        {
            Background = NextActionBg,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 9, 0, 0),
            Child = new TextBlock
            {
                Text = $"建议下一步：{finding.NextActionText}",
                FontFamily = CardFont,
                FontSize = 13,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            }
        });

        return CreateCardBorder(stack, new Thickness(0, 0, 0, 10));
    }

    private static Border BuildAdditionalHintsCard(IReadOnlyList<ShareFindingItem> hints)
    {
        var stack = new StackPanel();
        foreach (var h in hints)
        {
            var tb = new TextBlock
            {
                FontFamily = CardFont,
                FontSize = 12.5,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            };
            tb.Inlines.Add(new System.Windows.Documents.Run($"[{h.RuleTag}] {h.Title}（{h.EvidenceStrengthText}）：")
            {
                FontWeight = FontWeights.SemiBold
            });
            tb.Inlines.Add(new System.Windows.Documents.Run($"{h.Statement} 建议：{h.NextActionText}"));
            stack.Children.Add(tb);
        }

        return CreateCardBorder(stack, new Thickness(0, 0, 0, 14));
    }

    private static Border BuildExcerptCard(IReadOnlyList<string> lines)
    {
        var stack = new StackPanel();
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 3)
            });
        }

        return CreateCardBorder(stack, new Thickness(0, 0, 0, 14));
    }

    private static Border BuildEnvironmentCard(IReadOnlyList<ShareEnvironmentItem> facts)
    {
        var stack = new StackPanel();
        foreach (var fact in facts)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var (bg, fg) = fact.NoticeLevel switch
            {
                EnvironmentNoticeLevel.Attention => (WarningBg, WarningFg),
                _ => (NeutralBg, TextPrimary)
            };

            var pill = CreatePill(fact.NoticeLabel, bg, fg);
            pill.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(pill, Dock.Left);
            row.Children.Add(pill);

            var tb = new TextBlock
            {
                FontFamily = CardFont,
                FontSize = 12.5,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.Inlines.Add(new System.Windows.Documents.Run($"{fact.Label}：") { FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run(fact.Value));
            row.Children.Add(tb);

            stack.Children.Add(row);
        }

        return CreateCardBorder(stack, new Thickness(0, 0, 0, 14));
    }

    private static Border BuildFooterBlock(ShareReportModel model)
    {
        var stack = new StackPanel();

        if (model.BackgroundSummaryLines.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "系统背景（无进程归属）：" + string.Join(" ｜ ", model.BackgroundSummaryLines),
                FontFamily = CardFont,
                FontSize = 11.5,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        if (model.CoverageSummaryLines.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "数据源覆盖：" + string.Join(" ｜ ", model.CoverageSummaryLines),
                FontFamily = CardFont,
                FontSize = 11.5,
                Foreground = TextSecondary,
                TextWrapping = TextWrapping.Wrap
            });
        }

        return new Border
        {
            Padding = new Thickness(4, 4, 4, 0),
            Child = stack
        };
    }

    private static TextBlock BuildSectionHeading(string text) => new()
    {
        Text = text,
        FontFamily = CardFont,
        FontSize = 15,
        FontWeight = FontWeights.Bold,
        Foreground = TextPrimary,
        Margin = new Thickness(0, 2, 0, 8)
    };

    private static Border CreateCardBorder(UIElement child, Thickness margin) => new()
    {
        Background = CardBg,
        BorderBrush = CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 13, 16, 13),
        Margin = margin,
        Child = child
    };

    private static Border CreatePill(string text, Brush bg, Brush fg) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 8, 0),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = CardFont,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg
        }
    };

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
