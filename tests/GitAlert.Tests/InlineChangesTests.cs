using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The changed files unfold under the alert itself, in the list, and the pane beside the list
/// holds only the diff. What the card does: it opens, it folds on a second click, it says what
/// it is doing on one line, and a long list waits behind a click rather than pushing every other
/// project off the bottom.
/// </summary>
public class InlineChangesTests : IDisposable
{
    private readonly List<string> _files = [];
    private readonly List<MonitorService> _monitors = [];

    [Fact]
    public void Clicking_the_open_alert_again_folds_it()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Alert("a"), Alert("b"));
            var a = Find(flyout, "a");
            var b = Find(flyout, "b");

            Select(flyout, a);

            Assert.True(a.IsSelected);
            Assert.Same(a, flyout.SelectedAlert);
            Assert.True(flyout.Detail.HasSelection);

            Select(flyout, a);

            Assert.False(a.IsSelected);
            Assert.Null(flyout.SelectedAlert);
            Assert.False(flyout.Detail.HasSelection);

            // Folding it does not unread it.
            Assert.True(a.IsRead);

            Select(flyout, a);
            Select(flyout, b);

            Assert.False(a.IsSelected);
            Assert.True(b.IsSelected);
            Assert.Same(b, flyout.Detail.Alert);
        });
    }

    /// <summary>An issue has nothing to fetch, so the card says so and offers no reload.</summary>
    [Fact]
    public void An_alert_without_a_diff_says_so_on_its_card()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Alert("a"));

            Select(flyout, Find(flyout, "a"));

            Assert.Equal("No changed files", flyout.Detail.Caption);
            Assert.False(flyout.Detail.CanReload);
            Assert.True(flyout.Detail.HasNotice);
            Assert.Empty(flyout.Detail.Files);
        });
    }

    [Fact]
    public async Task A_long_change_list_shows_its_first_thirty_and_the_rest_on_request()
    {
        var detail = await ShowCommitTouchingAsync(45);

        Assert.Equal(AlertDetailViewModel.InlineLimit, detail.Files.Count);
        Assert.Equal(15, detail.HiddenFileCount);
        Assert.True(detail.HasHiddenFiles);
        Assert.Equal("Show all 45 files", detail.ShowAllFilesLabel);

        // The count on the card is the whole change, not the part on screen.
        Assert.Equal("45 files changed  ·  +45  -0", detail.Summary);
        Assert.Equal(detail.Summary, detail.Caption);
        Assert.True(detail.CanReload);

        var shown = detail.Files.ToList();
        var picked = detail.SelectedFile;

        detail.ShowAllFilesCommand.Execute(null);

        Assert.Equal(45, detail.Files.Count);
        Assert.False(detail.HasHiddenFiles);

        // The rows already on screen are the same rows: nothing was rebuilt under the pointer.
        Assert.Equal(shown, detail.Files.Take(AlertDetailViewModel.InlineLimit));
        Assert.Same(picked, detail.SelectedFile);
    }

    [Fact]
    public async Task A_short_change_list_holds_nothing_back()
    {
        var detail = await ShowCommitTouchingAsync(4);

        Assert.Equal(4, detail.Files.Count);
        Assert.False(detail.HasHiddenFiles);
        Assert.Equal("4 files changed  ·  +4  -0", detail.Caption);
    }

    /// <summary>Moving to another alert forgets the held-back tail along with everything else.</summary>
    [Fact]
    public async Task Leaving_the_alert_clears_the_held_back_files_too()
    {
        var detail = await ShowCommitTouchingAsync(45);

        await detail.ShowAsync(null);

        Assert.Empty(detail.Files);
        Assert.False(detail.HasHiddenFiles);
        Assert.Equal(string.Empty, detail.Caption);
        Assert.False(detail.CanReload);
    }

    // ---- Plumbing ----------------------------------------------------------

    private static AlertViewModel Find(FlyoutViewModel flyout, string id) =>
        flyout.Groups.SelectMany(g => g.Items).Single(a => a.Model.Id.EndsWith(id, StringComparison.Ordinal));

    /// <summary>
    /// Clicks a card. Alerts with nothing to diff finish without ever awaiting, so nothing needs
    /// a message pump to run here.
    /// </summary>
    private static void Select(FlyoutViewModel flyout, AlertViewModel alert)
    {
        var pending = flyout.SelectAlertCommand.ExecuteAsync(alert);

        Assert.True(pending.IsCompleted, "selecting an alert with no diff should not need a message pump");
        pending.GetAwaiter().GetResult();
    }

    private static Alert Alert(string id) => new()
    {
        Id = $"account|event:{id}",
        Kind = AlertKind.Issue,
        Title = $"Alert {id}",
        Repository = "acme/api-gateway",
        Timestamp = DateTimeOffset.UtcNow,
    };

    private FlyoutViewModel Build(params Alert[] alerts)
    {
        var store = new AlertStore(NewFile());
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));
        _monitors.Add(monitor);

        return new FlyoutViewModel(store, monitor, new SilentShell(), new AppSettings());
    }

    /// <summary>A shell that does nothing: none of this is about what the tray does.</summary>
    private sealed class SilentShell : IShellCommands
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

        public void SaveListPreferences(ListPreferences preferences)
        {
        }

        public void UnreadChanged()
        {
        }
    }

    /// <summary>A detail pane showing a single commit that touched a given number of files.</summary>
    private async Task<AlertDetailViewModel> ShowCommitTouchingAsync(int fileCount)
    {
        var account = GitHubAccount.Create("octocat");
        var settings = new AppSettings { Accounts = [account] };

        var handler = new StubHandler(request =>
            request.Path.EndsWith("/commits/abc123", StringComparison.Ordinal)
                ? Responses.Ok(CommitTouching(fileCount))
                : Responses.Ok("[]"));

        var store = new AlertStore(NewFile());
        var monitor = new MonitorService(store, new StateStore(NewFile()), new HttpClient(handler));
        _monitors.Add(monitor);
        monitor.Configure(settings, new Dictionary<string, string> { [account.Id] = "ghp_test" });

        var detail = new AlertDetailViewModel(monitor);

        var alert = new AlertViewModel(new Alert
        {
            Id = $"{account.Id}|commit:abc123",
            Kind = AlertKind.Push,
            Title = "Commit abc123",
            Repository = "acme/api-gateway",
            AccountId = account.Id,
            DiffHead = "abc123",
            Timestamp = DateTimeOffset.UtcNow,
        });

        await detail.ShowAsync(alert);

        Assert.False(detail.HasError, detail.Error);

        return detail;
    }

    private static string CommitTouching(int fileCount)
    {
        var body = new StringBuilder("""{"sha":"abc123","files":[""");

        for (var i = 0; i < fileCount; i++)
        {
            if (i > 0)
            {
                body.Append(',');
            }

            body.Append($$"""{"filename":"src/File{{i}}.cs","status":"modified","additions":1,"deletions":0,"changes":1,"patch":"@@ -1 +1 @@\n-a\n+b"}""");
        }

        return body.Append("]}").ToString();
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-inline-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var monitor in _monitors)
        {
            monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A temp file that will not go is not worth failing a test over.
            }
        }
    }
}
