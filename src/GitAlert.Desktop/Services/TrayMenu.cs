using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using GitAlert.Core;
using GitAlert.Platform;

namespace GitAlert.Services;

/// <summary>
/// The menu behind a right click on the tray icon. A flyout needs something to hang off, and the
/// icon is not ours to draw on, so a one-pixel invisible window is parked under the pointer for as
/// long as the menu is open. It also gives the menu a window to dismiss against.
/// </summary>
public sealed class TrayMenu : IDisposable
{
    /// <summary>One line of the menu; <see cref="Separator"/> draws a rule instead.</summary>
    public sealed record Entry(string Header, Action? Action, bool Bold = false)
    {
        public static readonly Entry Separator = new(string.Empty, null);

        public bool IsSeparator => Action is null;
    }

    private readonly IPlatform _platform;
    private readonly Window _anchor;
    private readonly MenuFlyout _flyout;

    public TrayMenu(IPlatform platform, IReadOnlyList<Entry> entries)
    {
        _platform = platform;

        _anchor = new Window
        {
            Width = 1,
            Height = 1,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            ShowInTaskbar = false,
            ShowActivated = true,
            CanResize = false,
            Topmost = true,
            Content = new Border { Width = 1, Height = 1, Background = Brushes.Transparent },
        };

        _flyout = new MenuFlyout
        {
            Placement = PlacementMode.Pointer,
            FlyoutPresenterClasses = { "tray" },
        };

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                _flyout.Items.Add(new Separator());
                continue;
            }

            var item = new MenuItem { Header = entry.Header };

            if (entry.Bold)
            {
                item.FontWeight = FontWeight.SemiBold;
            }

            item.Click += (_, _) => entry.Action?.Invoke();
            _flyout.Items.Add(item);
        }

        _flyout.Closed += (_, _) => _anchor.Hide();
    }

    public void ShowAt(ScreenPoint screenPoint)
    {
        _anchor.Position = new PixelPoint(screenPoint.X, screenPoint.Y);
        _anchor.Show();
        _anchor.Activate();

        // Without the foreground the menu would not close when the user clicks elsewhere.
        _platform.TakeForeground(_anchor);

        _flyout.ShowAt((Control)_anchor.Content!);
    }

    public void Dispose()
    {
        _flyout.Hide();
        _anchor.Close();
    }
}
