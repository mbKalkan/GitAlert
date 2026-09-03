using System.Windows;
using GitAlert.Configuration;
using GitAlert.Platform;

namespace GitAlert.Services;

/// <summary>
/// Swaps the palette dictionary at the front of the application's merged resources. Every control
/// style refers to those keys with <c>DynamicResource</c>, so a swap re-themes the whole app
/// without recreating a single window.
/// </summary>
public static class ThemeService
{
    private static readonly Uri DarkPalette = new("/Themes/Dark.xaml", UriKind.Relative);
    private static readonly Uri LightPalette = new("/Themes/Light.xaml", UriKind.Relative);

    private static AppTheme _mode = AppTheme.System;

    /// <summary>True when the app is currently painting itself dark.</summary>
    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? Applied;

    public static void Apply(AppTheme mode)
    {
        _mode = mode;
        Reapply();
    }

    /// <summary>Re-evaluates the system theme; called when Windows reports a preference change.</summary>
    public static void Reapply()
    {
        var dark = _mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemTheme.IsAppDark,
        };

        var application = Application.Current;
        if (application is null)
        {
            IsDark = dark;
            return;
        }

        var palette = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };
        var merged = application.Resources.MergedDictionaries;

        // The palette always sits first so the control styles that follow can override nothing
        // and simply resolve against it.
        if (merged.Count == 0)
        {
            merged.Add(palette);
        }
        else
        {
            merged[0] = palette;
        }

        IsDark = dark;
        Applied?.Invoke(null, EventArgs.Empty);
    }
}
