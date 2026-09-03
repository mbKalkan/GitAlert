using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GitAlert.Platform;
using GitAlert.ViewModels;

namespace GitAlert.Views;

/// <summary>
/// The panel that drops out of the tray icon. It behaves like a shell flyout: it appears beside the
/// icon, closes as soon as focus moves elsewhere or Escape is pressed, and never survives as a
/// stray window - closing only hides it.
/// </summary>
public partial class FlyoutWindow : Window
{
    /// <summary>
    /// Clicking the tray icon while the flyout is open deactivates it first, so a plain
    /// show-on-click would immediately reopen what the user just dismissed. Ignoring activations
    /// that arrive within this window of a dismissal makes the icon toggle properly.
    /// </summary>
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How long after opening a deactivation is treated as shell noise rather than a dismissal.
    /// Explorer is still tearing down its own tray popup while we appear, and the activation churn
    /// from that arrives here as a deactivation the user never asked for.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    private readonly FlyoutViewModel _viewModel;
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _settleTimer;

    private DateTime _hiddenAt = DateTime.MinValue;
    private DateTime _shownAt = DateTime.MinValue;
    private bool _reassertedForeground;
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

    /// <summary>True when a tray click should open rather than toggle closed.</summary>
    public bool ShouldOpenOnTrayClick =>
        !IsVisible && DateTime.UtcNow - _hiddenAt > ReopenGuard;

    public void ShowAt(Point screenPoint)
    {
        // Lay out first: the window sizes to its content, and the position depends on its height.
        Opacity = 0;
        _shownAt = DateTime.UtcNow;
        _reassertedForeground = false;

        Show();
        UpdateLayout();

        FlyoutPositioner.PositionNear(this, screenPoint);

        Activate();

        // Activate() alone loses the argument with Windows when the click that opened us went to
        // Explorer, and a panel that is active without being foreground is taken back immediately.
        TakeForeground();

        Focus();

        _viewModel.OnShown();
        PlayEntranceAnimation();
    }

    public void HideFlyout()
    {
        _settleTimer.Stop();

        if (!IsVisible)
        {
            return;
        }

        _hiddenAt = DateTime.UtcNow;
        _viewModel.OnHidden();
        Hide();
    }

    private IntPtr Handle =>
        PresentationSource.FromVisual(this) is HwndSource source ? source.Handle : IntPtr.Zero;

    private void TakeForeground() => NativeMethods.ForceForeground(Handle);

    /// <summary>
    /// The grace period is over. Anything short of actually holding the foreground now means the
    /// user's attention moved on, so the panel gets out of the way.
    /// </summary>
    private void OnSettled(object? sender, EventArgs e)
    {
        _settleTimer.Stop();

        if (!IsVisible || IsActive || NativeMethods.IsForeground(Handle))
        {
            return;
        }

        HideFlyout();
    }

    /// <summary>Closes the window for real, when the application is shutting down.</summary>
    public void CloseForGood()
    {
        _settleTimer.Stop();
        _reallyClosing = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            NativeMethods.AddExtendedStyle(source.Handle, NativeMethods.WS_EX_TOOLWINDOW);
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // A deactivation this soon after opening is the shell finishing its own business, not the
        // user clicking away. Reaching for the foreground once more usually settles it; either way
        // the decision waits until the churn has died down.
        if (DateTime.UtcNow - _shownAt < SettleDelay)
        {
            if (!_reassertedForeground)
            {
                _reassertedForeground = true;
                TakeForeground();
            }

            _settleTimer.Stop();
            _settleTimer.Start();
            return;
        }

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
