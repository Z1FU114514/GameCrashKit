using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace GameCrashKit.App.Tests;

[CollectionDefinition("WpfApp")]
public sealed class WpfAppCollectionDefinition
{
}

internal static class WpfTestHost
{
    private static readonly Lock SyncRoot = new();

    public static void EnsureResourcesLoaded()
    {
        lock (SyncRoot)
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }

            var resources = Application.Current!.Resources;
            if (!resources.Contains("BoolToVis"))
            {
                resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/GameCrashKit.App;component/Themes/CyberDarkTheme.xaml")
                });
                resources["BoolToVis"] = new BooleanToVisibilityConverter();
            }
        }
    }
}
