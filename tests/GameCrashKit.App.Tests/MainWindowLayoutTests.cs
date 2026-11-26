using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameCrashKit.App.ViewModels;
using GameCrashKit.App.Views;
using GameCrashKit.Core.Correlation;
using GameCrashKit.Core.Models;
using GameCrashKit.Core.Rules;
using GameCrashKit.Windows.Scanning;
using Xunit;

namespace GameCrashKit.App.Tests;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(860, 480)]
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
                var bgBrush = Assert.IsAssignableFrom<SolidColorBrush>(window.Background);
                Assert.Equal(Color.FromRgb(0x0B, 0x0E, 0x14), bgBrush.Color);
                Assert.NotNull(window.Icon);
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
                var previewBg = Assert.IsAssignableFrom<SolidColorBrush>(previewWin.Background);
                Assert.Equal(Color.FromRgb(0x0B, 0x0E, 0x14), previewBg.Color);
                Assert.NotNull(previewWin.Icon);

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

    [Fact]
    public void SharePreviewWindow_MeasureAndArrangeAtLowHeightWorkArea_KeepsCloseButtonVisible()
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
                root.Measure(new Size(940, 460));
                root.Arrange(new Rect(0, 0, 940, 460));
                root.UpdateLayout();

                foreach (var btn in FindVisualChildren<Button>(root))
                {
                    if (btn.Visibility != Visibility.Visible || btn.ActualWidth <= 0)
                    {
                        continue;
                    }

                    var topLeft = btn.TransformToAncestor(root).Transform(new Point(0, 0));
                    Assert.True(topLeft.Y + btn.ActualHeight <= root.ActualHeight + 1.0, $"SharePreview button '{btn.Content}' bottom clipped at height 460");
                }
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });

        Assert.Null(staEx);
    }

    [Fact]
    public async Task MainViewModel_SwitchingCandidate_IsolatesAttachedEngineLogPerIncident()
    {
        var tempDir = Directory.CreateTempSubdirectory("gck_vm_iso_");
        try
        {
            var logPath = Path.Combine(tempDir.FullName, "Player.log");
            File.WriteAllText(
                logPath,
                "Initialize engine version: 2022.3.20f1c1\nD3D11: DXGI_ERROR_DEVICE_REMOVED\nCrash!!!\n");

            var now = DateTimeOffset.UtcNow;
            var req = new ScanRequest(now.AddHours(-2), now);
            var evA = new EvidenceItem("ev-a", EvidenceSourceKind.WindowsApplicationCrash1000, now.AddMinutes(-2), "EventSystemTimeUtc", "App/1000/1", "1.0.0", new Dictionary<string, string>(), false);
            var evB = new EvidenceItem("ev-b", EvidenceSourceKind.WindowsApplicationCrash1000, now.AddMinutes(-5), "EventSystemTimeUtc", "App/1000/2", "1.0.0", new Dictionary<string, string>(), false);

            var incA = new CrashIncident("inc-A", IncidentAnchorKind.ApplicationCrash, now.AddMinutes(-2), "GameA.exe", @"D:\Games\GameA\GameA.exe", @"...\GameA\GameA.exe", "GameA.exe", null, "0xc0000005", "0x1", "0x1", null, null, true, false, true, 1, [now.AddMinutes(-2)], AttributionStrength.Unlinked, evA, null, [], null, []);
            var incB = new CrashIncident("inc-B", IncidentAnchorKind.ApplicationCrash, now.AddMinutes(-5), "GameB.exe", @"D:\Games\GameB\GameB.exe", @"...\GameB\GameB.exe", "GameB.exe", null, "0xc0000005", "0x2", "0x2", null, null, true, false, true, 1, [now.AddMinutes(-5)], AttributionStrength.Unlinked, evB, null, [], null, []);

            var session = new ScanSessionResult(false, false, false, req, new CorrelateResult([incA, incB], 0, [], [evA, evB]), [], null, []);
            using var vm = new MainViewModel();
            vm.LoadSessionForTest(session);

            Assert.Equal("GameA.exe", vm.SelectedCandidate!.DisplayTitle);
            await vm.AttachManualLogFileAsync(logPath);
            Assert.True(vm.HasAttachedEngineLog);
            Assert.Contains(vm.PrimaryFindings, static f => f.RuleTag.StartsWith("ENGINE-GPU-LOST", StringComparison.Ordinal));

            vm.SelectedCandidate = vm.Candidates[1];
            Assert.Equal("GameB.exe", vm.SelectedCandidate.DisplayTitle);
            Assert.False(vm.HasAttachedEngineLog);
            Assert.Null(vm.ActiveAttachedEngineLogFullPath);
            Assert.DoesNotContain(vm.PrimaryFindings, static f => f.RuleTag.StartsWith("ENGINE-GPU-LOST", StringComparison.Ordinal));

            vm.SelectedCandidate = vm.Candidates[0];
            Assert.True(vm.HasAttachedEngineLog);
            Assert.Contains(vm.PrimaryFindings, static f => f.RuleTag.StartsWith("ENGINE-GPU-LOST", StringComparison.Ordinal));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void SharePreviewViewModel_SaveOverAttachedSourceLog_IsRejected()
    {
        Exception? staEx = null;
        var tempDir = Directory.CreateTempSubdirectory("gck_overwrite_guard_");
        try
        {
            var sourceLog = Path.Combine(tempDir.FullName, "Player.txt");
            File.WriteAllText(sourceLog, "original evidence log");

            RunOnStaThread(() =>
            {
                try
                {
                    EnsureAppResourcesLoaded();
                    var now = DateTimeOffset.UtcNow;
                    var req = new ScanRequest(now.AddHours(-2), now);
                    var report = new DiagnosisRuleEngine().Evaluate(new RuleEvaluationContext(req, null, [], [], null), []);
                    var vm = new SharePreviewViewModel(report, attachedSourceLogFullPath: sourceLog);

                    Assert.False(vm.SaveMarkdownToPath(sourceLog, out var err));
                    Assert.Contains("不能覆盖", err);
                }
                catch (Exception ex)
                {
                    staEx = ex;
                }
            });

            Assert.Null(staEx);
            Assert.Equal("original evidence log", File.ReadAllText(sourceLog));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
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
                Source = new Uri("pack://application:,,,/GameCrashKit.App;component/Themes/CyberDarkTheme.xaml")
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
