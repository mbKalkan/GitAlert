using System.Windows.Media;
using Microsoft.Win32;

namespace GitAlert.Platform;

/// <summary>
/// Reads the two Windows appearance settings GitAlert cares about: the app theme, which drives the
/// flyout palette, and the system (taskbar) theme, which decides whether the tray icon should be
/// light or dark to stay legible.
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when apps use the dark palette.</summary>
    public static bool IsAppDark => ReadFlag("AppsUseLightTheme", defaultLight: true) is false;

    /// <summary>True when the taskbar and tray use the dark palette.</summary>
    public static bool IsSystemDark => ReadFlag("SystemUsesLightTheme", defaultLight: false) is false;

    /// <summary>
    /// The colour a tray icon should be drawn in: near-white on a dark taskbar, near-black on a
    /// light one.
    /// </summary>
    public static Color TrayForeground =>
        IsSystemDark
            ? Color.FromRgb(0xF2, 0xF4, 0xF8)
            : Color.FromRgb(0x1B, 0x1F, 0x26);

    /// <summary>
    /// Raised when the user changes their Windows theme. Registry change notifications are more
    /// trouble than they are worth here; the app subscribes to WPF's system-preference event and
    /// calls <see cref="Raise"/>.
    /// </summary>
    public static event EventHandler? Changed;

    public static void Raise() => Changed?.Invoke(null, EventArgs.Empty);

    private static bool? ReadFlag(string valueName, bool defaultLight)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue(valueName);

            return value is int flag ? flag != 0 : defaultLight;
        }
        catch (Exception)
        {
            // Group policy or a locked-down profile can hide the key; assume the default.
            return defaultLight;
        }
    }
}
