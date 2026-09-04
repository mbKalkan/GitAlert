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
    private static readonly Uri VsCodeDark = new("/Themes/Dark.xaml", UriKind.Relative);
    private static readonly Uri GitHubDark = new("/Themes/DarkGitHub.xaml", UriKind.Relative);
    private static readonly Uri Light = new("/Themes/Light.xaml", UriKind.Relative);

    private static AppTheme _mode = AppTheme.System;
    private static DarkPalette _palette = DarkPalette.VsCode;

    /// <summary>True when the app is currently painting itself dark.</summary>
    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? Applied;

    /// <summary>
    /// Light has one look; dark has a choice, which only matters when dark is what comes out -
    /// whether because it was asked for or because Windows is dark.
    /// </summary>
    public static void Apply(AppTheme mode, DarkPalette palette = DarkPalette.VsCode)
    {
        _mode = mode;
        _palette = palette;
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

        var source = !dark ? Light : _palette == DarkPalette.GitHub ? GitHubDark : VsCodeDark;
        var palette = new ResourceDictionary { Source = source };
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
