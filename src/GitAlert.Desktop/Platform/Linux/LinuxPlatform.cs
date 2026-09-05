using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.Platform.Linux;

/// <summary>
/// Linux: a StatusNotifierItem in the panel, tokens in the desktop's secret service, an autostart
/// entry for login, notifications over <c>notify-send</c>. Windows are activated the ordinary way;
/// the window manager draws no title bar of ours to theme.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatform : IPlatform
{
    public ITrayHost CreateTray() => new AvaloniaTrayHost(() => IsSystemDark, LinuxNotifier.Show);

    public ISecretStore CreateSecretStore() => LinuxSecretStores.Create();

    public IStartupRegistrar Startup { get; } = new XdgAutostartRegistrar();

    public bool IsSystemDark =>
        Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    /// <summary>
    /// Beside the panel: at the anchor's end of the work area when the anchor is a real point, and
    /// in the corner the panel occupies when the status item could not say where it was clicked.
    /// </summary>
    public PixelPoint? PlaceFlyout(Window window, ScreenPoint anchor, PixelSize size)
    {
        var point = new PixelPoint(anchor.X, anchor.Y);
        var screen = window.Screens.ScreenFromPoint(point) ?? window.Screens.Primary ?? window.Screens.All.FirstOrDefault();

        if (screen is null)
        {
            return null;
        }

        var work = screen.WorkingArea;

        if (!work.Contains(point))
        {
            anchor = FlyoutPlacement.CornerOf(work, screen.Bounds);
        }

        return FlyoutPlacement.Beside(anchor, size, work);
    }

    public bool TakeForeground(Window window)
    {
        window.Activate();
        return true;
    }

    public bool IsForeground(Window window) => window.IsActive;

    public void ApplyTitleBarTheme(Window window, bool dark)
    {
    }

    public void RoundCorners(Window window)
    {
    }

    public string StartupProblem => "Could not write the autostart entry under ~/.config/autostart.";
}

/// <summary>Notifications through <c>notify-send</c>, which every desktop with a notification daemon answers.</summary>
public static class LinuxNotifier
{
    [SupportedOSPlatform("linux")]
    public static void Show(string title, string message, NotificationKind kind, bool playSound)
    {
        var urgency = kind switch
        {
            NotificationKind.Error => "critical",
            NotificationKind.Warning => "normal",
            _ => "low",
        };

        // The daemon decides about sound; there is no switch for it here.
        var (code, _) = Tool.Run(
            "notify-send",
            ["--app-name=GitAlert", "--icon=gitalert", $"--urgency={urgency}", "--hint=string:desktop-entry:gitalert", title, message],
            timeout: TimeSpan.FromSeconds(5));

        if (code != 0)
        {
            TraceLog.Write($"notification not shown: notify-send exited with {code}");
        }
    }
}
