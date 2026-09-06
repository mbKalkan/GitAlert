using System.IO;
using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The polling engine reading a project board. A board has no timeline, so the engine compares
/// readings; what it must never do is announce the whole board the moment it is added, miss a
/// card that moved, or let a board the token cannot read take the repositories down with it.
/// </summary>
public class BoardPollingTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public async Task The_first_reading_records_where_the_cards_stand_and_announces_nothing()
    {
        var github = new FakeBoard();
        github.Cards[13] = "Todo";
        await using var harness = NewHarness(github);

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal("Watching 1 repository and 1 board", status.Message);
        Assert.Empty(harness.Delivered);
        Assert.Contains(harness.Handler.Requests, r => r.Path == "/orgs/acme/projectsV2/12/fields");
        Assert.Contains(harness.Handler.Requests, r => r.Path == "/orgs/acme/projectsV2/12/items" && r.Query.Contains("fields=1,3"));
    }

    [Fact]
    public async Task A_card_that_moved_since_the_last_reading_is_announced_under_the_boards_name()
    {
        var github = new FakeBoard();
        github.Cards[13] = "Todo";
        await using var harness = NewHarness(github);

        await harness.PollAsync();
        github.Cards[13] = "In progress";
        github.Cards[14] = "Todo";
        await harness.PollAsync();

        Assert.Equal(2, harness.Delivered.Count);
        var moved = Assert.Single(harness.Delivered, a => a.Title == "Moved to In progress");
        Assert.Equal(AlertKind.Board, moved.Kind);
        Assert.Equal("acme/#12", moved.Repository);
        Assert.Equal("Status · Todo → In progress", moved.Note);
        Assert.Equal("#13 Card 13", moved.Detail);
        Assert.Contains(harness.Delivered, a => a.Title == "Added to Roadmap" && a.Detail == "#14 Card 14");

        // The fields are learned once, not on every reading.
        Assert.Single(harness.Handler.Requests, r => r.Path == "/orgs/acme/projectsV2/12/fields");
    }

    [Fact]
    public async Task The_second_reading_sends_back_the_tag_and_an_untouched_board_costs_nothing()
    {
        var github = new FakeBoard { ETag = "\"board-1\"" };
        github.Cards[13] = "Todo";
        await using var harness = NewHarness(github);

        await harness.PollAsync();
        await harness.PollAsync();

        var reads = harness.Handler.Requests.Where(r => r.Path == "/orgs/acme/projectsV2/12/items").ToList();
        Assert.Equal(2, reads.Count);
        Assert.Null(reads[0].IfNoneMatch);
        Assert.Equal("\"board-1\"", reads[1].IfNoneMatch);
        Assert.Empty(harness.Delivered);
    }

    [Fact]
    public async Task A_restart_does_not_replay_what_the_previous_run_had_already_seen()
    {
        var github = new FakeBoard();
        github.Cards[13] = "Todo";
        var account = GitHubAccount.Create("octocat");
        var state = NewFile();
        var history = NewFile();

        await using (var first = NewHarness(github, state, history, account: account))
        {
            await first.PollAsync();
        }

        github.Cards[13] = "Done";

        await using var second = NewHarness(github, state, history, account: account);
        await second.PollAsync();

        var alert = Assert.Single(second.Delivered);
        Assert.Equal("Moved to Done", alert.Title);
    }

    /// <summary>
    /// A token without the project scope is answered 404 by GitHub. That is one board the user
    /// has to fix, not a reason to stop reading the repositories beside it.
    /// </summary>
    [Fact]
    public async Task A_board_the_token_cannot_read_is_named_in_the_status_and_leaves_the_repositories_alone()
    {
        var github = new FakeBoard { Refuse = HttpStatusCode.NotFound };
        await using var harness = NewHarness(github);

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Warning, status.State);
        Assert.StartsWith("acme/Roadmap:", status.Message);
        Assert.Contains(harness.Handler.Requests, r => r.Path == "/repos/acme/api-gateway/events");
    }

    [Fact]
    public async Task A_board_with_its_tick_off_is_neither_read_nor_counted()
    {
        var github = new FakeBoard();
        await using var harness = NewHarness(github, configure: s => s.Boards[0].Enabled = false);

        var status = await harness.PollAsync();

        Assert.Equal("Watching 1 repository", status.Message);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.Path.Contains("projectsV2"));
    }

    [Fact]
    public async Task A_card_that_moved_on_a_board_with_its_kind_muted_is_not_delivered()
    {
        var github = new FakeBoard();
        github.Cards[13] = "Todo";
        await using var harness = NewHarness(github, configure: s => s.MutedKinds.Add(AlertKind.Board));

        await harness.PollAsync();
        github.Cards[13] = "Done";
        await harness.PollAsync();

        Assert.Empty(harness.Delivered);
    }

    [Fact]
    public async Task A_board_of_more_than_one_page_is_read_to_the_end()
    {
        var github = new FakeBoard { PageSize = 2 };
        github.Cards[1] = "Todo";
        github.Cards[2] = "Todo";
        github.Cards[3] = "Todo";
        await using var harness = NewHarness(github);

        await harness.PollAsync();
        github.Cards.Remove(3);
        github.Cards[4] = "Done";
        await harness.PollAsync();

        Assert.Equal(2, harness.Delivered.Count);
        Assert.Contains(harness.Delivered, a => a.Title == "Removed from Roadmap" && a.Detail == "#3 Card 3");
        Assert.Contains(harness.Delivered, a => a.Title == "Added to Roadmap" && a.Detail == "#4 Card 4");
        Assert.Equal(2, harness.Handler.Requests.Count(r => r.Path.EndsWith("/items", StringComparison.Ordinal) && !r.Query.Contains("after=")));
        Assert.True(harness.Handler.Requests.Count(r => r.Query.Contains("after=")) >= 2);
    }

    [Fact]
    public async Task Removing_the_board_from_settings_drops_its_bookkeeping()
    {
        var github = new FakeBoard();
        github.Cards[13] = "Todo";
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        var without = harness.Settings.Clone();
        without.Boards.Clear();
        harness.Monitor.Configure(without, harness.Tokens);

        // Watching it again is a fresh baseline: the fields are asked for a second time.
        harness.Monitor.Configure(harness.Settings, harness.Tokens);
        await harness.PollAsync();

        Assert.Equal(2, harness.Handler.Requests.Count(r => r.Path == "/orgs/acme/projectsV2/12/fields"));
        Assert.Empty(harness.Delivered);
    }

    // ---- Harness -------------------------------------------------------------

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-boards-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    private Harness NewHarness(
        FakeBoard github,
        string? statePath = null,
        string? historyPath = null,
        Action<AppSettings>? configure = null,
        GitHubAccount? account = null)
    {
        account ??= GitHubAccount.Create("octocat");
        account.IncludeInbox = false;

        var settings = new AppSettings
        {
            Accounts = [account],
            Repositories = [RepoSubscription.From(account.Id, RepoRef.Parse("acme/api-gateway"))],
            Boards = [BoardSubscription.From(account.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap")],
            WatchWorkflowRuns = false,
        };

        configure?.Invoke(settings);

        var tokens = new Dictionary<string, string> { [account.Id] = "ghp_token" };

        return new Harness(github, settings, tokens, statePath ?? NewFile(), historyPath ?? NewFile());
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

    private sealed class Harness : IAsyncDisposable
    {
        private TaskCompletionSource<MonitorStatus> _settled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _started;

        public Harness(FakeBoard github, AppSettings settings, Dictionary<string, string> tokens, string statePath, string historyPath)
        {
            Handler = new StubHandler(github.Respond);
            Settings = settings;
            Tokens = tokens;

            Alerts = new AlertStore(historyPath);
            Monitor = new MonitorService(Alerts, new StateStore(statePath), new HttpClient(Handler));

            Monitor.AlertsReceived += (_, alerts) => Delivered.AddRange(alerts);

            Monitor.StatusChanged += (_, status) =>
            {
                if (status.State != ConnectionState.Connecting)
                {
                    _settled.TrySetResult(status);
                }
            };

            Monitor.Configure(settings, tokens);
        }

        public StubHandler Handler { get; }

        public AppSettings Settings { get; }

        public Dictionary<string, string> Tokens { get; }

        public AlertStore Alerts { get; }

        public MonitorService Monitor { get; }

        public List<Alert> Delivered { get; } = [];

        public async Task<MonitorStatus> PollAsync()
        {
            _settled = new TaskCompletionSource<MonitorStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_started)
            {
                Monitor.RequestRefresh();
            }
            else
            {
                _started = true;
                Monitor.Start();
            }

            return await _settled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public ValueTask DisposeAsync() => Monitor.DisposeAsync();
    }

    /// <summary>
    /// A GitHub with one repository that never changes and one board whose cards the test moves
    /// between polls. Cards are numbered, and the number is the issue number too.
    /// </summary>
    private sealed class FakeBoard
    {
        public SortedDictionary<int, string?> Cards { get; } = [];

        public string? ETag { get; set; }

        /// <summary>How many cards a page holds; the real page is a hundred.</summary>
        public int PageSize { get; set; } = 100;

        /// <summary>Answers every board request with this status, the way a token without the scope is answered.</summary>
        public HttpStatusCode? Refuse { get; set; }

        private string _servedAs = string.Empty;

        public HttpResponseMessage Respond(RecordedRequest request)
        {
            var path = request.Path;

            if (path == "/user")
            {
                return Responses.Ok("""{"login":"octocat"}""");
            }

            if (path.StartsWith("/repos/", StringComparison.Ordinal))
            {
                return path.EndsWith("/events", StringComparison.Ordinal) || path.EndsWith("/commits", StringComparison.Ordinal)
                    ? Responses.Ok("[]")
                    : Responses.Ok("""{"full_name":"acme/api-gateway","default_branch":"main"}""");
            }

            if (Refuse is { } refused)
            {
                return Responses.Status(refused);
            }

            if (path == "/orgs/acme/projectsV2/12/fields")
            {
                return Responses.Ok(
                    """[{"id": 1, "name": "Title", "data_type": "title"}, {"id": 3, "name": "Status", "data_type": "single_select", "options": []}]""");
            }

            if (path == "/orgs/acme/projectsV2/12/items")
            {
                var body = Serialise();

                if (ETag is not null && request.IfNoneMatch == ETag && body == _servedAs)
                {
                    return Responses.Status(HttpStatusCode.NotModified);
                }

                _servedAs = body;

                var after = Cursor(request.Query);
                var page = Cards.Keys.Where(id => id > after).Take(PageSize).ToList();
                var last = page.Count > 0 ? page[^1] : after;
                var hasMore = Cards.Keys.Any(id => id > last);

                var headers = new List<(string, string)>();

                if (ETag is not null)
                {
                    headers.Add(("ETag", ETag));
                }

                if (hasMore)
                {
                    headers.Add(("Link", $"<https://api.github.com/orgs/acme/projectsV2/12/items?per_page={PageSize}&after={last}>; rel=\"next\""));
                }

                return Responses.Ok("[" + string.Join(",", page.Select(Card)) + "]", [.. headers]);
            }

            return Responses.Status(HttpStatusCode.NotFound);
        }

        private static int Cursor(string query)
        {
            var marker = query.IndexOf("after=", StringComparison.Ordinal);
            return marker < 0 ? 0 : int.Parse(query[(marker + 6)..].Split('&')[0]);
        }

        private string Serialise() => string.Join(";", Cards.Select(c => $"{c.Key}={c.Value}"));

        private string Card(int id)
        {
            var status = Cards[id] is { } name
                ? $$"""{ "id": "opt", "name": { "raw": "{{name}}", "html": "{{name}}" } }"""
                : "null";

            return $$"""
                {
                  "id": {{id}},
                  "content_type": "Issue",
                  "content": { "number": {{id}}, "title": "Card {{id}}", "html_url": "https://github.com/acme/api-gateway/issues/{{id}}", "state": "open" },
                  "updated_at": "2026-09-06T10:00:00Z",
                  "archived_at": null,
                  "fields": [
                    { "id": 1, "name": "Title", "data_type": "title", "value": { "raw": "Card {{id}}", "number": {{id}} } },
                    { "id": 3, "name": "Status", "data_type": "single_select", "value": {{status}} }
                  ]
                }
                """;
        }
    }
}
