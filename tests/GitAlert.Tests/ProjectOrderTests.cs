using System.IO;
using System.Net.Http;
using System.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Dragging a project to a new place in the list. The drop lands it directly above or below the
/// project it was dropped on, in the total order - the one that is saved - not just among the
/// projects that happen to be on screen.
/// </summary>
public class ProjectOrderTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void Dropping_a_project_above_another_puts_it_directly_above()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Alert("1", "acme/alpha"), Alert("2", "acme/beta"), Alert("3", "acme/gamma"));

            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma"], Order(flyout));

            flyout.PlaceProject(Group(flyout, "acme/gamma"), Group(flyout, "acme/alpha"), above: true);

            Assert.Equal(["acme/gamma", "acme/alpha", "acme/beta"], Order(flyout));
            Assert.Equal(["acme/gamma", "acme/alpha", "acme/beta"], shell.SavedOrder);
        });
    }

    [Fact]
    public void Dropping_a_project_below_the_last_one_puts_it_last()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Alert("1", "acme/alpha"), Alert("2", "acme/beta"), Alert("3", "acme/gamma"));

            flyout.PlaceProject(Group(flyout, "acme/alpha"), Group(flyout, "acme/gamma"), above: false);

            Assert.Equal(["acme/beta", "acme/gamma", "acme/alpha"], Order(flyout));
            Assert.Equal(["acme/beta", "acme/gamma", "acme/alpha"], shell.SavedOrder);
        });
    }

    [Fact]
    public void Dropping_a_project_on_itself_changes_nothing_and_saves_nothing()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Alert("1", "acme/alpha"), Alert("2", "acme/beta"));

            flyout.PlaceProject(Group(flyout, "acme/beta"), Group(flyout, "acme/beta"), above: true);

            Assert.Equal(["acme/alpha", "acme/beta"], Order(flyout));
            Assert.Null(shell.SavedOrder);
        });
    }

    /// <summary>
    /// With "unread only" on, a project with nothing unread is not on screen. Dragging past it
    /// must still put the moved project where the drop said, and the hidden one must keep the
    /// place it had rather than falling to the end when the list is shown in full again.
    /// </summary>
    [Fact]
    public void A_hidden_project_keeps_its_place_while_others_are_dragged_around_it()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(
                shell,
                Alert("1", "acme/alpha"),
                Alert("2", "acme/beta", read: true),
                Alert("3", "acme/gamma"));

            flyout.ToggleUnreadOnlyCommand.Execute(null);
            Assert.Equal(["acme/alpha", "acme/gamma"], Order(flyout));

            flyout.PlaceProject(Group(flyout, "acme/gamma"), Group(flyout, "acme/alpha"), above: true);

            Assert.Equal(["acme/gamma", "acme/alpha"], Order(flyout));
            Assert.Equal(["acme/gamma", "acme/alpha", "acme/beta"], shell.SavedOrder);

            flyout.ToggleUnreadOnlyCommand.Execute(null);
            Assert.Equal(["acme/gamma", "acme/alpha", "acme/beta"], Order(flyout));
        });
    }

    [Fact]
    public void Clearing_the_drag_markers_takes_every_line_down()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Alert("1", "acme/alpha"), Alert("2", "acme/beta"));

            Group(flyout, "acme/alpha").IsBeingDragged = true;
            Group(flyout, "acme/beta").DropMarker = DropMarker.Below;

            flyout.ClearDragMarkers();

            Assert.All(flyout.Groups, g => Assert.False(g.IsBeingDragged));
            Assert.All(flyout.Groups, g => Assert.Equal(DropMarker.None, g.DropMarker));
        });
    }

    // ---- Plumbing ----------------------------------------------------------

    private static List<string> Order(FlyoutViewModel flyout) => [.. flyout.Groups.Select(g => g.Repository)];

    private static ProjectGroupViewModel Group(FlyoutViewModel flyout, string repository) =>
        flyout.Groups.Single(g => g.Repository == repository);

    private static Alert Alert(string id, string repository, bool read = false) => new()
    {
        Id = $"account|event:{id}",
        Kind = AlertKind.Issue,
        Title = $"Alert {id}",
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
        IsRead = read,
    };

    private FlyoutViewModel Build(IShellCommands shell, params Alert[] alerts)
    {
        var store = new AlertStore(NewFile());
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        return new FlyoutViewModel(store, monitor, shell, new AppSettings());
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-order-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    private sealed class RecordingShell : IShellCommands
    {
        public List<string>? SavedOrder { get; private set; }

        public void ShowSettings()
        {
        }

        public void HideFlyout()
        {
        }

        public void Quit()
        {
        }

        public void SaveListPreferences(ListPreferences preferences) =>
            SavedOrder = [.. preferences.ProjectOrder];

        public void UnreadChanged()
        {
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        foreach (var file in _files.Where(File.Exists))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
