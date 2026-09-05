using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>A pointer press this recent means the click, not the keyboard, asked for a scroll.</summary>
    private static readonly TimeSpan PointerRecency = TimeSpan.FromMilliseconds(250);

    private readonly FlyoutViewModel _viewModel;
    private readonly IPlatform _platform;
    private readonly DispatcherTimer _settleTimer;

    private DateTime _hiddenAt = DateTime.MinValue;
    private DateTime _shownAt = DateTime.MinValue;
    private DateTime _lastPointerPress = DateTime.MinValue;
    private int _foregroundAttempts;
    private bool _autoHide;
    private bool _hasStoredPlacement;
    private bool _reallyClosing;

    public FlyoutWindow(FlyoutViewModel viewModel, IPlatform platform)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _platform = platform;
        DataContext = viewModel;

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += OnSettled;

        Deactivated += OnDeactivated;
        PositionChanged += (_, _) => NotePlacement();
        SizeChanged += (_, _) => NotePlacement();

        // Clicking a row that sits half off the edge focuses it, and focus asks the list to scroll
        // the row fully into view - so the list jumped under the pointer. A pointer is already
        // where it wants to be; only the keyboard needs the help.
        AddHandler(PointerPressedEvent, (_, _) => _lastPointerPress = DateTime.UtcNow, RoutingStrategies.Tunnel);
        GroupList.AddHandler(RequestBringIntoViewEvent, OnListRequestBringIntoView);

        // A Button handles its own press and release before any handler attached to it runs, so
        // the drag is watched from the list on the way down instead.
        GroupList.AddHandler(PointerPressedEvent, OnGroupListPointerPressed, RoutingStrategies.Tunnel);
        GroupList.AddHandler(PointerMovedEvent, OnGroupListPointerMoved, RoutingStrategies.Tunnel);
        GroupList.AddHandler(PointerReleasedEvent, OnGroupListPointerReleased, RoutingStrategies.Tunnel);
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
            // Stored in device-independent units, the way the WPF build kept them, so a placement
            // survives the change of front end. Positions here are physical pixels.
            var scale = RenderScaling;
            Position = new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
            _hasStoredPlacement = true;
            KeepOnScreen();
        }

        if (settings.ListPaneShare is > 0 and < 1 and var share)
        {
            Panes.ColumnDefinitions[0].Width = new GridLength(share, GridUnitType.Star);
            Panes.ColumnDefinitions[2].Width = new GridLength(1 - share, GridUnitType.Star);
        }
    }

    /// <summary>Copies the current size, position and pinned state back into the settings.</summary>
    public void CapturePreferences(AppSettings settings)
    {
        if (WindowState == WindowState.Normal && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var scale = RenderScaling;
            settings.WindowLeft = Position.X / scale;
            settings.WindowTop = Position.Y / scale;
            settings.WindowWidth = Bounds.Width;
            settings.WindowHeight = Bounds.Height;
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
    public void ToggleFromTray(ScreenPoint screenPoint)
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

        if (!_platform.IsForeground(this))
        {
            Activate();
            TakeForeground();
            return;
        }

        HideFlyout();
    }

    public void ShowAt(ScreenPoint screenPoint)
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
            var scale = RenderScaling;
            var height = Bounds.Height > 0 ? Bounds.Height : Height;
            var size = new PixelSize((int)Math.Round(Width * scale), (int)Math.Round(height * scale));

            if (_platform.PlaceFlyout(screenPoint, size) is { } position)
            {
                Position = position;
            }
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
        else
        {
            Opacity = 1;
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

    private void TakeForeground()
    {
        _foregroundAttempts++;

        var won = _platform.TakeForeground(this);
        TraceLog.Write($"  foreground attempt {_foregroundAttempts}: {(won ? "won" : "lost")}");
    }

    /// <summary>
    /// Drags a remembered position back onto a monitor that still exists. Unplugging a second
    /// screen would otherwise reopen the window somewhere nobody can see.
    /// </summary>
    private void KeepOnScreen()
    {
        var screens = Screens.All;

        if (screens.Count == 0)
        {
            return;
        }

        var desktop = screens[0].Bounds;

        foreach (var screen in screens.Skip(1))
        {
            desktop = desktop.Union(screen.Bounds);
        }

        var scale = RenderScaling;
        var width = (int)Math.Round((Bounds.Width > 0 ? Bounds.Width : Width) * scale);

        // Keeping a strip of the window on screen is enough to drag it back by hand.
        var visible = (int)Math.Round(120 * scale);

        Position = new PixelPoint(
            Math.Clamp(Position.X, desktop.X - width + visible, desktop.Right - visible),
            Math.Clamp(Position.Y, desktop.Y, desktop.Bottom - visible));
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
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

        if (!IsVisible || IsActive || _platform.IsForeground(this))
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

    // ---- Dragging a project to a new place in the list ----------------------

    /// <summary>How far a pressed pointer travels before the press counts as a drag, not a click.</summary>
    private const double DragThreshold = 4;

    private Point _dragOrigin;
    private Button? _dragHeader;
    private ProjectGroupViewModel? _dragCandidate;
    private ProjectGroupViewModel? _dragged;

    /// <summary>
    /// A click folds the section, unless the press turned into a drag on the way. The tools on the
    /// header are buttons too, and their clicks bubble up through this one: those are theirs.
    /// </summary>
    private void OnGroupHeaderClick(object? sender, RoutedEventArgs e)
    {
        if (_dragged is not null || !ReferenceEquals(e.Source, sender))
        {
            return;
        }

        if (sender is Button { DataContext: ProjectGroupViewModel group })
        {
            group.ToggleCommand.Execute(null);
        }
    }

    /// <summary>
    /// A press on a project header may be the start of a drag. Nothing happens until the pointer
    /// has moved far enough to mean it, so a plain click still folds the section.
    /// </summary>
    private void OnGroupListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragCandidate = null;
        _dragHeader = null;
        _dragged = null;

        // The arrows live inside the header. A press on one of them is a click on it, not a drag.
        if (e.GetCurrentPoint(GroupList).Properties.IsLeftButtonPressed
            && HeaderUnder(e.Source) is { DataContext: ProjectGroupViewModel group } header)
        {
            _dragOrigin = e.GetPosition(this);
            _dragHeader = header;
            _dragCandidate = group;
        }
    }

    private void OnGroupListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is { } dragged)
        {
            UpdateDropMarkers(dragged, e.GetPosition(GroupList));
            ScrollTowardsEdge(e.GetPosition(ListScroller));
            return;
        }

        if (_dragCandidate is not { } group
            || _dragHeader is not { } header
            || !e.GetCurrentPoint(GroupList).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var moved = e.GetPosition(this) - _dragOrigin;

        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
        {
            return;
        }

        _dragCandidate = null;
        _dragged = group;
        group.IsBeingDragged = true;

        // From here on every move and the release come to the header, wherever the pointer goes.
        e.Pointer.Capture(header);
    }

    private void OnGroupListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragCandidate = null;
        _dragHeader = null;

        if (_dragged is not { } dragged)
        {
            return;
        }

        var (target, above) = TargetUnder(e.GetPosition(GroupList));
        _viewModel.ClearDragMarkers();

        // The list is rebuilt once the release has finished its round: the header that holds the
        // capture is still on the event's route, and rebuilding now would pull it out from under it.
        Dispatcher.UIThread.Post(() =>
        {
            if (target is not null && !ReferenceEquals(target, dragged))
            {
                _viewModel.PlaceProject(dragged, target, above);
            }

            // Cleared after the click that follows the release has had its chance to see it.
            _dragged = null;
        });
    }

    private void OnGroupHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Dropped here, dropped somewhere else or let go: nothing is in the air any more.
        _dragCandidate = null;
        _dragHeader = null;

        if (_dragged is not null)
        {
            _viewModel.ClearDragMarkers();
            Dispatcher.UIThread.Post(() => _dragged = null);
        }
    }

    /// <summary>
    /// The project header a press landed on, or null when it landed on one of the tool buttons
    /// inside the header, on an alert card, or outside any header at all.
    /// </summary>
    private static Button? HeaderUnder(object? source) =>
        source is Visual visual
        && visual.FindAncestorOfType<Button>(includeSelf: true) is { Name: "Header" } header
            ? header
            : null;

    private void UpdateDropMarkers(ProjectGroupViewModel dragged, Point point)
    {
        var (target, above) = TargetUnder(point);

        foreach (var group in _viewModel.Groups)
        {
            group.DropMarker = ReferenceEquals(group, target) && !ReferenceEquals(group, dragged)
                ? above ? DropMarker.Above : DropMarker.Below
                : DropMarker.None;
        }
    }

    /// <summary>
    /// The project under a point in the list, and whether the point is in the top half of its
    /// header. Anywhere lower in the section - the alerts inside an open one - means "after it".
    /// </summary>
    private (ProjectGroupViewModel? Group, bool Above) TargetUnder(Point point)
    {
        ProjectGroupViewModel? last = null;

        for (var i = 0; i < GroupList.ItemCount; i++)
        {
            if (GroupList.Items[i] is not ProjectGroupViewModel group
                || GroupList.ContainerFromIndex(i) is not { } container
                || container.TranslatePoint(new Point(0, 0), GroupList) is not { } origin)
            {
                continue;
            }

            last = group;

            var top = origin.Y;

            if (point.Y < top)
            {
                return (group, true);
            }

            if (point.Y <= top + container.Bounds.Height)
            {
                var header = container.FindDescendantOfType<Button>();
                var headerTop = header?.TranslatePoint(new Point(0, 0), GroupList)?.Y ?? top;
                var headerHeight = header?.Bounds.Height ?? container.Bounds.Height;

                return (group, point.Y < headerTop + headerHeight / 2);
            }
        }

        // Below everything: after the last project.
        return (last, false);
    }

    /// <summary>Creeps the list along while a drag hovers near its top or bottom edge.</summary>
    private void ScrollTowardsEdge(Point pointInScroller)
    {
        const double Edge = 28;
        const double Step = 10;

        var offset = ListScroller.Offset;

        if (pointInScroller.Y < Edge)
        {
            ListScroller.Offset = offset.WithY(Math.Max(0, offset.Y - Step));
        }
        else if (pointInScroller.Y > ListScroller.Bounds.Height - Edge)
        {
            ListScroller.Offset = offset.WithY(offset.Y + Step);
        }
    }

    // ---- The list beside the detail pane ---------------------------------------

    private void OnListRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (DateTime.UtcNow - _lastPointerPress < PointerRecency)
        {
            e.Handled = true;
        }
    }

    /// <summary>Where the splitter is left is worth keeping, like the window's own size.</summary>
    private void OnListSplitterDragCompleted(object? sender, VectorEventArgs e) =>
        PlacementChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The list's share of the width. A share rather than a width, so the two panes keep
    /// splitting the window the same way when it is dragged wider or narrower.
    /// </summary>
    private double? ListShare()
    {
        var list = Panes.ColumnDefinitions[0].ActualWidth;
        var detail = Panes.ColumnDefinitions[2].ActualWidth;

        return list > 0 && detail > 0 ? list / (list + detail) : null;
    }

    // ---- Lifetime and placement -----------------------------------------------

    protected override void OnClosing(WindowClosingEventArgs e)
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

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => HideFlyout();

    private void OnPinClicked(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _hasStoredPlacement = true;
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Anything the user does to the window counts as placing it deliberately, so later openings
    /// stop jumping back to the tray corner.
    /// </summary>
    private void NotePlacement()
    {
        if (IsVisible)
        {
            _hasStoredPlacement = true;
        }
    }

    private void PlayEntranceAnimation()
    {
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(130),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } },
            },
        };

        _ = fade.RunAsync(this).ContinueWith(_ => Dispatcher.UIThread.Post(() => Opacity = 1));
    }
}
