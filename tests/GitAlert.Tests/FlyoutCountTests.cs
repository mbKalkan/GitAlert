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
/// Everything in the window that shows a number: the filter chips along the top, the badge beside
/// each project, the line in the header, and the tray icon behind it all.
/// </summary>
/// <remarks>
/// They are four separate counters over the same alerts, and each was recomputed by a different
/// event. Reading an alert changes all four and rearranges none of them, so it went through the
/// one path that recomputed nothing - and the numbers sat there while the list under them emptied.
/// These tests are written together on purpose: fixing one counter and leaving the next is exactly
/// what happened, twice.
/// </remarks>
public class FlyoutCountTests : IDisposable
{
    private readonly List<string> _files = [];

    // ---- The chips along the top -------------------------------------------

    [Fact]
    public void The_chip_counts_start_from_what_is_unread()
    {
        OnStaThread(() =>
        {
            var flyout = Build(Alert("a", AlertKind.Issue), Alert("b", AlertKind.Issue), Alert("c", AlertKind.Push));

            Assert.Equal(3, Chip(flyout, AlertFilter.All).Count);
            Assert.Equal(2, Chip(flyout, AlertFilter.Issues).Count);
            Assert.Equal(1, Chip(flyout, AlertFilter.Push).Count);
            Assert.Equal(0, Chip(flyout, AlertFilter.Ci).Count);
        });
    }

    /// <summary>The one the user pointed at: the chips went on showing the old numbers.</summary>
    [Fact]
    public void Reading_an_alert_lowers_its_chip_and_leaves_the_others_alone()
    {
        OnStaThread(() =>
        {
            var flyout = Build(Alert("a", AlertKind.Issue), Alert("b", AlertKind.Issue), Alert("c", AlertKind.Push));

            Read(flyout, "a");

            Assert.Equal(2, Chip(flyout, AlertFilter.All).Count);
            Assert.Equal(1, Chip(flyout, AlertFilter.Issues).Count);
            Assert.Equal(1, Chip(flyout, AlertFilter.Push).Count);
        });
    }

    /// <summary>
    /// A chip with nothing unread behind it drops its badge rather than showing a nought, which
    /// is what HasCount is for.
    /// </summary>
    [Fact]
    public void The_last_alert_in_a_category_takes_the_badge_with_it()
    {
        OnStaThread(() =>
        {
            var flyout = Build(Alert("a", AlertKind.Issue), Alert("c", AlertKind.Push));

            Assert.True(Chip(flyout, AlertFilter.Issues).HasCount);

            Read(flyout, "a");

            Assert.Equal(0, Chip(flyout, AlertFilter.Issues).Count);
            Assert.False(Chip(flyout, AlertFilter.Issues).HasCount);
            Assert.True(Chip(flyout, AlertFilter.Push).HasCount);
        });
    }

    [Fact]
    public void Marking_everything_read_empties_every_chip()
    {
        OnStaThread(() =>
        {
            var flyout = Build(Alert("a", AlertKind.Issue), Alert("b", AlertKind.Push), Alert("c", AlertKind.Workflow));

            flyout.MarkAllReadCommand.Execute(null);

            Assert.All(flyout.Filters, chip => Assert.Equal(0, chip.Count));
            Assert.All(flyout.Filters, chip => Assert.False(chip.HasCount));
        });
    }

    [Fact]
    public void Reading_the_same_alert_twice_does_not_count_it_twice()
    {
        OnStaThread(() =>
        {
            var flyout = Build(Alert("a", AlertKind.Issue), Alert("b", AlertKind.Issue));

            Read(flyout, "a");
            Read(flyout, "a");

            Assert.Equal(1, Chip(flyout, AlertFilter.All).Count);
            Assert.Equal(1, flyout.UnreadCount);
        });
    }

    // ---- All four counters at once -----------------------------------------

    /// <summary>
    /// The point of the whole file: one read has to move every number that describes it, and the
    /// tray has to be told, because it is drawn outside this window entirely.
    /// </summary>
    [Fact]
    public void One_read_moves_the_chip_the_project_badge_the_header_and_the_tray()
    {
        OnStaThread(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Alert("a", AlertKind.Issue), Alert("b", AlertKind.Issue));

            var project = Assert.Single(flyout.Groups);
            Assert.Equal(2, project.UnreadCount);
            Assert.Equal(2, flyout.UnreadCount);
            Assert.Equal(2, Chip(flyout, AlertFilter.All).Count);

            var toldBefore = shell.UnreadChangedCalls;

            Read(flyout, "a");

            Assert.Equal(1, Chip(flyout, AlertFilter.All).Count);
            Assert.Equal(1, project.UnreadCount);
            Assert.Equal("1 unread alert", flyout.UnreadText);
            Assert.Equal(toldBefore + 1, shell.UnreadChangedCalls);
        });
    }

    [Fact]
    public void Marking_everything_read_moves_all_four_as_well()
    {
        OnStaThread(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Alert("a", AlertKind.Issue), Alert("b", AlertKind.Push));

            var project = Assert.Single(flyout.Groups);
            var toldBefore = shell.UnreadChangedCalls;

            flyout.MarkAllReadCommand.Execute(null);

            Assert.Equal(0, Chip(flyout, AlertFilter.All).Count);
            Assert.Equal(0, project.UnreadCount);
            Assert.False(project.HasUnread);
            Assert.False(flyout.HasUnread);
            Assert.Equal("No unread alerts", flyout.UnreadText);
            Assert.Equal(toldBefore + 1, shell.UnreadChangedCalls);
        });
    }

    /// <summary>
    /// Two projects, and reading in one must not touch the other's badge - the counters are per
    /// project and it would be easy to recount the wrong one, or all of them.
    /// </summary>
    [Fact]
    public void Reading_in_one_project_leaves_the_other_project_alone()
    {
        OnStaThread(() =>
        {
            var flyout = Build(
                Alert("a", AlertKind.Issue, repository: "acme/api-gateway"),
                Alert("b", AlertKind.Issue, repository: "acme/web"));

            var api = flyout.Groups.Single(g => g.Repository == "acme/api-gateway");
            var web = flyout.Groups.Single(g => g.Repository == "acme/web");

            Read(flyout, "a");

            Assert.Equal(0, api.UnreadCount);
            Assert.Equal(1, web.UnreadCount);
        });
    }

    /// <summary>
    /// Reading is not rearranging. A row that vanishes from under the pointer as it is clicked is
    /// hostile, so hiding read alerts takes effect the next time the list is actually rebuilt.
    /// </summary>
    [Fact]
    public void Reading_an_alert_does_not_pull_the_row_out_from_under_the_pointer()
    {
        OnStaThread(() =>
        {
            var flyout = Build(Alert("a", AlertKind.Issue), Alert("b", AlertKind.Issue));

            flyout.ToggleUnreadOnlyCommand.Execute(null);
            Assert.True(flyout.UnreadOnly);

            var project = Assert.Single(flyout.Groups);
            Assert.Equal(2, project.Items.Count);

            Read(flyout, "a");

            Assert.Equal(2, project.Items.Count);
            Assert.Equal(1, project.UnreadCount);
        });
    }

    // ---- Plumbing ----------------------------------------------------------

    private static FilterChipViewModel Chip(FlyoutViewModel flyout, AlertFilter filter) =>
        flyout.Filters.Single(c => c.Filter == filter);

    /// <summary>
    /// Selects an alert, which is what reading one is. Alerts with nothing to diff finish without
    /// ever awaiting, so nothing needs a message pump to run here.
    /// </summary>
    private static void Read(FlyoutViewModel flyout, string id)
    {
        var alert = flyout.Groups.SelectMany(g => g.Items).Single(a => a.Model.Id.EndsWith(id, StringComparison.Ordinal));

        var pending = flyout.SelectAlertCommand.ExecuteAsync(alert);

        Assert.True(pending.IsCompleted, "selecting an alert with no diff should not need a message pump");
        pending.GetAwaiter().GetResult();
    }

    private static Alert Alert(string id, AlertKind kind, string repository = "acme/api-gateway") => new()
    {
        Id = $"account|event:{id}",
        Kind = kind,
        Title = $"Alert {id}",
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private FlyoutViewModel Build(params Alert[] alerts) => Build(new RecordingShell(), alerts);

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
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-flyout-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <summary>The shell, only so far as the window is allowed to talk to it.</summary>
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

    /// <summary>The window's view models are WPF objects, so they get a thread WPF is happy on.</summary>
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
