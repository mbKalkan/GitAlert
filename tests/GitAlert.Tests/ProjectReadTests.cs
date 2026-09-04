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
/// The tick on a project header, which reads everything the project is showing at once. It is
/// "mark all read" for one project: the same numbers have to move, the store has to be told, and
/// the projects either side have to be left alone.
/// </summary>
public class ProjectReadTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void Reading_a_project_reads_every_row_in_it_and_none_in_the_next()
    {
        OnStaThread(() =>
        {
            var built = Build(
                Alert("1", "acme/alpha"),
                Alert("2", "acme/alpha", AlertKind.Push),
                Alert("3", "acme/beta"));

            Group(built.Flyout, "acme/alpha").MarkReadCommand.Execute(null);

            Assert.All(Group(built.Flyout, "acme/alpha").Items, row => Assert.True(row.IsRead));
            Assert.Equal(0, Group(built.Flyout, "acme/alpha").UnreadCount);
            Assert.Equal(1, Group(built.Flyout, "acme/beta").UnreadCount);
            Assert.False(Group(built.Flyout, "acme/beta").Items.Single().IsRead);
        });
    }

    [Fact]
    public void Reading_a_project_is_written_to_disk()
    {
        OnStaThread(() =>
        {
            var built = Build(Alert("1", "acme/alpha"), Alert("2", "acme/alpha"), Alert("3", "acme/beta"));

            Group(built.Flyout, "acme/alpha").MarkReadCommand.Execute(null);

            var reloaded = new AlertStore(built.HistoryPath);
            reloaded.Load();

            Assert.Equal(1, reloaded.UnreadCount);
            Assert.Equal("acme/beta", reloaded.Snapshot.Single(a => !a.IsRead).Repository);
        });
    }

    /// <summary>
    /// The same four counters a single read moves: the chips, the badge, the footer line, and
    /// the tray icon drawn outside this window.
    /// </summary>
    [Fact]
    public void Reading_a_project_moves_every_counter_and_tells_the_tray()
    {
        OnStaThread(() =>
        {
            var built = Build(
                Alert("1", "acme/alpha"),
                Alert("2", "acme/alpha", AlertKind.Push),
                Alert("3", "acme/beta"));

            var toldBefore = built.Shell.UnreadChangedCalls;

            Group(built.Flyout, "acme/alpha").MarkReadCommand.Execute(null);

            Assert.Equal(1, built.Flyout.UnreadCount);
            Assert.Equal("1 unread alert", built.Flyout.UnreadText);
            Assert.Equal(1, Chip(built.Flyout, AlertFilter.All).Count);
            Assert.Equal(1, Chip(built.Flyout, AlertFilter.Issues).Count);
            Assert.Equal(0, Chip(built.Flyout, AlertFilter.Push).Count);
            Assert.Equal(toldBefore + 1, built.Shell.UnreadChangedCalls);
        });
    }

    /// <summary>
    /// With a kind picked, the badge counts that kind alone, so the tick clears the number it is
    /// next to and leaves the rows the filter is hiding for later.
    /// </summary>
    [Fact]
    public void With_a_kind_picked_the_tick_reads_only_that_kind()
    {
        OnStaThread(() =>
        {
            var built = Build(
                Alert("1", "acme/alpha", AlertKind.PullRequest),
                Alert("2", "acme/alpha", AlertKind.Issue));

            built.Flyout.SelectFilterCommand.Execute(Chip(built.Flyout, AlertFilter.PullRequests));
            Assert.Single(Group(built.Flyout, "acme/alpha").Items);

            Group(built.Flyout, "acme/alpha").MarkReadCommand.Execute(null);

            Assert.False(Group(built.Flyout, "acme/alpha").HasUnread);
            Assert.Equal(1, built.Flyout.UnreadCount);
            Assert.Equal(1, Chip(built.Flyout, AlertFilter.Issues).Count);

            built.Flyout.SelectFilterCommand.Execute(Chip(built.Flyout, AlertFilter.All));
            Assert.Equal(1, Group(built.Flyout, "acme/alpha").UnreadCount);
        });
    }

    [Fact]
    public void A_project_with_nothing_unread_does_nothing_and_tells_nobody()
    {
        OnStaThread(() =>
        {
            var built = Build(Alert("1", "acme/alpha", read: true), Alert("2", "acme/beta"));
            var toldBefore = built.Shell.UnreadChangedCalls;

            Group(built.Flyout, "acme/alpha").MarkReadCommand.Execute(null);

            Assert.Equal(1, built.Flyout.UnreadCount);
            Assert.Equal(toldBefore, built.Shell.UnreadChangedCalls);
        });
    }

    /// <summary>
    /// Reading is not rearranging, for a project as much as for a row: with "unread only" on, the
    /// project stays where the pointer is until the list is next rebuilt.
    /// </summary>
    [Fact]
    public void Reading_a_project_does_not_pull_it_out_from_under_the_pointer()
    {
        OnStaThread(() =>
        {
            var built = Build(Alert("1", "acme/alpha"), Alert("2", "acme/alpha"), Alert("3", "acme/beta"));

            built.Flyout.ToggleUnreadOnlyCommand.Execute(null);
            Assert.True(built.Flyout.UnreadOnly);

            Group(built.Flyout, "acme/alpha").MarkReadCommand.Execute(null);

            Assert.Equal(2, Group(built.Flyout, "acme/alpha").Items.Count);
            Assert.Equal(0, Group(built.Flyout, "acme/alpha").UnreadCount);
            Assert.Equal(["acme/alpha", "acme/beta"], built.Flyout.Groups.Select(g => g.Repository).ToList());
        });
    }

    // ---- Plumbing ----------------------------------------------------------

    private sealed record Built(FlyoutViewModel Flyout, RecordingShell Shell, string HistoryPath);

    private static ProjectGroupViewModel Group(FlyoutViewModel flyout, string repository) =>
        flyout.Groups.Single(g => g.Repository == repository);

    private static FilterChipViewModel Chip(FlyoutViewModel flyout, AlertFilter filter) =>
        flyout.Filters.Single(c => c.Filter == filter);

    private static Alert Alert(string id, string repository, AlertKind kind = AlertKind.Issue, bool read = false) => new()
    {
        Id = $"account|event:{id}",
        Kind = kind,
        Title = $"Alert {id}",
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
        IsRead = read,
    };

    private Built Build(params Alert[] alerts)
    {
        var historyPath = NewFile();
        var store = new AlertStore(historyPath);
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        var shell = new RecordingShell();
        return new Built(new FlyoutViewModel(store, monitor, shell, new AppSettings()), shell, historyPath);
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-read-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    private sealed class RecordingShell : IShellCommands
    {
        public int UnreadChangedCalls { get; private set; }

        public void ShowSettings()
        {
        }

        public void HideFlyout()
        {
        }

        public void Quit()
        {
        }

        public void SaveListPreferences(IReadOnlyList<string> projectOrder, bool unreadOnly)
        {
        }

        public void UnreadChanged() => UnreadChangedCalls++;
    }

    private static void OnStaThread(Action work)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
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
