using System.IO;
using GitAlert.Core;
using GitAlert.Services;
using Xunit;

namespace GitAlert.Tests;

public class AlertStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gitalert-tests-{Guid.NewGuid():N}.json");

    private static Alert Make(string id, int minutesAgo = 0, bool read = false) => new()
    {
        Id = id,
        Kind = AlertKind.Push,
        Title = $"Alert {id}",
        Repository = "acme/api-gateway",
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        IsRead = read,
    };

    private static Alert In(string id, string repository) => new()
    {
        Id = id,
        Kind = AlertKind.Push,
        Title = $"Alert {id}",
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// <c>required</c> only guards against a property that is missing. An explicit null in a
    /// hand-edited history file walks straight through, and the first thing done with a loaded
    /// alert is to read its id - which used to be the end of startup.
    /// </summary>
    [Fact]
    public void A_history_entry_that_lost_its_id_is_skipped_rather_than_taking_the_load_down()
    {
        File.WriteAllText(
            _path,
            """
            [
              {"id":null,"kind":"Push","title":"Nameless","repository":"acme/api-gateway","timestamp":"2026-01-01T00:00:00Z"},
              {"id":"acc|event:2","kind":"Push","title":"Homeless","repository":null,"timestamp":"2026-01-01T00:00:00Z"},
              {"id":"acc|event:1","kind":"Push","title":"Alert 1","repository":"acme/api-gateway","timestamp":"2026-01-01T00:00:00Z"}
            ]
            """);

        var store = new AlertStore(_path);
        store.Load();

        Assert.Equal(["acc|event:1"], store.Snapshot.Select(a => a.Id));
        Assert.Equal(0, store.RemoveUnwatched(["acme/api-gateway"]));
    }

    /// <summary>
    /// Removing a repository in settings has to take its alerts with it. Left behind, they kept
    /// the project in the list with a count beside it for something just removed.
    /// </summary>
    [Fact]
    public void Alerts_about_a_repository_that_is_no_longer_watched_are_dropped()
    {
        var store = new AlertStore(_path);

        store.Add(
        [
            In("acc|event:1", "acme/api-gateway"),
            In("acc|event:2", "acme/dropped"),
            In("acc|event:3", "acme/dropped"),
        ]);

        var removed = store.RemoveUnwatched(["acme/api-gateway"]);

        Assert.Equal(2, removed);
        Assert.Equal(["acc|event:1"], store.Snapshot.Select(a => a.Id));
    }

    /// <summary>
    /// The inbox is not the watch list. Somebody mentioning you in a repository you never
    /// watched is still worth keeping, and dropping it would empty the inbox on every save.
    /// </summary>
    [Fact]
    public void An_inbox_alert_survives_even_for_a_repository_that_is_not_watched()
    {
        var store = new AlertStore(_path);

        store.Add(
        [
            In("acc|inbox:99:1700000000", "stranger/project"),
            In("acc|event:2", "stranger/project"),
        ]);

        Assert.Equal(1, store.RemoveUnwatched(["acme/api-gateway"]));
        Assert.Equal(["acc|inbox:99:1700000000"], store.Snapshot.Select(a => a.Id));
    }

    [Fact]
    public void Watching_is_matched_the_way_github_writes_it_rather_than_case_sensitively()
    {
        var store = new AlertStore(_path);
        store.Add([In("acc|event:1", "Acme/API-Gateway")]);

        Assert.Equal(0, store.RemoveUnwatched(["acme/api-gateway"]));
        Assert.Single(store.Snapshot);
    }

    [Fact]
    public void Reading_an_alert_lowers_the_unread_count()
    {
        var store = new AlertStore(_path);
        store.Add([Make("a"), Make("b"), Make("c")]);

        Assert.Equal(3, store.UnreadCount);

        store.MarkRead("b");
        Assert.Equal(2, store.UnreadCount);

        store.MarkAllRead();
        Assert.Equal(0, store.UnreadCount);
    }

    [Fact]
    public void Add_returns_only_what_was_new()
    {
        var store = new AlertStore(_path);

        var first = store.Add([Make("a"), Make("b")]);
        var second = store.Add([Make("b"), Make("c")]);

        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Equal("c", second[0].Id);
    }

    [Fact]
    public void An_alert_seen_before_is_never_announced_again()
    {
        var store = new AlertStore(_path);

        store.Add([Make("a")]);
        Assert.Empty(store.Add([Make("a")]));
    }

    [Fact]
    public void The_list_is_ordered_newest_first()
    {
        var store = new AlertStore(_path);
        store.Add([Make("old", minutesAgo: 60), Make("new", minutesAgo: 1), Make("mid", minutesAgo: 30)]);

        Assert.Equal(["new", "mid", "old"], store.Snapshot.Select(a => a.Id));
    }

    [Fact]
    public void History_is_trimmed_to_the_configured_size()
    {
        var store = new AlertStore(_path) { MaxHistory = 20 };

        store.Add(Enumerable.Range(0, 100).Select(i => Make($"a{i}", minutesAgo: i)));

        Assert.Equal(20, store.Snapshot.Count);
        Assert.Equal("a0", store.Snapshot[0].Id);
    }

    [Fact]
    public void Unread_count_tracks_reads()
    {
        var store = new AlertStore(_path);
        store.Add([Make("a"), Make("b"), Make("c", read: true)]);

        Assert.Equal(2, store.UnreadCount);

        store.MarkRead("a");
        Assert.Equal(1, store.UnreadCount);

        store.MarkAllRead();
        Assert.Equal(0, store.UnreadCount);
    }

    [Fact]
    public void Clearing_the_history_does_not_resurrect_the_alerts_on_the_next_poll()
    {
        var store = new AlertStore(_path);
        store.Add([Make("a")]);
        store.Clear();

        Assert.Empty(store.Snapshot);
        Assert.Empty(store.Add([Make("a")]));
    }

    [Fact]
    public void History_survives_a_restart()
    {
        var store = new AlertStore(_path);
        store.Add([Make("a"), Make("b", read: true)]);
        store.Save();

        var reloaded = new AlertStore(_path);
        reloaded.Load();

        Assert.Equal(2, reloaded.Snapshot.Count);
        Assert.Equal(1, reloaded.UnreadCount);
        Assert.Empty(reloaded.Add([Make("a")]));
    }

    [Fact]
    public void A_corrupt_history_file_starts_empty_instead_of_throwing()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = new AlertStore(_path);
        store.Load();

        Assert.Empty(store.Snapshot);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        GC.SuppressFinalize(this);
    }
}
