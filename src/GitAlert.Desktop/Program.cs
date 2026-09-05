using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GitAlert.Graphics;
using GitAlert.Platform;

namespace GitAlert;

internal static class Program
{
    /// <summary>Used by the build to regenerate <c>app.ico</c> from the vector artwork.</summary>
    private const string ExportIconSwitch = "--export-icon";

    [STAThread]
    public static int Main(string[] args)
    {
        if (TryExportIcon(args))
        {
            return 0;
        }

        using var instance = SingleInstance.TryAcquire();

        if (instance is null)
        {
            // Another copy is already in the tray; ask it to show itself and step aside.
            SingleInstance.SignalRunning();
            return 0;
        }

        // Show the window, not the setup prompt: whoever launched GitAlert again wants to see
        // their alerts, and is already past being told to add an account.
        instance.Activated += () => Dispatcher.UIThread.Post(() => (Application.Current as App)?.ShowFlyout());
        instance.Listen();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also what the previewer and the headless render tool start from.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Renders the application icon from the vector artwork and exits, so the icon file and the
    /// on-screen artwork can never drift apart. Only Windows wants an .ico.
    /// </summary>
    private static bool TryExportIcon(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(ExportIconSwitch, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length || !OperatingSystem.IsWindows())
        {
            return false;
        }

        IconFactory.WriteApplicationIcon(args[index + 1], Bell.RenderAppIcon);
        return true;
    }
}
