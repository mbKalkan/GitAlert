using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.Platform.Linux;
using GitAlert.Platform.MacOS;
using Xunit;

namespace GitAlert.Desktop.Tests;

/// <summary>
/// The parts of the macOS and Linux platform layers that are pure: where a flyout lands, what the
/// login entries say, how a notification script is quoted, and what the native tray menu holds.
/// The tools they drive - security, secret-tool, osascript, notify-send - are exercised on their
/// own platforms.
/// </summary>
public class PlatformPiecesTests
{
    private static readonly PixelRect Work = new(0, 25, 1920, 1055);

    [Fact]
    public void The_flyout_lands_in_the_corner_of_the_work_area_nearest_the_anchor()
    {
        var size = new PixelSize(1020, 660);

        // Bottom right, the way a Windows or KDE panel has it.
        Assert.Equal(new PixelPoint(1920 - 1020 - 12, 25 + 1055 - 660 - 12), FlyoutPlacement.Beside(new ScreenPoint(1900, 1070), size, Work));

        // Top left, for a panel up there and an icon at that end.
        Assert.Equal(new PixelPoint(12, 25 + 12), FlyoutPlacement.Beside(new ScreenPoint(30, 30), size, Work));

        // The menu bar's corner, whatever the anchor says, when the platform keeps its items up top.
        Assert.Equal(new PixelPoint(1920 - 1020 - 12, 25 + 12), FlyoutPlacement.Beside(new ScreenPoint(1900, 1070), size, Work, alwaysTop: true));
    }

    [Fact]
    public void A_flyout_wider_than_the_work_area_still_starts_inside_it()
    {
        var placed = FlyoutPlacement.Beside(new ScreenPoint(1900, 1070), new PixelSize(2400, 400), Work);

        Assert.Equal(12, placed.X);
    }

    [Fact]
    public void Without_a_click_position_the_corner_follows_the_panel()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);

        // A panel along the top pushes the work area down; that is where the icon is.
        Assert.Equal(new ScreenPoint(1919, 25), FlyoutPlacement.CornerOf(new PixelRect(0, 25, 1920, 1055), bounds));

        // A panel along the bottom leaves the top alone.
        Assert.Equal(new ScreenPoint(1919, 1039), FlyoutPlacement.CornerOf(new PixelRect(0, 0, 1920, 1040), bounds));
    }

    [Fact]
    public void The_launch_agent_runs_the_executable_at_load_with_the_startup_switch()
    {
        var plist = LaunchAgent.Plist("/Applications/Git & Alert.app/Contents/MacOS/GitAlert");

        Assert.Contains("<string>com.mbkalkan.gitalert</string>", plist);
        Assert.Contains("<string>/Applications/Git &amp; Alert.app/Contents/MacOS/GitAlert</string>", plist);
        Assert.Contains("<string>--startup</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("<true/>", plist);
    }

    [Fact]
    public void The_autostart_entry_quotes_the_executable_and_passes_the_startup_switch()
    {
        var entry = XdgAutostart.DesktopEntry("/home/mert/Apps/Git\"Alert-$1.AppImage");

        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Exec=\"/home/mert/Apps/Git\\\"Alert-\\$1.AppImage\" --startup", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
        Assert.Contains("Terminal=false", entry);
    }

    [Fact]
    public void The_notification_script_quotes_what_the_alert_says()
    {
        var script = MacNotifier.Script("CI failed (#212)", "He said \"done\" \\ and left", playSound: true);

        Assert.Equal(
            "display notification \"He said \\\"done\\\" \\\\ and left\" with title \"CI failed (#212)\" sound name \"default\"",
            script);

        Assert.DoesNotContain("sound name", MacNotifier.Script("t", "m", playSound: false));
    }

    [Fact]
    public void The_badge_says_what_the_windows_icon_says()
    {
        Assert.Null(AvaloniaTrayHost.BadgeFor(TrayState.Idle, hasUnread: false));
        Assert.Equal(new Rgb(0x3F, 0xB9, 0x50), AvaloniaTrayHost.BadgeFor(TrayState.Idle, hasUnread: true));
        Assert.Equal(new Rgb(0xD2, 0x99, 0x22), AvaloniaTrayHost.BadgeFor(TrayState.Warning, hasUnread: true));
        Assert.Equal(new Rgb(0xF8, 0x51, 0x49), AvaloniaTrayHost.BadgeFor(TrayState.Error, hasUnread: false));
    }

    [AvaloniaFact]
    public void The_native_menu_has_one_item_per_entry_and_a_rule_per_separator_and_the_items_fire()
    {
        var chosen = new List<string>();

        var menu = AvaloniaTrayHost.BuildMenu(
        [
            new TrayMenuEntry("Open GitAlert", () => chosen.Add("open"), IsDefault: true),
            TrayMenuEntry.Separator,
            new TrayMenuEntry("Quit", () => chosen.Add("quit")),
        ]);

        Assert.Equal(3, menu.Items.Count);
        Assert.IsType<NativeMenuItemSeparator>(menu.Items[1]);

        var quit = Assert.IsType<NativeMenuItem>(menu.Items[2]);
        Assert.Equal("Quit", quit.Header);

        quit.Command!.Execute(null);

        Assert.Equal(["quit"], chosen);
    }

    [AvaloniaFact]
    public void The_tray_bitmap_carries_the_rendered_pixels()
    {
        var pixels = GitAlert.Graphics.Bell.RenderTrayIcon(32, AvaloniaTrayHost.ForegroundFor(dark: true), badge: null);

        var bitmap = AvaloniaTrayHost.ToBitmap(pixels, 32);

        Assert.Equal(new PixelSize(32, 32), bitmap.PixelSize);
    }
}
