using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using GitAlert.Configuration;

namespace GitAlert.Services;

/// <summary>
/// Swaps the palette dictionary at the front of the application's merged resources. Every control
/// style refers to those keys with <c>DynamicResource</c>, so a swap re-themes the whole app
/// without recreating a single window. The Fluent base theme is switched between its light and
/// dark variants alongside, so the pieces it still draws - tooltips, scrollbars - agree.
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri Base = new("avares://GitAlert/");
    private static readonly Uri VsCodeDark = new("avares://GitAlert/Themes/Dark.axaml");
    private static readonly Uri GitHubDark = new("avares://GitAlert/Themes/DarkGitHub.axaml");
    private static readonly Uri Light = new("avares://GitAlert/Themes/Light.axaml");

    private readonly Application _app;
    private readonly IPlatformSettings? _system;

    private AppTheme _mode = AppTheme.System;
    private DarkPalette _palette = DarkPalette.VsCode;

    public ThemeService(Application app)
    {
        _app = app;
        _system = app.PlatformSettings;

        if (_system is not null)
        {
            _system.ColorValuesChanged += (_, _) => Reapply();
        }
    }

    /// <summary>True when the app is currently painting itself dark.</summary>
    public bool IsDark { get; private set; } = true;

    public event EventHandler? Applied;

    /// <summary>
    /// Light has one look; dark has a choice, which only matters when dark is what comes out -
    /// whether because it was asked for or because the system is dark.
    /// </summary>
    public void Apply(AppTheme mode, DarkPalette palette = DarkPalette.VsCode)
    {
        _mode = mode;
        _palette = palette;
        Reapply();
    }

    /// <summary>Re-evaluates the system theme; called when the OS reports a preference change.</summary>
    public void Reapply()
    {
        var dark = _mode switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => _system?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light,
        };

        var source = !dark ? Light : _palette == DarkPalette.GitHub ? GitHubDark : VsCodeDark;
        var palette = new ResourceInclude(Base) { Source = source };
        var merged = _app.Resources.MergedDictionaries;

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

        _app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        IsDark = dark;
        Applied?.Invoke(this, EventArgs.Empty);
    }
}
