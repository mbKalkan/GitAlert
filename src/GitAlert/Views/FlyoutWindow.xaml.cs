using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        if (settings.ListPaneShare is > 0 and < 1 and var share)
        {
            ListColumn.Width = new GridLength(share, GridUnitType.Star);
            DetailColumn.Width = new GridLength(1 - share, GridUnitType.Star);
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

        if (ListShare() is { } share)
        {
            settings.ListPaneShare = share;
        }
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
            var dpi = VisualTreeHelper.GetDpi(this);
            var height = ActualHeight > 0 ? ActualHeight : Height;
            var anchor = new ScreenPoint((int)screenPoint.X, (int)screenPoint.Y);

            (Left, Top) = FlyoutPositioner.Place(anchor, Width, height, dpi.DpiScaleX, dpi.DpiScaleY);
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

    /// <summary>
    /// Shift and the wheel scroll sideways, the way every editor and browser does it. WPF's
    /// ScrollViewer only ever turns the wheel into vertical movement, which left a wide diff
    /// reachable by the thin bar under it and nothing else.
    /// </summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && e.OriginalSource is DependencyObject source
            && FindSidewaysScroller(source) is { } scroller)
        {
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    /// <summary>The nearest scroll viewer above an element that has somewhere sideways to go.</summary>
    private static ScrollViewer? FindSidewaysScroller(DependencyObject start)
    {
        for (var current = start; current is not null; current = ParentOf(current))
        {
            if (current is ScrollViewer { ScrollableWidth: > 0 } scroller)
            {
                return scroller;
            }
        }

        return null;
    }

    // ---- Dragging a project to a new place in the list ----------------------

    private Point _dragOrigin;
    private ProjectGroupViewModel? _dragCandidate;

    /// <summary>
    /// A press on a project header may be the start of a drag. Nothing happens until the mouse
    /// has moved far enough to mean it, so a plain click still folds the section.
    /// </summary>
    private void OnGroupHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;

        // The arrows live inside the header. A press on one of them is a click on it, not a drag.
        if (sender is Button header
            && header.DataContext is ProjectGroupViewModel group
            && e.OriginalSource is DependencyObject source
            && ReferenceEquals(FindAncestor<Button>(source), header))
        {
            _dragOrigin = e.GetPosition(this);
            _dragCandidate = group;
        }
    }

    private void OnGroupHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } group || sender is not Button header || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(this) - _dragOrigin;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = null;
        group.IsBeingDragged = true;

        try
        {
            DragDrop.DoDragDrop(header, new DataObject(typeof(ProjectGroupViewModel), group), DragDropEffects.Move);
        }
        finally
        {
            // Dropped here, dropped somewhere else or let go: nothing is in the air any more.
            _viewModel.ClearDragMarkers();
        }
    }

    private void OnGroupsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        if (sender is not ItemsControl list || Dragged(e) is not { } dragged)
        {
            return;
        }

        var (target, above) = TargetUnder(list, e.GetPosition(list));

        foreach (var group in _viewModel.Groups)
        {
            group.DropMarker = ReferenceEquals(group, target) && !ReferenceEquals(group, dragged)
                ? above ? DropMarker.Above : DropMarker.Below
                : DropMarker.None;
        }

        if (target is not null && !ReferenceEquals(target, dragged))
        {
            e.Effects = DragDropEffects.Move;
        }

        ScrollTowardsEdge(list, e);
    }

    private void OnGroupsDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires on the way from one row to the next, so only a pointer that has
        // really left the list takes the line down.
        if (sender is not ItemsControl list)
        {
            return;
        }

        var point = e.GetPosition(list);

        if (point.X < 0 || point.Y < 0 || point.X > list.ActualWidth || point.Y > list.ActualHeight)
        {
            foreach (var group in _viewModel.Groups)
            {
                group.DropMarker = DropMarker.None;
            }
        }
    }

    private void OnGroupsDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not ItemsControl list || Dragged(e) is not { } dragged)
        {
            return;
        }

        var (target, above) = TargetUnder(list, e.GetPosition(list));
        _viewModel.ClearDragMarkers();

        if (target is not null)
        {
            _viewModel.PlaceProject(dragged, target, above);
        }
    }

    private static ProjectGroupViewModel? Dragged(DragEventArgs e) =>
        e.Data.GetData(typeof(ProjectGroupViewModel)) as ProjectGroupViewModel;

    /// <summary>
    /// The project under a point in the list, and whether the point is in the top half of its
    /// header. Anywhere lower in the section - the alerts inside an open one - means "after it".
    /// </summary>
    private static (ProjectGroupViewModel? Group, bool Above) TargetUnder(ItemsControl list, Point point)
    {
        ProjectGroupViewModel? last = null;

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is not ProjectGroupViewModel group
                || list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            last = group;

            var top = container.TranslatePoint(new Point(0, 0), list).Y;

            if (point.Y < top)
            {
                return (group, true);
            }

            if (point.Y <= top + container.ActualHeight)
            {
                var header = FindDescendant<Button>(container);
                var headerTop = header?.TranslatePoint(new Point(0, 0), list).Y ?? top;
                var headerHeight = header?.ActualHeight ?? container.ActualHeight;

                return (group, point.Y < headerTop + headerHeight / 2);
            }
        }

        // Below everything: after the last project.
        return (last, false);
    }

    /// <summary>Creeps the list along while a drag hovers near its top or bottom edge.</summary>
    private static void ScrollTowardsEdge(ItemsControl list, DragEventArgs e)
    {
        if (FindAncestor<ScrollViewer>(list) is not { } scroller)
        {
            return;
        }

        const double Edge = 28;
        const double Step = 10;

        var y = e.GetPosition(scroller).Y;

        if (y < Edge)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - Step);
        }
        else if (y > scroller.ActualHeight - Edge)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + Step);
        }
    }

    // ---- The list beside the detail pane ---------------------------------------

    /// <summary>
    /// Clicking a row that sits half off the edge focuses it, and focus asks the list to scroll
    /// the row fully into view - so the list jumped under the pointer. A pointer is already
    /// where it wants to be; only the keyboard needs the help.
    /// </summary>
    private void OnListRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice)
        {
            e.Handled = true;
        }
    }

    /// <summary>Where the splitter is left is worth keeping, like the window's own size.</summary>
    private void OnListSplitterDragCompleted(object sender, DragCompletedEventArgs e) =>
        PlacementChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The list's share of the width. A share rather than a width, so the two panes keep
    /// splitting the window the same way when it is dragged wider or narrower.
    /// </summary>
    private double? ListShare()
    {
        var list = ListColumn.ActualWidth;
        var detail = DetailColumn.ActualWidth;

        return list > 0 && detail > 0 ? list / (list + detail) : null;
    }

    // ---- Tree walking --------------------------------------------------------

    /// <summary>The parent of a node, whichever tree it lives in: a Run has no visual parent.</summary>
    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = ParentOf(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
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
