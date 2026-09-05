using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.Views;
using Xunit;

namespace GitAlert.UI.Tests;

/// <summary>
/// The menu behind a right click on the tray icon: its entries, the click that chooses one, and the
/// keys that walk it and put it away. The foreground fight with the shell cannot be staged here.
/// </summary>
public class TrayMenuTests
{
    [AvaloniaFact]
    public void The_menu_shows_its_entries_and_runs_the_one_clicked()
    {
        var chosen = new List<string>();
        using var menu = Build(chosen);

        menu.ShowAt(new ScreenPoint(400, 300));
        Frames.Settle();

        var buttons = Buttons(menu);

        Assert.True(menu.IsVisible);
        Assert.Equal(["Open GitAlert", "Check now", "Quit"], buttons.Select(b => b.Content as string));
        Assert.Single(menu.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("trayRule"));

        var at = Centre(buttons[1], menu);
        menu.MouseDown(at, MouseButton.Left);
        menu.MouseUp(at, MouseButton.Left);
        Frames.Settle();

        Assert.Equal(["check"], chosen);
        Assert.False(menu.IsVisible, "choosing an entry puts the menu away");
    }

    [AvaloniaFact]
    public void The_arrow_keys_walk_the_entries_and_enter_chooses()
    {
        var chosen = new List<string>();
        using var menu = Build(chosen);

        menu.ShowAt(new ScreenPoint(400, 300));
        Frames.Settle();

        var buttons = Buttons(menu);

        Assert.True(buttons[0].IsFocused, "the first entry starts highlighted");

        menu.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Frames.Settle();

        Assert.True(buttons[1].IsFocused);

        menu.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        menu.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Frames.Settle();

        Assert.True(buttons[0].IsFocused, "the highlight wraps around, skipping the rule");

        menu.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Frames.Settle();

        Assert.True(buttons[2].IsFocused);

        menu.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        menu.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Frames.Settle();

        Assert.Equal(["quit"], chosen);
        Assert.False(menu.IsVisible);
    }

    [AvaloniaFact]
    public void Escape_puts_the_menu_away_without_choosing_and_it_can_open_again()
    {
        var chosen = new List<string>();
        using var menu = Build(chosen);

        menu.ShowAt(new ScreenPoint(400, 300));
        Frames.Settle();

        menu.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Frames.Settle();

        Assert.False(menu.IsVisible);
        Assert.Empty(chosen);

        menu.ShowAt(new ScreenPoint(120, 80));
        Frames.Settle();

        Assert.True(menu.IsVisible);
        Assert.Equal(3, Buttons(menu).Count);
    }

    private static TrayMenu Build(List<string> chosen) => new(
        new HeadlessPlatform(),
        [
            new TrayMenuEntry("Open GitAlert", () => chosen.Add("open"), IsDefault: true),
            new TrayMenuEntry("Check now", () => chosen.Add("check")),
            TrayMenuEntry.Separator,
            new TrayMenuEntry("Quit", () => chosen.Add("quit")),
        ]);

    private static List<Button> Buttons(TrayMenu menu) => menu.GetVisualDescendants().OfType<Button>().ToList();

    private static Point Centre(Visual control, TrayMenu menu) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), menu)!.Value;
}
