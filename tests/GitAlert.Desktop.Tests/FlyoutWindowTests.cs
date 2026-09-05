using System.Net.Http;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Services;
using GitAlert.ViewModels;
using GitAlert.Views;
using Xunit;

namespace GitAlert.Desktop.Tests;

/// <summary>
/// The flyout as the user sees it: what a project unfolds, what an open alert unfolds, and that
/// every binding in the window finds what it points at. A binding that misses is silent at run time
/// and shows up as a blank spot in the window, which is how the first port lost every alert row.
/// </summary>
public class FlyoutWindowTests
{
    [AvaloniaFact]
    public void An_open_project_shows_its_alerts_and_an_open_alert_its_files()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var project = vm.Groups.First(g => g.Repository == "mbKalkan/GitAlert");

            // Folded: nothing of the project shows; expanded: one card per alert.
            project.IsExpanded = false;
            Frames.Settle();

            Assert.Empty(Cards(window, project));

            project.IsExpanded = true;
            Frames.Settle();

            Assert.Equal(2, Cards(window, project).Count);

            // Opening the push unfolds the files the commit touched under it.
            var push = project.Items.First(a => a.Kind == AlertKind.Push);
            vm.SelectAlertCommand.ExecuteAsync(push).GetAwaiter().GetResult();
            Frames.Settle();

            Assert.Equal(2, FileRows(window).Count);
            Assert.True(vm.Detail.HasSelectedFile, "the first file opens in the pane");

            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Reading_an_alert_dims_its_title_and_takes_the_dot_away()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            var project = vm.Groups.First(g => g.Repository == "mbKalkan/GitAlert");
            project.IsExpanded = true;
            Frames.Settle();

            var unread = project.Items.First(a => !a.IsRead);
            var title = TitleOf(window, unread);
            var dot = DotOf(window, unread);

            Assert.Equal(Avalonia.Media.FontWeight.SemiBold, title.FontWeight);
            Assert.Equal(1, dot.Opacity);

            unread.MarkRead();
            Frames.Settle();

            Assert.Equal(Avalonia.Media.FontWeight.Normal, title.FontWeight);
            Assert.Equal(0, dot.Opacity);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void The_window_renders_a_frame_in_every_palette()
    {
        var theme = new ThemeService(Avalonia.Application.Current!);
        var (window, _, dispose) = Build();

        try
        {
            window.Show();

            foreach (var (mode, palette) in new[]
            {
                (Configuration.AppTheme.Dark, Configuration.DarkPalette.VsCode),
                (Configuration.AppTheme.Dark, Configuration.DarkPalette.GitHub),
                (Configuration.AppTheme.Light, Configuration.DarkPalette.VsCode),
            })
            {
                theme.Apply(mode, palette);
                Frames.Settle();

                var frame = window.CaptureRenderedFrame();

                Assert.NotNull(frame);
                Assert.Equal(1020, frame.PixelSize.Width);
                Assert.Equal(660, frame.PixelSize.Height);
            }
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void A_click_on_a_project_header_folds_it()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var project = vm.Groups[0];
            var wasExpanded = project.IsExpanded;
            var at = Centre(HeaderOf(window, project), window);

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Frames.Settle();

            Assert.Equal(!wasExpanded, project.IsExpanded);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void The_arrows_on_a_header_move_the_project_without_folding_it()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var first = vm.Groups[0];
            var second = vm.Groups[1];
            var (firstOpen, secondOpen) = (first.IsExpanded, second.IsExpanded);

            // The tools only show while the pointer is over the header, as they do for the user.
            window.MouseMove(Centre(HeaderOf(window, second), window));
            Frames.Settle();

            var at = Centre(ToolOf(window, second, "Move this project up"), window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Frames.Settle();

            Assert.Equal([second.Repository, first.Repository], vm.Groups.Select(g => g.Repository));
            Assert.Equal(firstOpen, first.IsExpanded);
            Assert.Equal(secondOpen, second.IsExpanded);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Dragging_a_project_header_above_another_puts_it_there()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var first = vm.Groups[0];
            var second = vm.Groups[1];
            var secondOpen = second.IsExpanded;

            var grip = Centre(HeaderOf(window, second), window);
            var target = HeaderOf(window, first);
            var drop = target.TranslatePoint(new Point(target.Bounds.Width / 2, 2), window)!.Value;

            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(grip + new Vector(0, -8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
            Frames.Settle();

            Assert.True(second.IsBeingDragged, "the header past the threshold is in the air");
            Assert.Equal(DropMarker.Above, first.DropMarker);

            window.MouseUp(drop, MouseButton.Left);
            Frames.Settle();

            Assert.Equal([second.Repository, first.Repository], vm.Groups.Select(g => g.Repository));
            Assert.False(second.IsBeingDragged);
            Assert.All(vm.Groups, g => Assert.Equal(DropMarker.None, g.DropMarker));
            Assert.Equal(secondOpen, second.IsExpanded);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void The_header_drags_the_window_but_its_buttons_keep_their_clicks()
    {
        var (window, _, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var title = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "GitAlert");
            var close = window.FindControl<Button>("CloseButton")!;

            Assert.Equal(WindowDecorationsElementRole.TitleBar, ChromeRoleAt(window, Centre(title, window)));
            Assert.Equal(WindowDecorationsElementRole.User, ChromeRoleAt(window, Centre(close, window)));
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>
    /// The first layout pass inside Show() changes the size while the window already counts as
    /// visible, and that used to pass for the user having placed it: the tray-side parking never ran.
    /// </summary>
    [AvaloniaFact]
    public void A_window_never_placed_is_parked_where_the_platform_says_on_its_first_opening()
    {
        var platform = new HeadlessPlatform { FlyoutPlace = new PixelPoint(640, 360) };
        var (window, _, dispose) = Build(platform);

        try
        {
            window.ShowAt(new ScreenPoint(1900, 1040));
            Frames.Settle();

            Assert.Equal(new PixelPoint(640, 360), window.Position);
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>
    /// At quit the windows are closed before the shell saves, and a closed window reports its
    /// position as the origin; the next start then opened in the top-left corner.
    /// </summary>
    [AvaloniaFact]
    public void The_placement_saved_after_the_window_closed_is_where_it_last_stood()
    {
        var (window, _, dispose) = Build();

        try
        {
            window.ShowAt(new ScreenPoint(0, 0));
            Frames.Settle();

            window.Position = new PixelPoint(300, 200);
            Frames.Settle();

            window.HideFlyout();
            window.CloseForGood();

            var settings = new AppSettings();
            window.CapturePreferences(settings);

            Assert.Equal(300, settings.WindowLeft);
            Assert.Equal(200, settings.WindowTop);
            Assert.Equal(1020, settings.WindowWidth);
            Assert.Equal(660, settings.WindowHeight);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void A_close_request_hides_the_window_and_it_opens_again()
    {
        var (window, _, dispose) = Build();

        try
        {
            window.ShowAt(new ScreenPoint(0, 0));
            Frames.Settle();

            window.Close();
            Frames.Settle();

            Assert.False(window.IsVisible);

            window.ShowAt(new ScreenPoint(0, 0));
            Frames.Settle();

            Assert.True(window.IsVisible);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Letting_go_of_a_dragged_header_outside_the_list_moves_nothing()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var first = vm.Groups[0];
            var second = vm.Groups[1];

            var grip = Centre(HeaderOf(window, second), window);
            var outside = new Point(window.Bounds.Width - 40, grip.Y);

            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(grip + new Vector(0, -8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            Frames.Settle();

            Assert.True(second.IsBeingDragged);
            Assert.All(vm.Groups, g => Assert.Equal(DropMarker.None, g.DropMarker));

            window.MouseUp(outside, MouseButton.Left);
            Frames.Settle();

            Assert.Equal([first.Repository, second.Repository], vm.Groups.Select(g => g.Repository));
            Assert.False(second.IsBeingDragged);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void The_header_tools_show_while_one_of_them_holds_the_keyboard_focus()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var first = vm.Groups[0];
            var tools = window.GetVisualDescendants()
                .OfType<StackPanel>()
                .First(p => p.Name == "HeaderTools" && ReferenceEquals(p.DataContext, first));

            Assert.Equal(0, tools.Opacity);

            ToolOf(window, first, "Move this project down").Focus(NavigationMethod.Tab);
            Frames.Settle();

            Assert.Equal(1, tools.Opacity);
        }
        finally
        {
            dispose();
        }
    }

    private static (FlyoutWindow Window, FlyoutViewModel ViewModel, Action Dispose) Build(HeadlessPlatform? platform = null)
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settings = SampleData.Settings(account);

        var store = new AlertStore(Path.Combine(work, "history.json"));
        store.Add(SampleData.Alerts(account));

        var monitor = new MonitorService(
            store,
            new StateStore(Path.Combine(work, "state.json")),
            new HttpClient(new DiffHandler()));

        monitor.Configure(settings, new Dictionary<string, string> { [account.Id] = "ghp_sample" });

        var vm = new FlyoutViewModel(store, monitor, new NoShell(), settings);
        var window = new FlyoutWindow(vm, platform ?? new HeadlessPlatform());

        return (window, vm, () =>
        {
            window.CloseForGood();
            vm.Dispose();
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    private static List<Button> Cards(FlyoutWindow window, ProjectGroupViewModel project) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.DataContext is AlertViewModel alert && project.Items.Contains(alert) && b.IsEffectivelyVisible)
            .ToList();

    private static List<Button> FileRows(FlyoutWindow window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is FileDiffViewModel).ToList();

    private static TextBlock TitleOf(FlyoutWindow window, AlertViewModel alert) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "TitleLine" && ReferenceEquals(t.DataContext, alert));

    private static Avalonia.Controls.Shapes.Ellipse DotOf(FlyoutWindow window, AlertViewModel alert) =>
        window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().First(e => e.Name == "UnreadDot" && ReferenceEquals(e.DataContext, alert));

    private static Button HeaderOf(FlyoutWindow window, ProjectGroupViewModel project) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "Header" && ReferenceEquals(b.DataContext, project));

    /// <summary>One of the small buttons on a project header, found by what its tooltip promises.</summary>
    private static Button ToolOf(FlyoutWindow window, ProjectGroupViewModel project, string tooltip) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => ReferenceEquals(b.DataContext, project) && ToolTip.GetTip(b) as string == tooltip);

    /// <summary>The middle of a control in window coordinates, where the headless mouse is aimed.</summary>
    private static Point Centre(Visual control, FlyoutWindow window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>
    /// The decoration role the platform acts on at a point: window move, or a click for the control
    /// there. Avalonia keeps this hit test internal, so it is asked through reflection; should a
    /// later version move it, this is the test that says so.
    /// </summary>
    private static WindowDecorationsElementRole? ChromeRoleAt(FlyoutWindow window, Point point)
    {
        // The window's input root is a separate object, and an internal interface method cannot
        // be invoked through the interface - only through that object's own implementation.
        var root = typeof(TopLevel)
            .GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(window)
            ?? throw new InvalidOperationException("TopLevel.InputRoot is gone; find where the chrome hit test moved.");

        var map = root.GetType().GetInterfaceMap(typeof(IInputRoot));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == "HitTestChromeElement");

        if (index < 0)
        {
            throw new InvalidOperationException("IInputRoot.HitTestChromeElement is gone; find where the chrome hit test moved.");
        }

        return (WindowDecorationsElementRole?)map.TargetMethods[index].Invoke(root, [point]);
    }
}
