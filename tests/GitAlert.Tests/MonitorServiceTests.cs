using System.IO;
using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The polling engine, on everything except the day it all works. What GitAlert must never do is
/// bury someone under history the moment they add a repository, announce the same thing twice, or
/// let one unreachable repository silence the rest - and none of that is reachable by clicking.
/// </summary>
public class MonitorServiceTests : IDisposable
{
    private const string Repository = "acme/api-gateway";

    private readonly List<string> _files = [];

    // ---- Baselining --------------------------------------------------------

    /// <summary>
    /// The first poll only writes down where things stand. Otherwise adding a repository would
    /// open with fifty notifications about things that happened last month.
    /// </summary>
    [Fact]
    public async Task The_first_poll_records_where_things_stand_and_announces_nothing()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        await using var harness = NewHarness(github);

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Empty(harness.Delivered);
    }

    [Fact]
    public async Task The_second_poll_announces_only_what_arrived_since_the_first()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        await harness.PollAsync();

        Assert.Single(harness.Delivered);
        Assert.Equal("commit:bbb2222", Unstamped(harness.Delivered[0].Id));
    }

    [Fact]
    public async Task An_event_already_seen_is_not_announced_again_when_it_reappears()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        await harness.PollAsync();
        await harness.PollAsync();
        await harness.PollAsync();

        Assert.Single(harness.Delivered);
    }

    /// <summary>
    /// The high-water mark lives in state.json for exactly this reason: a restart must not
    /// replay the window GitHub is still returning.
    /// </summary>
    [Fact]
    public async Task A_restart_does_not_replay_what_the_previous_run_had_already_seen()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        var account = GitHubAccount.Create("octocat");
        var state = NewFile();
        var history = NewFile();

        await using (var first = NewHarness(github, state, history, account: account))
        {
            await first.PollAsync();

            github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
            await first.PollAsync();

            Assert.Single(first.Delivered);
        }

        await using var second = NewHarness(github, state, history, account: account);
        var status = await second.PollAsync();

        // Same state, not a fresh baseline: the mark was read back, and everything is below it.
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Empty(second.Delivered);
    }

    /// <summary>
    /// Event ids are strings in the payload and numbers in meaning. Anything that is not a number
    /// cannot be compared against the high-water mark, so it is skipped rather than guessed at.
    /// </summary>
    [Fact]
    public async Task An_event_id_that_is_not_a_number_is_skipped_rather_than_throwing()
    {
        var github = new FakeGitHub
        {
            Events = Events(Push("not-a-number", "abc1234"), Push("1001", "bbb2222")),
        };

        await using var harness = NewHarness(github);

        await harness.PollAsync();
        github.Events = Events(Push("not-a-number", "abc1234"), Push("1002", "ccc3333"), Push("1001", "bbb2222"));

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Single(harness.Delivered);
        Assert.Equal("commit:ccc3333", Unstamped(harness.Delivered[0].Id));
    }

    /// <summary>
    /// One event GitAlert cannot make sense of must not cost the cycle. The translator threw on a
    /// payload that was not an object - which is also what an absent payload deserialises to -
    /// and that is not a GitHubException, so it went straight past the per-repository handler and
    /// aborted the whole poll: every repository after it in the list went unchecked.
    /// </summary>
    /// <remarks>
    /// The broken event has to arrive after the baseline. A baselining poll skips translating
    /// anything at all, so an event that was already there when the repository was added never
    /// reaches the translator and proves nothing.
    /// </remarks>
    [Fact]
    public async Task A_malformed_event_does_not_take_the_rest_of_the_poll_with_it()
    {
        const string Broken = """
        {
          "id": "1500",
          "type": "PushEvent",
          "actor": { "login": "someone" },
          "repo": { "name": "acme/api-gateway" },
          "created_at": "2026-01-01T10:00:00Z",
          "payload": null
        }
        """;

        var github = new FakeGitHub();
        github.EventsFor["acme/api-gateway"] = Events(Push("1000", "aaa1111"));
        github.EventsFor["acme/other"] = Events(Push("1001", "abc1234", repository: "acme/other"));

        await using var harness = NewHarness(github, configure: s =>
            s.Repositories.Add(RepoSubscription.From(s.Accounts[0].Id, new RepoRef("acme", "other"))));

        var first = await harness.PollAsync();
        Assert.Equal(ConnectionState.Connected, first.State);

        // Now the broken one turns up, ahead of the mark, on the repository polled first.
        github.EventsFor["acme/api-gateway"] = "[" + Broken + "," + Push("1000", "aaa1111") + "]";

        github.EventsFor["acme/other"] = Events(
            Push("1002", "bbb2222", repository: "acme/other"),
            Push("1001", "abc1234", repository: "acme/other"));

        var second = await harness.PollAsync();

        // The cycle finished, and the repository after the broken one was still reached.
        Assert.Equal(ConnectionState.Connected, second.State);
        Assert.Contains(harness.Delivered, a => a.Repository == "acme/other");

        // The broken event degrades to what could be read of it rather than disappearing: there
        // was a push, GitAlert just cannot say which commit or which branch.
        var salvaged = Assert.Single(harness.Delivered, a => a.Repository == "acme/api-gateway");
        Assert.False(salvaged.HasDiff);
        Assert.Contains("(unknown)", salvaged.Title);
    }

    // ---- What the user asked not to hear -----------------------------------

    [Fact]
    public async Task A_muted_kind_is_not_delivered_at_all()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };

        await using var harness = NewHarness(github, configure: s => s.MutedKinds = [AlertKind.Push]);

        await harness.PollAsync();
        github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        await harness.PollAsync();

        Assert.Empty(harness.Delivered);
    }

    [Fact]
    public async Task Your_own_activity_is_skipped_when_you_have_asked_for_that()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };

        await using var harness = NewHarness(github, configure: s => s.IgnoreOwnActivity = true);

        await harness.PollAsync();

        github.Events = Events(
            Push("1003", "ccc3333", actor: "someone-else"),
            Push("1002", "bbb2222", actor: "octocat"),
            Push("1001", "abc1234"));

        await harness.PollAsync();

        Assert.Single(harness.Delivered);
        Assert.Equal("someone-else", harness.Delivered[0].Actor);
    }

    [Fact]
    public async Task Your_own_activity_is_kept_by_default_because_it_is_how_people_test_the_app()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        await using var harness = NewHarness(github);

        await harness.PollAsync();
        github.Events = Events(Push("1002", "bbb2222", actor: "octocat"), Push("1001", "abc1234"));
        await harness.PollAsync();

        Assert.Single(harness.Delivered);
    }

    // ---- When part of it fails ---------------------------------------------

    /// <summary>
    /// One repository going missing - renamed, made private, access revoked - must not stop the
    /// others being checked. It is a warning about that repository, not an outage.
    /// </summary>
    [Fact]
    public async Task One_unreachable_repository_does_not_silence_the_others()
    {
        var github = new FakeGitHub();
        github.NotFound.Add("gone/repo");
        github.EventsFor["acme/api-gateway"] = Events(Push("1001", "abc1234"));

        await using var harness = NewHarness(github, configure: s =>
            s.Repositories.Add(RepoSubscription.From(s.Accounts[0].Id, new RepoRef("gone", "repo"))));

        await harness.PollAsync();

        github.EventsFor["acme/api-gateway"] = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Warning, status.State);
        Assert.Contains("gone/repo", status.Message);
        Assert.Single(harness.Delivered);
    }

    [Fact]
    public async Task A_rejected_token_is_an_error_rather_than_a_warning_because_nothing_will_work()
    {
        var github = new FakeGitHub();
        github.Failures["/user"] = HttpStatusCode.Unauthorized;

        await using var harness = NewHarness(github);

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Error, status.State);
        Assert.Contains("token", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Being_offline_is_reported_as_being_offline()
    {
        var github = new FakeGitHub { Throw = () => new HttpRequestException("no route to host") };

        await using var harness = NewHarness(github);

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Error, status.State);
        Assert.Contains("Cannot reach GitHub", status.Message);
    }

    [Fact]
    public async Task Several_things_failing_at_once_is_summarised_rather_than_listed()
    {
        var github = new FakeGitHub();
        github.NotFound.Add("gone/one");
        github.NotFound.Add("gone/two");

        await using var harness = NewHarness(github, configure: s =>
        {
            s.Repositories.Add(RepoSubscription.From(s.Accounts[0].Id, new RepoRef("gone", "one")));
            s.Repositories.Add(RepoSubscription.From(s.Accounts[0].Id, new RepoRef("gone", "two")));
        });

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Warning, status.State);
        Assert.Contains("2 of the things", status.Message);
    }

    /// <summary>An account with nothing watched under it should not be reported as broken.</summary>
    [Fact]
    public async Task Nothing_to_watch_is_a_prompt_rather_than_a_failure()
    {
        await using var harness = NewHarness(new FakeGitHub(), configure: s => s.Repositories.Clear());

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.NotConfigured, status.State);
        Assert.Contains("Add a repository", status.Message);
    }

    [Fact]
    public async Task An_account_with_no_token_is_reported_as_such_rather_than_polled()
    {
        await using var harness = NewHarness(new FakeGitHub(), withToken: false);

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.NotConfigured, status.State);
        Assert.Contains("usable token", status.Message);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task A_disabled_repository_is_not_polled()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };

        await using var harness = NewHarness(github, configure: s => s.Repositories[0].Enabled = false);

        await harness.PollAsync();

        Assert.DoesNotContain(harness.Handler.Requests, r => r.Path.Contains("api-gateway"));
    }

    // ---- Commits, which are the other half of a push -----------------------

    /// <summary>
    /// A push reaches GitAlert twice - through the events timeline and through the commits
    /// endpoint - and the two share an id precisely so it is announced once.
    /// </summary>
    [Fact]
    public async Task A_push_seen_through_both_endpoints_is_announced_once()
    {
        var github = new FakeGitHub
        {
            Events = Events(Push("1001", "abc1234")),
            Commits = Commits(("abc1234", "Set the baseline")),
        };

        await using var harness = NewHarness(github);
        await harness.PollAsync();

        github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        github.Commits = Commits(("bbb2222", "The change"), ("abc1234", "Set the baseline"));

        await harness.PollAsync();

        Assert.Single(harness.Delivered);
        Assert.Equal("commit:bbb2222", Unstamped(harness.Delivered[0].Id));
    }

    [Fact]
    public async Task Several_commits_at_once_become_one_alert_covering_the_range()
    {
        var github = new FakeGitHub { Commits = Commits(("abc1234", "Baseline")) };
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        github.Commits = Commits(("ccc3333", "Third"), ("bbb2222", "Second"), ("abc1234", "Baseline"));
        await harness.PollAsync();

        var alert = Assert.Single(harness.Delivered);
        Assert.Equal("2 new commits on main", alert.Title);
        Assert.Equal("ccc3333", alert.DiffHead);
        Assert.Equal("abc1234", alert.DiffBase);
    }

    /// <summary>
    /// The branch name comes from the repository, and a repository that cannot be read should
    /// still produce the alert - without a branch in the sentence rather than without the alert.
    /// </summary>
    [Fact]
    public async Task A_push_is_still_announced_when_the_branch_name_cannot_be_learned()
    {
        var github = new FakeGitHub { Commits = Commits(("abc1234", "Baseline")) };
        github.Failures["/repos/acme/api-gateway"] = HttpStatusCode.Forbidden;

        await using var harness = NewHarness(github);

        await harness.PollAsync();

        github.Commits = Commits(("bbb2222", "The change"), ("abc1234", "Baseline"));
        await harness.PollAsync();

        // Without a branch to name, the sentence drops the branch rather than the alert.
        var alert = Assert.Single(harness.Delivered);
        Assert.Equal("New commit", alert.Title);
    }

    /// <summary>
    /// And once it has been learned it is not asked for again, however many pushes follow.
    /// </summary>
    [Fact]
    public async Task The_branch_name_is_learned_once_and_then_left_alone()
    {
        var github = new FakeGitHub { Commits = Commits(("abc1234", "Baseline")) };
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        github.Commits = Commits(("bbb2222", "Second"), ("abc1234", "Baseline"));
        await harness.PollAsync();

        github.Commits = Commits(("ccc3333", "Third"), ("bbb2222", "Second"), ("abc1234", "Baseline"));
        await harness.PollAsync();

        Assert.Single(harness.Handler.Requests, r => r.Path == "/repos/acme/api-gateway");
    }

    // ---- CI runs -----------------------------------------------------------

    /// <summary>
    /// Runs finish out of order, and one can wait days for a deployment approval. Holding the
    /// mark back at it meant nothing after it was announced until it finished, which for a
    /// stuck approval was never. It is remembered by id instead: the runs after it go out as
    /// they finish, and it is still announced when it does.
    /// </summary>
    [Fact]
    public async Task A_run_that_is_still_going_neither_holds_up_the_runs_after_it_nor_is_forgotten()
    {
        var github = new FakeGitHub { Runs = Runs((10, "completed", "success")) };

        await using var harness = NewHarness(github, configure: s => s.WatchWorkflowRuns = true);
        await harness.PollAsync();

        github.Runs = Runs((12, "completed", "success"), (11, "waiting", null), (10, "completed", "success"));
        await harness.PollAsync();

        var first = Assert.Single(harness.Delivered);
        Assert.Contains("#12", first.Title);

        github.Runs = Runs((12, "completed", "success"), (11, "completed", "failure"), (10, "completed", "success"));
        await harness.PollAsync();

        Assert.Equal(2, harness.Delivered.Count);
        Assert.Contains("#11", harness.Delivered[1].Title);
        Assert.Equal(AlertSeverity.Error, harness.Delivered[1].Severity);
    }

    /// <summary>The run being waited on survives a restart, like every other mark does.</summary>
    [Fact]
    public async Task A_run_being_waited_on_is_still_waited_on_after_a_restart()
    {
        var github = new FakeGitHub { Runs = Runs((10, "completed", "success")) };
        var account = GitHubAccount.Create("octocat");
        var state = NewFile();
        var history = NewFile();

        await using (var first = NewHarness(github, state, history, s => s.WatchWorkflowRuns = true, account: account))
        {
            await first.PollAsync();

            github.Runs = Runs((11, "in_progress", null), (10, "completed", "success"));
            await first.PollAsync();

            Assert.Empty(first.Delivered);
        }

        github.Runs = Runs((11, "completed", "success"), (10, "completed", "success"));

        await using var second = NewHarness(github, state, history, s => s.WatchWorkflowRuns = true, account: account);
        await second.PollAsync();

        var alert = Assert.Single(second.Delivered);
        Assert.Contains("#11", alert.Title);
    }

    [Fact]
    public async Task Asking_only_about_failures_leaves_the_passes_alone()
    {
        var github = new FakeGitHub { Runs = Runs((10, "completed", "success")) };

        await using var harness = NewHarness(github, configure: s =>
        {
            s.WatchWorkflowRuns = true;
            s.OnlyFailedWorkflowRuns = true;
        });

        await harness.PollAsync();

        github.Runs = Runs((12, "completed", "failure"), (11, "completed", "success"), (10, "completed", "success"));
        await harness.PollAsync();

        var alert = Assert.Single(harness.Delivered);
        Assert.Contains("failed", alert.Title);
    }

    [Fact]
    public async Task Runs_are_not_polled_at_all_when_the_user_has_switched_them_off()
    {
        await using var harness = NewHarness(new FakeGitHub(), configure: s => s.WatchWorkflowRuns = false);

        await harness.PollAsync();

        Assert.DoesNotContain(harness.Handler.Requests, r => r.Path.EndsWith("/actions/runs"));
    }

    /// <summary>
    /// A repository whose only run is still going has nothing to announce yet, but it still has
    /// to write down that it looked. The run in flight belongs to the baseline - it was already
    /// there when the repository was added - so only what starts afterwards is news.
    /// </summary>
    [Fact]
    public async Task A_run_already_in_flight_when_the_repository_was_added_belongs_to_the_baseline()
    {
        var github = new FakeGitHub { Runs = Runs((7, "in_progress", null)) };

        await using var harness = NewHarness(github, configure: s => s.WatchWorkflowRuns = true);
        await harness.PollAsync();

        github.Runs = Runs((8, "completed", "success"), (7, "completed", "success"));
        await harness.PollAsync();

        var alert = Assert.Single(harness.Delivered);
        Assert.Contains("#8", alert.Title);
    }

    // ---- The inbox ---------------------------------------------------------

    [Fact]
    public async Task The_inbox_baselines_before_it_announces_anything()
    {
        var github = new FakeGitHub { Inbox = Inbox(("n1", "mention", "2026-01-01T10:00:00Z")) };

        await using var harness = NewHarness(github, configure: s => s.Accounts[0].IncludeInbox = true);

        await harness.PollAsync();
        Assert.Empty(harness.Delivered);

        github.Inbox = Inbox(("n2", "mention", "2026-01-01T11:00:00Z"), ("n1", "mention", "2026-01-01T10:00:00Z"));
        await harness.PollAsync();

        var alert = Assert.Single(harness.Delivered);
        Assert.Equal(AlertKind.Mention, alert.Kind);
    }

    [Fact]
    public async Task The_inbox_is_left_alone_when_the_account_has_it_switched_off()
    {
        await using var harness = NewHarness(new FakeGitHub(), configure: s => s.Accounts[0].IncludeInbox = false);

        await harness.PollAsync();

        Assert.DoesNotContain(harness.Handler.Requests, r => r.Path == "/notifications");
    }

    /// <summary>
    /// The repository is on the list with its tick off. It is not polled, and a mention there
    /// arriving through the inbox is not announced either: off means off.
    /// </summary>
    [Fact]
    public async Task The_inbox_stays_quiet_about_a_repository_that_is_switched_off()
    {
        var github = new FakeGitHub { Inbox = Inbox(("n1", "mention", "2026-01-01T10:00:00Z")) };

        await using var harness = NewHarness(github, configure: s =>
        {
            s.Accounts[0].IncludeInbox = true;
            s.Repositories[0].Enabled = false;
        });

        await harness.PollAsync();

        github.Inbox = Inbox(("n2", "mention", "2026-01-01T11:00:00Z"), ("n1", "mention", "2026-01-01T10:00:00Z"));
        await harness.PollAsync();

        Assert.Empty(harness.Delivered);
        Assert.DoesNotContain(harness.Handler.Requests, r => r.Path.StartsWith("/repos/", StringComparison.Ordinal));
    }

    /// <summary>An inbox failure is about the inbox, not about the repositories.</summary>
    [Fact]
    public async Task A_failing_inbox_does_not_stop_repositories_being_checked()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        github.Failures["/notifications"] = HttpStatusCode.Forbidden;

        await using var harness = NewHarness(github, configure: s => s.Accounts[0].IncludeInbox = true);

        await harness.PollAsync();

        github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Warning, status.State);
        Assert.Single(harness.Delivered);
    }

    // ---- Conditional requests ----------------------------------------------

    [Fact]
    public async Task The_second_poll_sends_back_the_etag_the_first_was_given()
    {
        var github = new FakeGitHub { ETag = "\"events-1\"" };
        await using var harness = NewHarness(github);

        await harness.PollAsync();
        await harness.PollAsync();

        var events = harness.Handler.Requests.Where(r => r.Path.EndsWith("/events")).ToList();

        Assert.Null(events[0].IfNoneMatch);
        Assert.Equal("\"events-1\"", events[1].IfNoneMatch);
    }

    /// <summary>
    /// Configure asks for a refresh whenever the credentials changed, which at startup they
    /// always have, and the loop polls immediately anyway. Left pending, that request made every
    /// repository be polled twice in a row on every launch.
    /// </summary>
    [Fact]
    public async Task Starting_up_polls_once_rather_than_twice()
    {
        var github = new FakeGitHub();
        await using var harness = NewHarness(github);

        await harness.PollAsync();
        await Task.Delay(400);

        Assert.Single(harness.Handler.Requests, r => r.Path.EndsWith("/events"));
    }

    // ---- Reset -------------------------------------------------------------

    [Fact]
    public async Task Resetting_the_state_makes_the_next_poll_baseline_again()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        await using var harness = NewHarness(github);

        await harness.PollAsync();

        harness.Monitor.ResetState();

        github.Events = Events(Push("1002", "bbb2222"), Push("1001", "abc1234"));
        await harness.PollAsync();

        // Baselining again, so the new event is recorded rather than announced.
        Assert.Empty(harness.Delivered);
    }

    // ---- Harness -----------------------------------------------------------

    // ---- Pushes that arrive late, or look old ------------------------------

    /// <summary>
    /// Two pushes in one interval are one alert from the commits endpoint, named after the newer
    /// head. The timeline's copy of the earlier push - hours or days later on a private
    /// repository - carried a head nothing had seen, and was announced as news about an old
    /// commit. The commits endpoint covers the default branch; the timeline only adds the rest.
    /// </summary>
    [Fact]
    public async Task The_timelines_late_copy_of_a_default_branch_push_is_not_announced_again()
    {
        var github = new FakeGitHub { Commits = Commits(("aaa1111", "Baseline")) };
        await using var harness = NewHarness(github);
        await harness.PollAsync();

        github.Commits = Commits(("ccc3333", "Second push"), ("bbb2222", "First push"), ("aaa1111", "Baseline"));
        await harness.PollAsync();

        var fromCommits = Assert.Single(harness.Delivered);
        Assert.Equal("commit:ccc3333", Unstamped(fromCommits.Id));

        // Days later the timeline catches up with both pushes, and with one to another branch.
        github.Events = Events(
            Push("1003", "ddd4444", gitRef: "refs/heads/feature"),
            Push("1002", "ccc3333"),
            Push("1001", "bbb2222"));
        await harness.PollAsync();

        Assert.Equal(2, harness.Delivered.Count);
        Assert.Equal("commit:ddd4444", Unstamped(harness.Delivered[1].Id));
        Assert.Contains("feature", harness.Delivered[1].Title);
    }

    /// <summary>
    /// A rebase, a squash or a cherry-pick gives a commit a new committer date and keeps the
    /// author date. Stamped with the author date, a branch rebased and pushed today was filed
    /// under the week it was started and shown as days old.
    /// </summary>
    [Fact]
    public async Task A_rebased_commit_is_dated_by_when_it_landed_rather_than_when_it_was_written()
    {
        var github = new FakeGitHub
        {
            Commits = CommitsAt(("aaa1111", "Baseline", "2026-01-01T10:00:00Z", "2026-01-01T10:00:00Z")),
        };

        await using var harness = NewHarness(github);
        await harness.PollAsync();

        github.Commits = CommitsAt(
            ("bbb2222", "Written last week, rebased today", "2026-01-01T09:00:00Z", "2026-01-08T15:00:00Z"),
            ("aaa1111", "Baseline", "2026-01-01T10:00:00Z", "2026-01-01T10:00:00Z"));
        await harness.PollAsync();

        var alert = Assert.Single(harness.Delivered);
        Assert.Equal(DateTimeOffset.Parse("2026-01-08T15:00:00Z"), alert.Timestamp);
    }

    /// <summary>
    /// After a force push, or a change of default branch, the last head is not on the page at
    /// all. Every commit on it used to count as new - a page of old commits in one card. Only
    /// what was committed after the last head is news.
    /// </summary>
    [Fact]
    public async Task A_head_that_is_no_longer_on_the_page_does_not_make_the_whole_page_news()
    {
        var github = new FakeGitHub
        {
            Commits = CommitsAt(
                ("bbb2222", "Second", "2026-01-02T10:00:00Z", "2026-01-02T10:00:00Z"),
                ("aaa1111", "First", "2026-01-01T10:00:00Z", "2026-01-01T10:00:00Z")),
        };

        await using var harness = NewHarness(github);
        await harness.PollAsync();

        // bbb2222 is force-pushed away; ccc3333 replaces it, and aaa1111 is still there below.
        github.Commits = CommitsAt(
            ("ccc3333", "Rewritten", "2026-01-03T10:00:00Z", "2026-01-03T10:00:00Z"),
            ("aaa1111", "First", "2026-01-01T10:00:00Z", "2026-01-01T10:00:00Z"));
        await harness.PollAsync();

        var alert = Assert.Single(harness.Delivered);
        Assert.Equal("New commit on main", alert.Title);
        Assert.Equal("ccc3333", alert.DiffHead);

        // A plain rewind to an older commit is nothing to announce at all.
        github.Commits = CommitsAt(("aaa1111", "First", "2026-01-01T10:00:00Z", "2026-01-01T10:00:00Z"));
        await harness.PollAsync();

        Assert.Single(harness.Delivered);
    }

    // ---- Being told to slow down -------------------------------------------

    /// <summary>
    /// A rate limit answers every request the same way until it resets, and each request made
    /// in the meantime counts against the secondary limit that may be the reason. The account
    /// is left alone until then, and the status says so rather than "checking".
    /// </summary>
    [Fact]
    public async Task A_rate_limited_account_is_left_alone_until_the_budget_is_back()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        github.Failures["/repos/acme/api-gateway/events"] = HttpStatusCode.TooManyRequests;

        await using var harness = NewHarness(github);

        var first = await harness.PollAsync();
        Assert.Equal(ConnectionState.Error, first.State);

        // The cycle stopped at the refusal: nothing after it on this account was asked for.
        Assert.DoesNotContain(harness.Handler.Requests, r => r.Path.EndsWith("/commits", StringComparison.Ordinal));

        var asked = harness.Handler.Requests.Count;
        var second = await harness.PollAsync();

        Assert.Equal(ConnectionState.Error, second.State);
        Assert.Contains("Rate limited until", second.Message);
        Assert.Equal(asked, harness.Handler.Requests.Count);
    }

    /// <summary>Replacing the token is a new budget, so the wait does not carry over to it.</summary>
    [Fact]
    public async Task A_replaced_token_is_polled_straight_away_even_inside_a_rate_limit()
    {
        var github = new FakeGitHub { Events = Events(Push("1001", "abc1234")) };
        github.Failures["/repos/acme/api-gateway/events"] = HttpStatusCode.TooManyRequests;

        await using var harness = NewHarness(github);
        await harness.PollAsync();

        github.Failures.Clear();
        harness.Reconfigure(token: "ghp_another");

        var status = await harness.PollAsync();

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Contains(harness.Handler.Requests, r =>
            r.Path.EndsWith("/events", StringComparison.Ordinal) && r.Authorization == "Bearer ghp_another");
    }

    private static string Unstamped(string id) => id[(id.IndexOf('|') + 1)..];

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-monitor-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <param name="account">
    /// Pass the same account to two harnesses to model a restart. The sync state is keyed by
    /// account id, and a fresh id is a fresh baseline - which quietly proves nothing.
    /// </param>
    private Harness NewHarness(
        FakeGitHub github,
        string? statePath = null,
        string? historyPath = null,
        Action<AppSettings>? configure = null,
        bool withToken = true,
        GitHubAccount? account = null)
    {
        account ??= GitHubAccount.Create("octocat");
        account.IncludeInbox = false;

        var settings = new AppSettings
        {
            Accounts = [account],
            Repositories = [RepoSubscription.From(account.Id, RepoRef.Parse(Repository))],
            WatchWorkflowRuns = false,
        };

        configure?.Invoke(settings);

        var tokens = withToken
            ? new Dictionary<string, string> { [account.Id] = "ghp_token" }
            : [];

        return new Harness(
            github,
            settings,
            tokens,
            statePath ?? NewFile(),
            historyPath ?? NewFile());
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

        public Harness(
            FakeGitHub github,
            AppSettings settings,
            Dictionary<string, string> tokens,
            string statePath,
            string historyPath)
        {
            Handler = new StubHandler(github.Respond);
            Settings = settings;

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

        /// <summary>
        /// What saving the settings window does: the same settings, a different token. Configure
        /// asks for a poll of its own when the credentials change; the next PollAsync queues one
        /// more behind it, and the status it waits for is from a poll made with the new token
        /// either way.
        /// </summary>
        public void Reconfigure(string token)
        {
            var tokens = Settings.Accounts.ToDictionary(a => a.Id, _ => token, StringComparer.Ordinal);
            Monitor.Configure(Settings, tokens);
        }

        public AlertStore Alerts { get; }

        public MonitorService Monitor { get; }

        /// <summary>Everything this harness has raised, across every poll.</summary>
        public List<Alert> Delivered { get; } = [];

        /// <summary>Runs one poll and waits for it to reach a state that is not "checking".</summary>
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

    // ---- Payloads ----------------------------------------------------------

    private static string Events(params string[] events) => "[" + string.Join(",", events) + "]";

    private static string Push(
        string id,
        string head,
        string actor = "someone",
        string repository = "acme/api-gateway",
        string gitRef = "refs/heads/main") =>
        $$"""
        {
          "id": "{{id}}",
          "type": "PushEvent",
          "actor": { "login": "{{actor}}", "display_login": "{{actor}}" },
          "repo": { "name": "{{repository}}" },
          "created_at": "2026-01-01T10:00:00Z",
          "payload": {
            "ref": "{{gitRef}}",
            "size": 1,
            "distinct_size": 1,
            "head": "{{head}}",
            "before": "0000000",
            "commits": [ { "message": "A commit message" } ]
          }
        }
        """;

    private static string Commits(params (string Sha, string Message)[] commits) =>
        "[" + string.Join(",", commits.Select(c => $$"""
        {
          "sha": "{{c.Sha}}",
          "html_url": "https://github.com/acme/api-gateway/commit/{{c.Sha}}",
          "commit": {
            "message": "{{c.Message}}",
            "author": { "name": "Someone", "date": "2026-01-01T10:00:00Z" }
          }
        }
        """)) + "]";

    /// <summary>Commits with both dates, for the cases where the two disagree.</summary>
    private static string CommitsAt(params (string Sha, string Message, string Authored, string Committed)[] commits) =>
        "[" + string.Join(",", commits.Select(c => $$"""
        {
          "sha": "{{c.Sha}}",
          "html_url": "https://github.com/acme/api-gateway/commit/{{c.Sha}}",
          "commit": {
            "message": "{{c.Message}}",
            "author": { "name": "Someone", "date": "{{c.Authored}}" },
            "committer": { "name": "GitHub", "date": "{{c.Committed}}" }
          }
        }
        """)) + "]";

    private static string Runs(params (long Id, string Status, string? Conclusion)[] runs) =>
        $$"""{ "workflow_runs": [{{string.Join(",", runs.Select(r => $$"""
        {
          "id": {{r.Id}},
          "name": "CI",
          "run_number": {{r.Id}},
          "status": "{{r.Status}}",
          "conclusion": {{(r.Conclusion is null ? "null" : $"\"{r.Conclusion}\"")}},
          "head_branch": "main",
          "updated_at": "2026-01-01T10:00:00Z"
        }
        """))}}] }""";

    private static string Inbox(params (string Id, string Reason, string UpdatedAt)[] notifications) =>
        "[" + string.Join(",", notifications.Select(n => $$"""
        {
          "id": "{{n.Id}}",
          "unread": true,
          "reason": "{{n.Reason}}",
          "updated_at": "{{n.UpdatedAt}}",
          "subject": { "title": "Something happened", "type": "Issue", "url": null },
          "repository": { "full_name": "acme/api-gateway" }
        }
        """)) + "]";

    /// <summary>A GitHub that answers from fields the test can set between polls.</summary>
    private sealed class FakeGitHub
    {
        public string User { get; set; } = """{"login":"octocat"}""";

        public string Repository { get; set; } =
            """{"full_name":"acme/api-gateway","default_branch":"main"}""";

        public string Events { get; set; } = "[]";

        public string Commits { get; set; } = "[]";

        public string Runs { get; set; } = """{"workflow_runs":[]}""";

        public string Inbox { get; set; } = "[]";

        public string? ETag { get; set; }

        /// <summary>Per-repository event payloads, for tests with more than one repository.</summary>
        public Dictionary<string, string> EventsFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Repositories that answer 404 to everything.</summary>
        public HashSet<string> NotFound { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Exact paths that fail, and how.</summary>
        public Dictionary<string, HttpStatusCode> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Set to make every request fail before it reaches a response at all.</summary>
        public Func<Exception>? Throw { get; set; }

        public HttpResponseMessage Respond(RecordedRequest request)
        {
            if (Throw is not null)
            {
                throw Throw();
            }

            if (Failures.TryGetValue(request.Path, out var status))
            {
                return Responses.Status(status);
            }

            var path = request.Path;

            if (path == "/user")
            {
                return Responses.Ok(User);
            }

            if (path == "/notifications")
            {
                return Responses.Ok(Inbox);
            }

            var repository = RepositoryOf(path);

            if (repository is not null && NotFound.Contains(repository))
            {
                return Responses.Status(HttpStatusCode.NotFound);
            }

            var headers = ETag is null ? [] : new[] { ("ETag", ETag) };

            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                return Responses.Ok(EventsFor.GetValueOrDefault(repository ?? string.Empty, Events), headers);
            }

            if (path.EndsWith("/commits", StringComparison.Ordinal))
            {
                return Responses.Ok(Commits);
            }

            if (path.EndsWith("/actions/runs", StringComparison.Ordinal))
            {
                return Responses.Ok(Runs);
            }

            return Responses.Ok(Repository);
        }

        /// <summary>"/repos/owner/name/rest" -> "owner/name".</summary>
        private static string? RepositoryOf(string path)
        {
            if (!path.StartsWith("/repos/", StringComparison.Ordinal))
            {
                return null;
            }

            var segments = path["/repos/".Length..].Split('/');
            return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : null;
        }
    }
}
