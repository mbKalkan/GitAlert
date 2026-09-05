using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.Platform.MacOS;

/// <summary>
/// macOS: a status item in the menu bar, tokens in the login keychain, a launch agent for login,
/// notifications through AppleScript. Windows are activated the ordinary way; nothing fights for
/// the foreground here, and the system draws no title bar of ours to theme.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatform : IPlatform
{
    public ITrayHost CreateTray() => new AvaloniaTrayHost(() => IsSystemDark, MacNotifier.Show);

    public ISecretStore CreateSecretStore() => new KeychainSecretStore();

    public IStartupRegistrar Startup { get; } = new LaunchAgentRegistrar();

    public bool IsSystemDark =>
        Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    /// <summary>
    /// Under the menu bar, at the right, whatever anchor was handed in: that is where the status
    /// item lives, and the platform does not say where in the bar it was clicked.
    /// </summary>
    public PixelPoint? PlaceFlyout(Window window, ScreenPoint anchor, PixelSize size)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();

        return screen is null
            ? null
            : FlyoutPlacement.Beside(new ScreenPoint(screen.WorkingArea.Right - 1, screen.WorkingArea.Y), size, screen.WorkingArea, alwaysTop: true);
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

    public string StartupProblem => "Could not write the login item under ~/Library/LaunchAgents.";
}
