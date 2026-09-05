using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Graphics;

namespace GitAlert.Platform;

/// <summary>Windows, through the shared <c>GitAlert.Windows</c> layer the WPF app also runs on.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public ITrayHost CreateTray() => new TrayIcon(Bell.RenderTrayIcon);

    public ISecretStore CreateSecretStore() => new SecureTokenStore(new DpapiTokenProtector());

    public IStartupRegistrar Startup { get; } = new StartupManager();

    public bool IsSystemDark => SystemTheme.IsSystemDark;

    public PixelPoint? PlaceFlyout(ScreenPoint anchor, PixelSize size)
    {
        // Avalonia positions windows in physical pixels, so no scale is applied here.
        var (left, top) = FlyoutPositioner.Place(anchor, size.Width, size.Height, 1, 1);
        return new PixelPoint((int)left, (int)top);
    }

    public bool TakeForeground(Window window) => HandleOf(window) is { } handle && NativeMethods.ForceForeground(handle);

    public bool IsForeground(Window window) => HandleOf(window) is { } handle && NativeMethods.IsForeground(handle);

    public void ApplyTitleBarTheme(Window window, bool dark)
    {
        if (HandleOf(window) is { } handle)
        {
            NativeMethods.SetTitleBarTheme(handle, dark);
        }
    }

    public string StartupProblem => "Could not change the Windows startup entry.";

    private static IntPtr? HandleOf(Window window) => window.TryGetPlatformHandle()?.Handle;
}
