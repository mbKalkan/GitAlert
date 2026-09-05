using Avalonia;
using Avalonia.Controls;
using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.Platform;

/// <summary>
/// What differs per operating system, gathered in one place so the windows and the shell never ask
/// which one they are on. Windows answers today; macOS and Linux arrive with their own answers.
/// </summary>
public interface IPlatform
{
    ITrayHost CreateTray();

    ISecretStore CreateSecretStore();

    IStartupRegistrar Startup { get; }

    /// <summary>True when the bar that hosts the tray icon is dark, so the glyph must be light.</summary>
    bool IsSystemDark { get; }

    /// <summary>
    /// Where a flyout of the given size belongs beside the tray anchor, in physical pixels, or null
    /// when the platform has no better idea than the window's own.
    /// </summary>
    PixelPoint? PlaceFlyout(ScreenPoint anchor, PixelSize size);

    /// <summary>
    /// Insists on the foreground for a window the shell has just activated. Returns true when the
    /// window holds it; only Windows ever refuses, and only Windows has a way around the refusal.
    /// </summary>
    bool TakeForeground(Window window);

    bool IsForeground(Window window);

    /// <summary>Asks the system to paint the window's own title bar, where it draws one, to match the theme.</summary>
    void ApplyTitleBarTheme(Window window, bool dark);

    /// <summary>What to tell the user when <see cref="Startup"/> refuses.</summary>
    string StartupProblem { get; }
}

public static class Platforms
{
    /// <summary>The platform this process runs on, or a clear refusal.</summary>
    public static IPlatform Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatform();
        }

        throw new PlatformNotSupportedException("GitAlert runs on Windows for now; macOS and Linux arrive with phase 3.");
    }
}
