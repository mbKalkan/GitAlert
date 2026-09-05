using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

    private static (FlyoutWindow Window, FlyoutViewModel ViewModel, Action Dispose) Build()
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
        var window = new FlyoutWindow(vm, new HeadlessPlatform());

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
}
