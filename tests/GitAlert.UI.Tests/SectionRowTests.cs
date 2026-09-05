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

/// <summary>
/// Sections in the window: the header that folds them, the box that names them, the drop that
/// fills them and the two buttons above the list that fold and unfold everything at once.
/// </summary>
public class SectionRowTests
{
    [AvaloniaFact]
    public void A_click_on_a_section_header_folds_the_projects_under_it()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var work = vm.Rows.OfType<ProjectSectionViewModel>().Single();
            var inside = vm.Groups.Single(g => g.Repository == "acme/api-gateway");

            Assert.True(inside.IsInSection);
            Assert.Single(HeadersOf(window, inside));

            var at = Centre(SectionHeaderOf(window, work), window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Frames.Settle();

            Assert.False(work.IsExpanded);
            Assert.Empty(HeadersOf(window, inside));

            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Frames.Settle();

            Assert.True(work.IsExpanded);
            Assert.Single(HeadersOf(window, inside));
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void New_section_opens_a_name_box_that_takes_the_typing_and_keeps_it_on_enter()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var button = window.FindControl<Button>("NewSectionButton")!;
            var at = Centre(button, window);
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Frames.Settle();

            var added = Assert.IsType<ProjectSectionViewModel>(vm.Rows[^1]);
            var box = NameBoxOf(window, added);

            Assert.True(box.IsVisible);
            Assert.True(box.IsFocused, "the name box takes the focus so typing goes straight into it");
            Assert.Equal(ProjectSection.DefaultName, box.SelectedText);

            window.KeyTextInput("Client work");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Frames.Settle();

            Assert.False(added.IsEditing);
            Assert.Equal("Client work", added.Name);
            Assert.False(box.IsVisible);
        }
        finally
        {
            dispose();
        }
    }

    /// <summary>Escape closes the window everywhere else; in the name box it only drops the edit.</summary>
    [AvaloniaFact]
    public void Escape_in_the_name_box_cancels_the_rename_and_leaves_the_window_open()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.ShowAt(new ScreenPoint(0, 0));
            Frames.Settle();

            var work = vm.Rows.OfType<ProjectSectionViewModel>().Single();
            work.RenameCommand.Execute(null);
            Frames.Settle();

            var box = NameBoxOf(window, work);
            Assert.True(box.IsFocused);

            window.KeyTextInput("Typo");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Frames.Settle();

            Assert.False(work.IsEditing);
            Assert.Equal("Work", work.Name);
            Assert.True(window.IsVisible);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Dragging_a_project_header_onto_a_section_header_moves_it_into_the_section()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var work = vm.Rows.OfType<ProjectSectionViewModel>().Single();
            var loose = vm.Groups.Single(g => g.Repository == "mbKalkan/GitAlert");

            var grip = Centre(HeaderOf(window, loose), window);
            var target = SectionHeaderOf(window, work);
            var drop = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height * 0.7), window)!.Value;

            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(grip + new Vector(0, 8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
            Frames.Settle();

            Assert.True(loose.IsBeingDragged);
            Assert.Equal(DropMarker.Into, work.DropMarker);

            window.MouseUp(drop, MouseButton.Left);
            Frames.Settle();

            Assert.True(work.Contains("mbKalkan/GitAlert"));
            Assert.Equal(DropMarker.None, work.DropMarker);
            Assert.Equal([work, loose, vm.Groups.Single(g => g.Repository == "acme/api-gateway")], vm.Rows);
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Collapse_all_and_expand_all_fold_and_unfold_everything_from_above_the_list()
    {
        var (window, vm, dispose) = Build();

        try
        {
            window.Show();
            Frames.Settle();

            var work = vm.Rows.OfType<ProjectSectionViewModel>().Single();

            Click(window, ToolbarButton(window, "Collapse all"));

            Assert.All(vm.Groups, g => Assert.False(g.IsExpanded));
            Assert.False(work.IsExpanded);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.DataContext is AlertViewModel && b.IsEffectivelyVisible);

            Click(window, ToolbarButton(window, "Expand all"));

            Assert.All(vm.Groups, g => Assert.True(g.IsExpanded));
            Assert.True(work.IsExpanded);
            Assert.Equal(3, window.GetVisualDescendants().OfType<Button>().Count(b => b.DataContext is AlertViewModel && b.IsEffectivelyVisible));
        }
        finally
        {
            dispose();
        }
    }

    [AvaloniaFact]
    public void Dragging_a_section_header_above_another_section_moves_it_there_with_its_projects()
    {
        var (window, vm, dispose) = Build(settings => settings.Sections =
        [
            new ProjectSection { Name = "Work", Repositories = ["acme/api-gateway"] },
            new ProjectSection { Name = "Personal", Repositories = ["mbKalkan/GitAlert"] },
        ]);

        try
        {
            window.Show();
            Frames.Settle();

            var work = vm.Rows.OfType<ProjectSectionViewModel>().First(s => s.Name == "Work");
            var personal = vm.Rows.OfType<ProjectSectionViewModel>().First(s => s.Name == "Personal");
            var wasOpen = personal.IsExpanded;

            var grip = Centre(SectionHeaderOf(window, personal), window);
            var target = SectionHeaderOf(window, work);
            var drop = target.TranslatePoint(new Point(target.Bounds.Width / 2, 2), window)!.Value;

            window.MouseDown(grip, MouseButton.Left);
            window.MouseMove(grip + new Vector(0, -8), RawInputModifiers.LeftMouseButton);
            window.MouseMove(drop, RawInputModifiers.LeftMouseButton);
            Frames.Settle();

            Assert.True(personal.IsBeingDragged, "the header past the threshold is in the air");
            Assert.Equal(DropMarker.Above, work.DropMarker);

            window.MouseUp(drop, MouseButton.Left);
            Frames.Settle();

            Assert.Equal(["Personal", "Work"], vm.Rows.OfType<ProjectSectionViewModel>().Select(s => s.Name));
            Assert.Equal(["mbKalkan/GitAlert", "acme/api-gateway"], vm.Groups.Select(g => g.Repository));
            Assert.False(personal.IsBeingDragged);
            Assert.Equal(DropMarker.None, work.DropMarker);
            Assert.Equal(wasOpen, personal.IsExpanded);
        }
        finally
        {
            dispose();
        }
    }

    private static (FlyoutWindow Window, FlyoutViewModel ViewModel, Action Dispose) Build(Action<AppSettings>? shape = null)
    {
        var work = SampleData.NewWorkDir();
        var account = GitHubAccount.Create("mbKalkan");
        var settings = SampleData.Settings(account, sectioned: true);
        shape?.Invoke(settings);

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

    private static void Click(FlyoutWindow window, Visual control)
    {
        var at = Centre(control, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Frames.Settle();
    }

    private static Button ToolbarButton(FlyoutWindow window, string content) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);

    private static List<Button> HeadersOf(FlyoutWindow window, ProjectGroupViewModel project) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "Header" && ReferenceEquals(b.DataContext, project)).ToList();

    private static Button HeaderOf(FlyoutWindow window, ProjectGroupViewModel project) => HeadersOf(window, project).Single();

    private static Button SectionHeaderOf(FlyoutWindow window, ProjectSectionViewModel section) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SectionHeader" && ReferenceEquals(b.DataContext, section));

    private static TextBox NameBoxOf(FlyoutWindow window, ProjectSectionViewModel section) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SectionNameBox" && ReferenceEquals(t.DataContext, section));

    private static Point Centre(Visual control, FlyoutWindow window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
}
