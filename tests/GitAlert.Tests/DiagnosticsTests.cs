using System.IO;
using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The Diagnostics page: what the monitor says about its last check of each repository, board
/// and inbox, each account's budget, and the timing - the answer to "why did I not hear about
/// that" without opening the state file.
/// </summary>
public class DiagnosticsTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public async Task The_first_check_reads_a_repository_and_the_next_finds_it_unchanged()
    {
        var github = new FakeGitHub();
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        var first = harness.Monitor.Diagnose();
        var subject = Assert.Single(first.Subjects);

        Assert.Equal("acme/api-gateway", subject.Name);
        Assert.Equal("Repository", subject.Kind);
        Assert.Equal("@octocat", subject.Account);
        Assert.True(subject.Enabled);
        Assert.Equal(PollOutcome.Read, subject.Outcome);
        Assert.NotNull(subject.LastPolledAt);
        Assert.NotNull(subject.WatchingSince);
        Assert.True(subject.Requests >= 3, "events, commits and runs at the least");
        Assert.Null(subject.Error);

        var account = Assert.Single(first.Accounts);
        Assert.Equal("@octocat", account.Login);
        Assert.True(account.HasToken);
        Assert.Equal(4812, account.RateLimit.Remaining);
        Assert.Equal(5000, account.RateLimit.Limit);
        Assert.Null(account.ThrottledUntil);

        await harness.PollAsync();

        var second = harness.Monitor.Diagnose();
        var again = Assert.Single(second.Subjects);

        Assert.Equal(PollOutcome.Unchanged, again.Outcome);
        Assert.Equal(3, again.Requests);
        Assert.True(second.Accounts[0].NotModified >= 3);
        Assert.True(second.Accounts[0].Requests > second.Accounts[0].NotModified);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_reached_says_why_and_the_others_are_unaffected()
    {
        var github = new FakeGitHub();
        github.NotFound.Add("acme/private-thing");

        await using var harness = NewHarness(github, settings =>
            settings.Repositories.Add(RepoSubscription.From(settings.Accounts[0].Id, new RepoRef("acme", "private-thing"))));

        await harness.PollAsync();

        var snapshot = harness.Monitor.Diagnose();

        Assert.Equal(ConnectionState.Warning, snapshot.State);
        Assert.Equal(["acme/api-gateway", "acme/private-thing"], snapshot.Subjects.Select(s => s.Name));

        var fine = snapshot.Subjects[0];
        var broken = snapshot.Subjects[1];

        Assert.Equal(PollOutcome.Read, fine.Outcome);
        Assert.Equal(PollOutcome.Failed, broken.Outcome);
        Assert.Contains("was not found", broken.Error, StringComparison.Ordinal);
        Assert.Equal(1, broken.Requests);
    }

    [Fact]
    public async Task A_repository_switched_off_is_listed_as_such_rather_than_left_out()
    {
        var github = new FakeGitHub();

        await using var harness = NewHarness(github, settings =>
            settings.Repositories.Add(new RepoSubscription { AccountId = settings.Accounts[0].Id, Owner = "acme", Name = "paused", Enabled = false }));

        await harness.PollAsync();

        var paused = harness.Monitor.Diagnose().Subjects.Single(s => s.Name == "acme/paused");

        Assert.False(paused.Enabled);
        Assert.Equal(PollOutcome.NotYet, paused.Outcome);
        Assert.Null(paused.LastPolledAt);
    }

    [Fact]
    public async Task The_inbox_and_a_board_are_listed_beside_the_repositories()
    {
        var github = new FakeGitHub();

        await using var harness = NewHarness(github, settings =>
        {
            settings.Accounts[0].IncludeInbox = true;
            settings.Boards.Add(BoardSubscription.From(settings.Accounts[0].Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap"));
        });

        await harness.PollAsync();

        var snapshot = harness.Monitor.Diagnose();

        Assert.Equal(["Repository", "Board", "Inbox"], snapshot.Subjects.Select(s => s.Kind));
        Assert.Equal("acme/Roadmap", snapshot.Subjects[1].Name);
        Assert.Equal("@octocat inbox", snapshot.Subjects[2].Name);
        Assert.All(snapshot.Subjects, s => Assert.Equal(PollOutcome.Read, s.Outcome));
    }

    [Fact]
    public async Task The_timing_says_when_the_last_check_ran_and_when_the_next_is_due()
    {
        var github = new FakeGitHub();
        await using var harness = NewHarness(github);

        Assert.Null(harness.Monitor.Diagnose().LastPollStartedAt);

        var before = DateTimeOffset.Now;
        await harness.PollAsync();

        var snapshot = await WaitForAsync(harness.Monitor, d => d.NextPollAt is not null);

        Assert.NotNull(snapshot.LastPollStartedAt);
        Assert.NotNull(snapshot.LastPollFinishedAt);
        Assert.True(snapshot.LastPollStartedAt >= before.AddSeconds(-1));
        Assert.True(snapshot.LastPollFinishedAt >= snapshot.LastPollStartedAt);
        Assert.Equal(TimeSpan.FromMinutes(2), snapshot.Interval);
        Assert.True(snapshot.NextPollAt > snapshot.LastPollFinishedAt);
        Assert.True(snapshot.NextPollAt <= DateTimeOffset.Now + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task The_report_names_the_repositories_and_the_outcomes_but_never_the_token()
    {
        var github = new FakeGitHub();
        github.NotFound.Add("acme/private-thing");

        await using var harness = NewHarness(github, settings =>
            settings.Repositories.Add(RepoSubscription.From(settings.Accounts[0].Id, new RepoRef("acme", "private-thing"))));

        await harness.PollAsync();

        var report = harness.Monitor.Diagnose().ToReport("2.6.0", "Windows 11");

        Assert.Contains("GitAlert 2.6.0 on Windows 11", report, StringComparison.Ordinal);
        Assert.Contains("@octocat: 4812/5000 calls left", report, StringComparison.Ordinal);
        Assert.Contains("acme/api-gateway [Repository, @octocat]: checked", report, StringComparison.Ordinal);
        Assert.Contains(", read ·", report, StringComparison.Ordinal);
        Assert.Contains("failed: acme/private-thing was not found", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_token", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_keeps_its_rows_and_brings_them_up_to_date_after_every_check()
    {
        var github = new FakeGitHub();
        github.NotFound.Add("acme/private-thing");

        await using var harness = NewHarness(github, settings =>
            settings.Repositories.Add(RepoSubscription.From(settings.Accounts[0].Id, new RepoRef("acme", "private-thing"))));

        using var page = new DiagnosticsViewModel(harness.Monitor);

        Assert.Equal("No check has run yet.", page.LastCheckText);
        Assert.Equal(2, page.Subjects.Count);
        Assert.All(page.Subjects, row => Assert.Equal("Not checked yet", row.CheckedText));
        Assert.False(page.ShowAccounts, "one account is not worth naming on every row");

        var fine = page.Subjects[0];
        var broken = page.Subjects[1];

        await harness.PollAsync();
        page.Refresh();

        Assert.Same(fine, page.Subjects[0]);
        Assert.Equal("read", fine.OutcomeText);
        Assert.True(fine.IsRead);
        Assert.Equal("Checked just now", fine.CheckedText);
        Assert.Contains("requests", fine.DetailText, StringComparison.Ordinal);
        Assert.Contains("watching since", fine.DetailText, StringComparison.Ordinal);

        Assert.Equal("failed", broken.OutcomeText);
        Assert.True(broken.IsFailed);
        Assert.Contains("was not found", broken.Error, StringComparison.Ordinal);

        Assert.StartsWith("Connected, with a problem", page.StatusText, StringComparison.Ordinal);
        Assert.Contains("took", page.LastCheckText, StringComparison.Ordinal);
        Assert.Contains("every 2 minutes", page.NextCheckText, StringComparison.Ordinal);

        var account = Assert.Single(page.Accounts);
        Assert.Equal("@octocat", account.Login);
        Assert.Contains("4,812 of 5,000", account.BudgetText, StringComparison.Ordinal);
        Assert.False(account.HasProblem);

        await harness.PollAsync();
        page.Refresh();

        Assert.Same(fine, page.Subjects[0]);
        Assert.Equal("unchanged", fine.OutcomeText);
        Assert.DoesNotContain("ghp_token", page.Report, StringComparison.Ordinal);
    }

    // ---- Plumbing ----------------------------------------------------------

    private static async Task<MonitorDiagnostics> WaitForAsync(MonitorService monitor, Func<MonitorDiagnostics, bool> ready)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = monitor.Diagnose();

            if (ready(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(20);
        }

        return monitor.Diagnose();
    }

    private Harness NewHarness(FakeGitHub github, Action<AppSettings>? configure = null)
    {
        var account = GitHubAccount.Create("octocat");
        account.IncludeInbox = false;

        var settings = new AppSettings
        {
            Accounts = [account],
            Repositories = [RepoSubscription.From(account.Id, new RepoRef("acme", "api-gateway"))],
        };

        configure?.Invoke(settings);

        return new Harness(github, settings, new Dictionary<string, string> { [account.Id] = "ghp_token" }, NewFile(), NewFile());
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-diagnostics-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <summary>
    /// A GitHub with one quiet repository: a page of nothing for the timeline, one commit, no
    /// runs, an empty inbox and an empty board, all with a tag so the next reading comes back
    /// 304. Named repositories answer 404 to everything.
    /// </summary>
    private sealed class FakeGitHub
    {
        private const string Tag = "\"quiet\"";

        public HashSet<string> NotFound { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HttpResponseMessage Respond(RecordedRequest request)
        {
            var path = request.Path;

            if (path == "/user")
            {
                return Responses.Ok("""{"login":"octocat"}""");
            }

            if (RepositoryOf(path) is { } repository && NotFound.Contains(repository))
            {
                return Responses.Status(HttpStatusCode.NotFound);
            }

            var reset = DateTimeOffset.UtcNow.AddMinutes(40).ToUnixTimeSeconds().ToString();
            (string, string)[] headers =
            [
                ("ETag", Tag),
                ("x-ratelimit-remaining", "4812"),
                ("x-ratelimit-limit", "5000"),
                ("x-ratelimit-reset", reset),
            ];

            if (request.IfNoneMatch == Tag)
            {
                return Responses.Json(HttpStatusCode.NotModified, string.Empty, headers);
            }

            if (path.EndsWith("/events", StringComparison.Ordinal) || path == "/notifications")
            {
                return Responses.Ok("[]", headers);
            }

            if (path.EndsWith("/commits", StringComparison.Ordinal))
            {
                return Responses.Ok(
                    """[{"sha":"1111111111111111111111111111111111111111","html_url":"https://github.com","commit":{"message":"initial","author":{"name":"octocat","date":"2026-01-01T00:00:00Z"},"committer":{"name":"octocat","date":"2026-01-01T00:00:00Z"}}}]""",
                    headers);
            }

            if (path.EndsWith("/actions/runs", StringComparison.Ordinal))
            {
                return Responses.Ok("""{"workflow_runs":[]}""", headers);
            }

            if (path.EndsWith("/fields", StringComparison.Ordinal))
            {
                return Responses.Ok("""[{"id":1,"name":"Status","data_type":"single_select","options":[{"id":"a","name":"Todo"}]}]""", headers);
            }

            if (path.EndsWith("/items", StringComparison.Ordinal))
            {
                return Responses.Ok("[]", headers);
            }

            return Responses.Ok("""{"full_name":"acme/api-gateway","default_branch":"main"}""", headers);
        }

        private static string? RepositoryOf(string path)
        {
            if (!path.StartsWith("/repos/", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = path["/repos/".Length..].Split('/');
            return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private TaskCompletionSource<MonitorStatus> _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;

        public Harness(FakeGitHub github, AppSettings settings, Dictionary<string, string> tokens, string statePath, string historyPath)
        {
            Monitor = new MonitorService(new AlertStore(historyPath), new StateStore(statePath), new HttpClient(new StubHandler(github.Respond)));

            Monitor.StatusChanged += (_, status) =>
            {
                if (status.State != ConnectionState.Connecting)
                {
                    _settled.TrySetResult(status);
                }
            };

            Monitor.Configure(settings, tokens);
        }

        public MonitorService Monitor { get; }

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
