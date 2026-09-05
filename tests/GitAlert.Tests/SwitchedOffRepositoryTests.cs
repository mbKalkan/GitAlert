using System.IO;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// A repository with its tick off in settings. It is not removed - its history waits for the tick
/// to come back - but while it is off nothing of it shows: no project in the list, no number
/// anywhere, no new alert about it.
/// </summary>
/// <remarks>
/// It used to stay in the list with its old alerts, on the grounds that a switched-off project is
/// still one the user watches. The user switched one off to make it go away, and it did not.
/// </remarks>
public class SwitchedOffRepositoryTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void Only_a_name_with_every_tick_off_counts_as_switched_off()
    {
        var settings = new AppSettings
        {
            Repositories =
            [
                new() { AccountId = "a", Owner = "acme", Name = "api-gateway", Enabled = false },
                new() { AccountId = "a", Owner = "acme", Name = "web", Enabled = true },
                new() { AccountId = "b", Owner = "acme", Name = "web", Enabled = false },
            ],
        };

        Assert.Equal(["acme/api-gateway"], settings.SwitchedOffRepositories);
        Assert.True(settings.IsSwitchedOff("ACME/API-Gateway"));
        Assert.False(settings.IsSwitchedOff("acme/web"));
    }

    [Fact]
    public void Hidden_alerts_leave_the_snapshot_and_the_count_but_not_the_file()
    {
        var path = NewFile();
        var store = new AlertStore(path);
        store.Add([Alert("a", "acme/api-gateway"), Alert("b", "acme/web"), Alert("c", "acme/web")]);

        store.Hide(["ACME/Web"]);

        Assert.Equal(["a"], store.Snapshot.Select(a => a.Title));
        Assert.Equal(1, store.UnreadCount);

        // Hidden is not gone: the file keeps all three, and a store that loads it shows all three.
        store.Save();
        var reloaded = new AlertStore(path);
        reloaded.Load();

        Assert.Equal(3, reloaded.Snapshot.Count);

        store.Hide([]);

        Assert.Equal(3, store.Snapshot.Count);
        Assert.Equal(3, store.UnreadCount);
    }

    [Fact]
    public async Task A_switched_off_project_leaves_the_list_and_comes_back_with_its_alerts()
    {
        var store = new AlertStore(NewFile());
        store.Add([Alert("a", "acme/api-gateway"), Alert("b", "acme/web")]);

        await using var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        using var flyout = new FlyoutViewModel(store, monitor, new NoShell(), new AppSettings());

        Assert.Equal(["acme/api-gateway", "acme/web"], flyout.Groups.Select(g => g.Repository));
        Assert.Equal(2, flyout.UnreadCount);

        // What the shell does when settings are saved with the tick off.
        store.Hide(["acme/web"]);
        flyout.Reload();

        Assert.Equal(["acme/api-gateway"], flyout.Groups.Select(g => g.Repository));
        Assert.Equal(1, flyout.UnreadCount);

        // And with the tick back on.
        store.Hide([]);
        flyout.Reload();

        Assert.Equal(["acme/api-gateway", "acme/web"], flyout.Groups.Select(g => g.Repository));
        Assert.Equal(2, flyout.UnreadCount);
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
                // A temp file that outlives the test is untidy, not a failure.
            }
        }
    }

    private static Alert Alert(string title, string repository) => new()
    {
        Id = $"acc|event:{title}",
        Kind = AlertKind.Push,
        Title = title,
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-switched-off-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <summary>The shell, only so far as the window is allowed to talk to it.</summary>
    private sealed class NoShell : IShellCommands
    {
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

        public void UnreadChanged()
        {
        }
    }
}
