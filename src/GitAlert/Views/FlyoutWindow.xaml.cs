using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    private readonly FlyoutViewModel _viewModel;
    private readonly TranslateTransform _slide = new();

    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _reallyClosing;

    public FlyoutWindow(FlyoutViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        RenderTransform = _slide;
    }

    /// <summary>True when a tray click should open rather than toggle closed.</summary>
    public bool ShouldOpenOnTrayClick =>
        !IsVisible && DateTime.UtcNow - _hiddenAt > ReopenGuard;

    public void ShowAt(Point screenPoint)
    {
        // Lay out first: the window sizes to its content, and the position depends on its height.
        Opacity = 0;
        Show();
        UpdateLayout();

        FlyoutPositioner.PositionNear(this, screenPoint);

        Activate();
        Focus();

        _viewModel.OnShown();
        PlayEntranceAnimation();
    }

    public void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }

        _hiddenAt = DateTime.UtcNow;
        _viewModel.OnHidden();
        Hide();
    }

    /// <summary>Closes the window for real, when the application is shutting down.</summary>
    public void CloseForGood()
    {
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
