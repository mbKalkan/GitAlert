using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.ViewModels;

namespace GitAlert.Views;

/// <summary>
/// The window that drops out of the tray icon. It opens beside the icon like a flyout, but it is a
/// real resizable window that stays where it is put: reading a diff means reaching for a scrollbar,
/// another window, or a browser, and a panel that closes the moment focus moves cannot be read in.
/// Closing it only hides it - the tray icon owns the application's lifetime.
/// </summary>
public partial class FlyoutWindow : Window
{
    /// <summary>
    /// Clicking the tray icon while the window is open deactivates it first, so a plain
    /// show-on-click would immediately reopen what the user just dismissed. Only relevant while
    /// auto-hide is on, which is what makes that deactivation happen at all.
    /// </summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long after opening a deactivation is treated as shell noise rather than a dismissal.
    /// Explorer is still tearing down its own tray popup while we appear, and the activation churn
    /// from that arrives here as a deactivation the user never asked for.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How many times a single opening may fight the shell for the foreground.</summary>
    private const int MaxForegroundAttempts = 3;

    private readonly FlyoutViewModel _viewModel;
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _settleTimer;

    private DateTime _hiddenAt = DateTime.MinValue;
    private DateTime _shownAt = DateTime.MinValue;
    private int _foregroundAttempts;
    private bool _autoHide;
    private bool _hasStoredPlacement;
    private bool _reallyClosing;

    public FlyoutWindow(FlyoutViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        RenderTransform = _slide;

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += OnSettled;
    }

    /// <summary>Raised when the window's size, position or pinned state is worth persisting.</summary>
    public event EventHandler? PlacementChanged;

    /// <summary>Applies the parts of the settings the window itself owns.</summary>
    public void ApplyPreferences(AppSettings settings)
    {
        _autoHide = settings.AutoHideWindow;
        Topmost = settings.AlwaysOnTop;

        if (settings.WindowWidth is > 0 and var width && settings.WindowHeight is > 0 and var height)
        {
            Width = Math.Max(MinWidth, width);
            Height = Math.Max(MinHeight, height);
        }

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top)
        {
            Left = left;
            Top = top;
            _hasStoredPlacement = true;
            KeepOnScreen();
        }
    }

    /// <summary>Copies the current size, position and pinned state back into the settings.</summary>
    public void CapturePreferences(AppSettings settings)
    {
        var bounds = WindowState == WindowState.Normal && !double.IsNaN(Left) && ActualWidth > 0
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds is { Width: > 0, Height: > 0 } && !double.IsNaN(bounds.Left))
        {
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }

        settings.AlwaysOnTop = Topmost;
    }

    /// <summary>
    /// What a click on the tray icon does: open it, bring it forward if it is buried behind
    /// something, or tuck it away if it is already the window in front.
    /// </summary>
    public void ToggleFromTray(Point screenPoint)
    {
        if (!IsVisible)
        {
            // With auto-hide on, the click that reaches us has already dismissed the window by
            // moving the foreground away. Reopening here would undo the user's own click.
            if (_autoHide && DateTime.UtcNow - _hiddenAt <= ReopenGuard)
            {
                return;
            }

            ShowAt(screenPoint);
            return;
        }

        if (!NativeMethods.IsForeground(Handle))
        {
            Activate();
            TakeForeground();
            return;
        }

        HideFlyout();
    }

    public void ShowAt(Point screenPoint)
    {
        var wasVisible = IsVisible;

        _shownAt = DateTime.UtcNow;
        _foregroundAttempts = 0;

        TraceLog.Write($"window show at {screenPoint.X},{screenPoint.Y}");

        if (!wasVisible)
        {
            Opacity = 0;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        UpdateLayout();

        // Once the user has moved or resized the window, that is where they want it. Only a
        // window that has never been placed gets parked beside the tray icon.
        if (_hasStoredPlacement)
        {
            KeepOnScreen();
        }
        else
        {
            FlyoutPositioner.PositionNear(this, screenPoint);
        }

        Activate();

        // Activate() alone loses the argument with Windows when the click that opened us went to
        // Explorer, and a window that is active without being foreground is taken back immediately.
        TakeForeground();

        Focus();

        _viewModel.OnShown();

        if (!wasVisible)
        {
            PlayEntranceAnimation();
        }
    }

    public void HideFlyout()
    {
        _settleTimer.Stop();

        if (!IsVisible)
        {
            return;
        }

        TraceLog.Write("window hide");

        _hiddenAt = DateTime.UtcNow;
        _viewModel.OnHidden();
        Hide();

        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the window for real, when the application is shutting down.</summary>
    public void CloseForGood()
    {
        _settleTimer.Stop();
        _reallyClosing = true;
        Close();
    }

    private IntPtr Handle =>
        PresentationSource.FromVisual(this) is HwndSource source ? source.Handle : IntPtr.Zero;

    private void TakeForeground()
    {
        _foregroundAttempts++;

        var won = NativeMethods.ForceForeground(Handle);
        TraceLog.Write($"  foreground attempt {_foregroundAttempts}: {(won ? "won" : "lost")}");
    }

    /// <summary>
    /// Drags a remembered position back onto a monitor that still exists. Unplugging a second
    /// screen would otherwise reopen the window somewhere nobody can see.
    /// </summary>
    private void KeepOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        // Keeping a strip of the window on screen is enough to drag it back by hand.
        const double Visible = 120;

        Left = Math.Clamp(Left, left - width + Visible, right - Visible);
        Top = Math.Clamp(Top, top, bottom - Visible);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (!_autoHide)
        {
            return;
        }

        var age = DateTime.UtcNow - _shownAt;
        TraceLog.Write($"window deactivated {age.TotalMilliseconds:F0}ms after opening");

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

        HideFlyout();
    }

    /// <summary>
    /// The grace period is over. Anything short of actually holding the foreground now means the
    /// user's attention moved on, so the window gets out of the way.
    /// </summary>
    private void OnSettled(object? sender, EventArgs e)
    {
        _settleTimer.Stop();

        if (!IsVisible || IsActive || NativeMethods.IsForeground(Handle))
        {
            TraceLog.Write("window settled: staying open");
            return;
        }

        TraceLog.Write("window settled: never held the foreground, closing");
        HideFlyout();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFlyout();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The tray icon owns the app's lifetime, so a close request just tucks the panel away.
        if (!_reallyClosing)
        {
            e.Cancel = true;
            HideFlyout();
            return;
        }

        base.OnClosing(e);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => HideFlyout();

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _hasStoredPlacement = true;
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);

        // Anything the user does to the window counts as placing it deliberately, so later
        // openings stop jumping back to the tray corner.
        if (IsVisible)
        {
            _hasStoredPlacement = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        if (IsVisible)
        {
            _hasStoredPlacement = true;
        }
    }

    private void PlayEntranceAnimation()
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var rise = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
