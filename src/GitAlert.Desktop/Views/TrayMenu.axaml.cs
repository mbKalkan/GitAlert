using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GitAlert.Core;
using GitAlert.Platform;

namespace GitAlert.Views;

/// <summary>
/// The menu behind a right click on the tray icon. It is a small borderless window rather than a
/// popup: a popup dismisses itself the moment the window it hangs off is deactivated, and right
/// after a tray click the shell is still fighting for the foreground, so the popup was gone before
/// anyone saw it. A window can be handed the foreground the way the flyout is, and told apart a
/// deactivation that is shell noise from one that means the user moved on.
/// </summary>
public partial class TrayMenu : Window, IDisposable
{
    /// <summary>One line of the menu; <see cref="Separator"/> draws a rule instead.</summary>
    public sealed record Entry(string Header, Action? Action, bool Bold = false)
    {
        public static readonly Entry Separator = new(string.Empty, null);

        public bool IsSeparator => Action is null;
    }

    /// <summary>
    /// How long after opening a deactivation is shell noise rather than a dismissal; the same
    /// grace the flyout gives, for the same reason.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How many times a single opening may fight the shell for the foreground.</summary>
    private const int MaxForegroundAttempts = 3;

    private readonly IPlatform _platform;
    private readonly DispatcherTimer _settleTimer;
    private readonly List<Button> _buttons = [];

    private DateTime _shownAt = DateTime.MinValue;
    private int _foregroundAttempts;
    private bool _reallyClosing;

    public TrayMenu(IPlatform platform, IReadOnlyList<Entry> entries)
    {
        InitializeComponent();

        _platform = platform;

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += OnSettled;

        Deactivated += OnDeactivated;
        Opened += (_, _) => _platform.RoundCorners(this);

        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                Items.Children.Add(new Border { Classes = { "trayRule" } });
                continue;
            }

            var button = new Button { Content = entry.Header };

            if (entry.Bold)
            {
                button.FontWeight = FontWeight.SemiBold;
            }

            // Put away first: the action may open another window, which must not be the thing
            // that deactivates this one.
            button.Click += (_, _) =>
            {
                Dismiss("entry chosen");
                entry.Action?.Invoke();
            };

            Items.Children.Add(button);
            _buttons.Add(button);
        }
    }

    /// <summary>Opens the menu at the pointer, kept whole on the screen the pointer is on.</summary>
    public void ShowAt(ScreenPoint screenPoint)
    {
        _shownAt = DateTime.UtcNow;
        _foregroundAttempts = 0;

        TraceLog.Write($"menu show at {screenPoint.X},{screenPoint.Y}");

        // Size to content is only known once shown; place it while invisible, then let it appear.
        Opacity = 0;
        Position = new Avalonia.PixelPoint(screenPoint.X, screenPoint.Y);
        Show();
        UpdateLayout();
        Position = Place(screenPoint);
        Opacity = 1;

        Activate();
        TakeForeground();

        // The first entry is highlighted the way the system's menus do it, so the arrow keys and
        // Enter work from the start.
        _buttons.FirstOrDefault(b => b.IsEnabled)?.Focus(NavigationMethod.Directional);
    }

    public void Dispose()
    {
        _settleTimer.Stop();
        _reallyClosing = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss("escape");
                e.Handled = true;
                return;

            case Key.Down:
                MoveFocus(1);
                e.Handled = true;
                return;

            case Key.Up:
                MoveFocus(-1);
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The shell owns the menu's lifetime; a close request just puts it away.
        if (!_reallyClosing)
        {
            e.Cancel = true;
            Dismiss("close requested");
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// The top-left corner that keeps the whole menu on the pointer's screen: to the right and
    /// below the pointer when there is room, folded up above it when the taskbar sits at the
    /// bottom, which is where it usually is.
    /// </summary>
    private Avalonia.PixelPoint Place(ScreenPoint pointer)
    {
        var scale = RenderScaling;
        var width = (int)Math.Round(Bounds.Width * scale);
        var height = (int)Math.Round(Bounds.Height * scale);

        var at = new Avalonia.PixelPoint(pointer.X, pointer.Y);
        var screen = Screens.ScreenFromPoint(at) ?? Screens.Primary;

        if (screen is null)
        {
            return at;
        }

        var area = screen.WorkingArea;
        var left = Math.Clamp(at.X, area.X, Math.Max(area.X, area.Right - width));
        var top = at.Y + height <= area.Bottom ? at.Y : Math.Max(area.Y, at.Y - height);

        return new Avalonia.PixelPoint(left, top);
    }

    private void MoveFocus(int step)
    {
        var enabled = _buttons.Where(b => b.IsEnabled).ToList();

        if (enabled.Count == 0)
        {
            return;
        }

        var current = enabled.FindIndex(b => b.IsFocused);
        var next = current < 0
            ? (step > 0 ? 0 : enabled.Count - 1)
            : (current + step + enabled.Count) % enabled.Count;

        enabled[next].Focus(NavigationMethod.Directional);
    }

    private void TakeForeground()
    {
        _foregroundAttempts++;

        var won = _platform.TakeForeground(this);
        TraceLog.Write($"  menu foreground attempt {_foregroundAttempts}: {(won ? "won" : "lost")}");
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        var age = DateTime.UtcNow - _shownAt;
        TraceLog.Write($"menu deactivated {age.TotalMilliseconds:F0}ms after opening");

        // Explorer is still tearing down its own tray handling while we appear, and that reaches
        // us as a deactivation the user never asked for. Fight back for a moment, then decide.
        if (age < SettleDelay)
        {
            if (_foregroundAttempts < MaxForegroundAttempts)
            {
                TakeForeground();
            }

            _settleTimer.Stop();
            _settleTimer.Start();
            return;
        }

        Dismiss("focus moved away");
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        _settleTimer.Stop();

        if (!IsVisible || IsActive || _platform.IsForeground(this))
        {
            TraceLog.Write("menu settled: staying open");
            return;
        }

        // A menu nobody can dismiss is worse than one that has to be opened again.
        Dismiss("never held the foreground");
    }

    private void Dismiss(string reason)
    {
        _settleTimer.Stop();

        if (!IsVisible)
        {
            return;
        }

        TraceLog.Write($"menu hidden: {reason}");
        Hide();
    }
}
