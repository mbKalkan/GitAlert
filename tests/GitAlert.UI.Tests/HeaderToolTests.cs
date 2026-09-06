using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using GitAlert.Views;
using Xunit;

namespace GitAlert.UI.Tests;

/// <summary>The tools on a project header, clicked where they are: on a folded project, inside a section.</summary>
public class HeaderToolTests
{
    [AvaloniaFact]
    public void The_tick_on_a_folded_project_inside_a_section_reads_the_project()
    {
        using var errors = new BindingErrors();
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var inside = vm.Groups.Single(g => g.Repository == "acme/api-gateway");
            var loose = vm.Groups.Single(g => g.Repository == "mbKalkan/GitAlert");
            inside.IsExpanded = false;
            loose.IsExpanded = false;
            Frames.Settle();

            Assert.True(inside.IsInSection);
            Assert.True(inside.HasUnread || loose.HasUnread);

            foreach (var project in new[] { loose, inside }.Where(p => p.HasUnread))
            {
                var tick = ToolOf(window, project, "Mark everything in this project read");
                Assert.True(tick.IsEffectivelyEnabled);

                var at = Centre(tick, window);
                window.MouseMove(at);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Frames.Settle();

                Assert.All(project.Items, a => Assert.True(a.IsRead));
                Assert.False(project.HasUnread);
                Assert.False(tick.IsEffectivelyEnabled);
            }

            Assert.Empty(errors.Messages);
        }
        finally
        {
            dispose();
        }
    }

    private static Button ToolOf(FlyoutWindow window, ProjectGroupViewModel project, string tip) =>
        window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.DataContext, project) && ToolTip.GetTip(b) as string == tip);

    private static Avalonia.Point Centre(Visual control, FlyoutWindow window) =>
        control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static (FlyoutWindow Window, FlyoutViewModel ViewModel, Action Dispose) Build()
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settings = SampleData.Settings(account, sectioned: true);

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
}
