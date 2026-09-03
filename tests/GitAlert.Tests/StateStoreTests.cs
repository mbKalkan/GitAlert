using System.IO;
using GitAlert.Services;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The sync state on its way back from disk. The poll loop dereferences everything in it without
/// a check, so whatever a load hands over has to be whole.
/// </summary>
public class StateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gitalert-state-{Guid.NewGuid():N}.json");

    [Fact]
    public void Nulls_in_a_hand_edited_file_are_dropped_rather_than_handed_to_the_poll()
    {
        File.WriteAllText(
            _path,
            """{"repositories":{"acc|acme/api":null,"acc|acme/web":{"lastEventId":5}},"inboxes":null}""");

        var state = new StateStore(_path).Load();

        Assert.Equal(5, state.For("acc|acme/web").LastEventId);
        Assert.Equal(0, state.For("acc|acme/api").LastEventId);
        Assert.Null(state.InboxFor("acc").HighWater);
    }

    /// <summary>
    /// The deserialiser builds its own dictionary with the default comparer, whatever the
    /// property was declared with. Before a restart the lookup was case-insensitive; after one
    /// it was not, and a repository re-added with different capitals re-baselined from scratch.
    /// </summary>
    [Fact]
    public void Repository_state_is_still_found_without_regard_to_case_after_a_reload()
    {
        var store = new StateStore(_path);
        var state = new MonitorState();
        state.For("acc|Acme/API").LastEventId = 7;
        store.Save(state);

        var loaded = store.Load();

        Assert.Equal(7, loaded.For("acc|acme/api").LastEventId);
        Assert.Single(loaded.Repositories);
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
