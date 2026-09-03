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
