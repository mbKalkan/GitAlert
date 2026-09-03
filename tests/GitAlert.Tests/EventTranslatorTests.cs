using System.Text.Json;
using GitAlert.Core;
using GitAlert.GitHub;
using Xunit;

namespace GitAlert.Tests;

public class EventTranslatorTests
{
    private static GhEvent Event(string type, string payload, string repository = "acme/api-gateway") => new()
    {
        Id = "42",
        Type = type,
        Actor = new GhActor { Login = "deniz", DisplayLogin = "deniz" },
        Repo = new GhEventRepo { Name = repository },
        Payload = JsonDocument.Parse(payload).RootElement.Clone(),
        CreatedAt = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Push_with_several_commits_links_to_the_compare_view()
    {
        var alert = EventTranslator.FromEvent(Event("PushEvent", """
        {
          "ref": "refs/heads/main",
          "size": 3,
          "distinct_size": 3,
          "before": "aaa",
          "head": "bbb",
          "commits": [
            { "message": "first" },
            { "message": "fix: retry rate-limited requests\n\nlong body" }
          ]
        }
        """));

        Assert.NotNull(alert);
        Assert.Equal(AlertKind.Push, alert!.Kind);
        Assert.Equal("3 new commits on main", alert.Title);
        Assert.Equal("fix: retry rate-limited requests", alert.Detail);
        Assert.Equal("https://github.com/acme/api-gateway/compare/aaa...bbb", alert.Url);

        // Identified by the head commit rather than the event, so the same push reported by the
        // commits endpoint collapses into one alert.
        Assert.Equal("commit:bbb", alert.Id);
    }

    private static GhCommit Commit(string sha, string message, string author = "deniz") => new()
    {
        Sha = sha,
        HtmlUrl = $"https://github.com/acme/api-gateway/commit/{sha}",
        Commit = new GhCommitDetail
        {
            Message = message,
            Author = new GhCommitAuthor { Name = author, Date = new DateTimeOffset(2026, 9, 3, 11, 6, 0, TimeSpan.Zero) },
        },
        Author = new GhUser { Login = author },
    };

    [Fact]
    public void Commits_polled_directly_become_a_push_alert()
    {
        var alert = EventTranslator.FromCommits(
            [Commit("ccc", "Create test.md"), Commit("bbb", "Update README.md")],
            "acme/api-gateway",
            "main",
            previousSha: "aaa");

        Assert.Equal(AlertKind.Push, alert.Kind);
        Assert.Equal("2 new commits on main", alert.Title);
        Assert.Equal("Create test.md", alert.Detail);
        Assert.Equal("deniz", alert.Actor);
        Assert.Equal("https://github.com/acme/api-gateway/compare/aaa...ccc", alert.Url);
    }

    [Fact]
    public void A_single_polled_commit_links_to_that_commit()
    {
        var alert = EventTranslator.FromCommits([Commit("ccc", "Create test.md")], "acme/api-gateway", "main", "bbb");

        Assert.Equal("New commit on main", alert.Title);
        Assert.Equal("https://github.com/acme/api-gateway/commit/ccc", alert.Url);
    }

    [Fact]
    public void A_repository_with_no_known_branch_still_reads_sensibly()
    {
        var alert = EventTranslator.FromCommits([Commit("ccc", "Create test.md")], "acme/api-gateway", null, null);

        Assert.Equal("New commit", alert.Title);
    }

    [Fact]
    public void A_push_carries_the_commit_range_so_its_diff_can_be_fetched()
    {
        var many = EventTranslator.FromCommits(
            [Commit("ccc", "second"), Commit("bbb", "first")], "acme/api-gateway", "main", previousSha: "aaa");

        Assert.Equal("ccc", many.DiffHead);
        Assert.Equal("aaa", many.DiffBase);
        Assert.True(many.HasDiff);

        // A single commit is its own diff, so there is no range to compare against.
        var one = EventTranslator.FromCommits([Commit("ccc", "only")], "acme/api-gateway", "main", "bbb");

        Assert.Equal("ccc", one.DiffHead);
        Assert.Null(one.DiffBase);
    }

    [Fact]
    public void A_pull_request_alert_remembers_its_number_for_the_file_list()
    {
        var alert = EventTranslator.FromEvent(Event("PullRequestEvent", """
        { "action": "opened", "number": 88,
          "pull_request": { "number": 88, "title": "Add pagination" } }
        """));

        Assert.Equal(88, alert!.PullRequestNumber);
        Assert.True(alert.HasDiff);
    }

    [Fact]
    public void An_alert_about_nothing_diffable_says_so()
    {
        var alert = EventTranslator.FromWorkflowRun(
            new GhWorkflowRun { Id = 1, Name = "ci", Status = "completed", Conclusion = "success" },
            "acme/api-gateway");

        Assert.False(alert.HasDiff);
    }

    /// <summary>
    /// GitHub fills the events timeline in lazily, so a push often reaches us through the commits
    /// endpoint first and through the timeline hours later. Both must resolve to one alert.
    /// </summary>
    [Fact]
    public void The_same_push_seen_through_both_sources_is_one_alert()
    {
        var fromCommits = EventTranslator.FromCommits([Commit("bbb", "fix: retry")], "acme/api-gateway", "main", "aaa");

        var fromEvents = EventTranslator.FromEvent(Event("PushEvent", """
        { "ref": "refs/heads/main", "size": 1, "distinct_size": 1, "before": "aaa", "head": "bbb",
          "commits": [ { "message": "fix: retry" } ] }
        """));

        Assert.Equal(fromCommits.Id, fromEvents!.Id);
    }

    [Fact]
    public void A_single_commit_links_straight_to_the_diff()
    {
        var alert = EventTranslator.FromEvent(Event("PushEvent", """
        { "ref": "refs/heads/feature", "size": 1, "distinct_size": 1, "before": "aaa", "head": "bbb",
          "commits": [ { "message": "tidy up" } ] }
        """));

        Assert.Equal("New commit on feature", alert!.Title);
        Assert.Equal("https://github.com/acme/api-gateway/commit/bbb", alert.Url);
    }

    [Fact]
    public void A_merged_pull_request_reads_as_merged_and_counts_as_a_success()
    {
        var alert = EventTranslator.FromEvent(Event("PullRequestEvent", """
        { "action": "closed", "number": 88,
          "pull_request": { "number": 88, "merged": true, "title": "Add pagination",
                            "html_url": "https://github.com/acme/api-gateway/pull/88" } }
        """));

        Assert.Equal("PR #88 merged", alert!.Title);
        Assert.Equal(AlertSeverity.Success, alert.Severity);
    }

    [Fact]
    public void A_push_to_a_pull_request_branch_is_not_reported_twice()
    {
        // "synchronize" fires alongside the PushEvent that already covers the change.
        var alert = EventTranslator.FromEvent(Event("PullRequestEvent", """
        { "action": "synchronize", "number": 88, "pull_request": { "number": 88, "title": "x" } }
        """));

        Assert.Null(alert);
    }

    [Fact]
    public void A_comment_on_a_pull_request_says_PR_not_issue()
    {
        var alert = EventTranslator.FromEvent(Event("IssueCommentEvent", """
        { "action": "created",
          "issue": { "number": 91, "title": "Redis session store", "pull_request": { "url": "x" } },
          "comment": { "body": "Looks good to me", "html_url": "https://github.com/c/1" } }
        """));

        Assert.Equal("New comment on PR #91", alert!.Title);
        Assert.Equal("Looks good to me", alert.Detail);
    }

    [Fact]
    public void Unknown_event_types_are_ignored_rather_than_shown_raw()
    {
        Assert.Null(EventTranslator.FromEvent(Event("SponsorshipEvent", "{}")));
    }

    [Fact]
    public void A_failed_workflow_run_is_an_error()
    {
        var alert = EventTranslator.FromWorkflowRun(
            new GhWorkflowRun
            {
                Id = 9876,
                Name = "integration",
                DisplayTitle = "fix: retry",
                RunNumber = 241,
                Status = "completed",
                Conclusion = "failure",
                HtmlUrl = "https://github.com/acme/api-gateway/actions/runs/9876",
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            "acme/api-gateway");

        Assert.Equal(AlertKind.Workflow, alert.Kind);
        Assert.Equal("integration failed (#241)", alert.Title);
        Assert.Equal(AlertSeverity.Error, alert.Severity);
        Assert.Equal("run:9876", alert.Id);
    }

    [Theory]
    [InlineData("https://api.github.com/repos/acme/api/pulls/12", "https://github.com/acme/api/pull/12")]
    [InlineData("https://api.github.com/repos/acme/api/issues/44", "https://github.com/acme/api/issues/44")]
    [InlineData("https://api.github.com/repos/acme/api/commits/abc", "https://github.com/acme/api/commit/abc")]
    [InlineData("https://api.github.com/repos/acme/api/discussions/7", "https://github.com/acme/api/discussions/7")]
    public void Api_urls_are_rewritten_into_browser_urls(string api, string expected)
    {
        Assert.Equal(expected, EventTranslator.ToBrowserUrl(api, "fallback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/whatever")]
    [InlineData("https://api.github.com/user")]
    public void An_unmappable_subject_falls_back_to_the_repository(string? api)
    {
        Assert.Equal("fallback", EventTranslator.ToBrowserUrl(api, "fallback"));
    }
}
