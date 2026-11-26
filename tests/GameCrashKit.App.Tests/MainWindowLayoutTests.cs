using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameCrashKit.App.ViewModels;
using GameCrashKit.App.Views;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Rules;
using Xunit;

namespace GameCrashKit.App.Tests;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(860, 600)]
    [InlineData(1240, 800)]
    public void MainWindow_MeasureAndArrange_HasNoClippedButtonsAndAlignedSectionBadges(double width, double height)
    {
        Exception? staEx = null;
        var buttonCount = 0;
        var maxButtonRight = 0.0;
        var sectionBadgeXs = new List<double>();

        RunOnStaThread(() =>
        {
            try
            {
                EnsureAppResourcesLoaded();

                var window = new MainWindow();
                var rootContent = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                rootContent.Measure(new Size(width - 16, height - 38));
                rootContent.Arrange(new Rect(0, 0, width - 16, height - 38));
                rootContent.UpdateLayout();

                // 1. Verify no visible Button overflows the window content width
                foreach (var btn in FindVisualChildren<Button>(rootContent))
                {
                    if (btn.Visibility != Visibility.Visible || btn.ActualWidth <= 0)
                    {
                        continue;
                    }

                    buttonCount++;
                    var topLeft = btn.TransformToAncestor(rootContent).Transform(new Point(0, 0));
                    var right = topLeft.X + btn.ActualWidth;
                    if (right > maxButtonRight)
                    {
                        maxButtonRight = right;
                    }

                    Assert.True(
                        right <= rootContent.ActualWidth + 1.0,
                        $"Button '{btn.Content}' clipped at width {width}: right={right:F1} > contentWidth={rootContent.ActualWidth:F1}");
                }

                // 2. Verify Section 1, 2 (Expander), and 3 numbered badges align on exact same X coordinate
                foreach (var tb in FindVisualChildren<TextBlock>(rootContent))
                {
                    if (tb.Text is "1" or "2" or "3" && tb.Parent is Border badge && badge.ActualWidth is >= 20 and <= 24)
                    {
                        var pos = badge.TransformToAncestor(rootContent).Transform(new Point(0, 0));
                        sectionBadgeXs.Add(pos.X);
                    }
                }

                if (window.DataContext is IDisposable disp)
                {
                    disp.Dispose();
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        Assert.Null(staEx);
        Assert.True(buttonCount >= 4);
        Assert.Equal(3, sectionBadgeXs.Count);
        Assert.True(Math.Abs(sectionBadgeXs[0] - sectionBadgeXs[1]) < 1.0, $"Section 1 ({sectionBadgeXs[0]}) and 2 ({sectionBadgeXs[1]}) misaligned");
        Assert.True(Math.Abs(sectionBadgeXs[0] - sectionBadgeXs[2]) < 1.0, $"Section 1 ({sectionBadgeXs[0]}) and 3 ({sectionBadgeXs[2]}) misaligned");
    }

    [Fact]
    public void SharePreviewWindow_MeasureAndArrangeAtMinWidth_FitsAllControlsCleanly()
    {
        Exception? staEx = null;

        RunOnStaThread(() =>
        {
            try
            {
                EnsureAppResourcesLoaded();

                var now = DateTimeOffset.UtcNow;
                var req = new ScanRequest(now.AddHours(-2), now);
                var report = new DiagnosisRuleEngine().Evaluate(new RuleEvaluationContext(req, null, [], [], null), []);
                var vm = new SharePreviewViewModel(report);
                var previewWin = new SharePreviewWindow(vm);

                var root = Assert.IsAssignableFrom<FrameworkElement>(previewWin.Content);
                root.Measure(new Size(800, 560));
                root.Arrange(new Rect(0, 0, 800, 560));
                root.UpdateLayout();

                foreach (var btn in FindVisualChildren<Button>(root))
                {
                    if (btn.Visibility != Visibility.Visible || btn.ActualWidth <= 0)
                    {
                        continue;
                    }

                    var right = btn.TransformToAncestor(root).Transform(new Point(0, 0)).X + btn.ActualWidth;
                    Assert.True(right <= root.ActualWidth + 1.0, $"SharePreview button '{btn.Content}' clipped: right={right:F1}");
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        Assert.Null(staEx);
    }

    private static void EnsureAppResourcesLoaded()
    {
        if (Application.Current is null)
        {
            _ = new Application();
        }

        if (!Application.Current!.Resources.Contains("BoolToVis"))
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/GameCrashKit.App;component/Themes/FluentLightTheme.xaml")
            });
            Application.Current.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        }
    }

    private static void RunOnStaThread(Action action)
    {
        var thread = new Thread(new ThreadStart(action));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
