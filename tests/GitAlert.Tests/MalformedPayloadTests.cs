using System.Text.Json;
using GitAlert.Core;
using GitAlert.GitHub;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Payloads that are not the shape the documentation promises. GitHub's event payloads vary by
/// type and by age, a private repository can hide fields another one shows, and none of it is
/// worth an exception in a background poll: a translator that throws takes the whole cycle with
/// it, including the repositories it had not reached yet.
/// </summary>
public class MalformedPayloadTests
{
    private static GhEvent Event(string type, string payload = "{}", string id = "1001") =>
        new()
        {
            Id = id,
            Type = type,
            Actor = new GhActor { Login = "someone" },
            Repo = new GhEventRepo { Name = "acme/api-gateway" },
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonDocument.Parse(payload).RootElement,
        };

    // ---- Missing everything ------------------------------------------------

    [Theory]
    [InlineData("PushEvent")]
    [InlineData("PullRequestEvent")]
    [InlineData("PullRequestReviewEvent")]
    [InlineData("PullRequestReviewCommentEvent")]
    [InlineData("IssuesEvent")]
    [InlineData("IssueCommentEvent")]
    [InlineData("CommitCommentEvent")]
    [InlineData("ReleaseEvent")]
    [InlineData("CreateEvent")]
    [InlineData("DeleteEvent")]
    [InlineData("ForkEvent")]
    [InlineData("WatchEvent")]
    [InlineData("PublicEvent")]
    [InlineData("MemberEvent")]
    [InlineData("GollumEvent")]
    public void An_empty_payload_produces_nothing_rather_than_throwing(string type)
    {
        var alert = EventTranslator.FromEvent(Event(type));

        // Either it is skipped or it is built from what little there is. Neither may throw.
        if (alert is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(alert.Title));
        }
    }

    /// <summary>A payload that is not an object at all - null, a list, a number.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    public void A_payload_that_is_not_an_object_is_survived(string payload)
    {
        var alert = EventTranslator.FromEvent(Event("PushEvent", payload));

        Assert.NotNull(alert);
        Assert.Equal(AlertKind.Push, alert.Kind);
    }

    // ---- Pushes ------------------------------------------------------------

    [Fact]
    public void A_push_with_no_ref_says_the_branch_is_unknown_rather_than_leaving_a_gap()
    {
        var alert = EventTranslator.FromEvent(Event("PushEvent", """{"size":1,"head":"abc1234"}"""))!;

        Assert.Contains("(unknown)", alert.Title);
    }

    [Fact]
    public void A_push_with_no_head_falls_back_to_the_branch_listing_and_has_no_diff()
    {
        var alert = EventTranslator.FromEvent(Event("PushEvent", """{"ref":"refs/heads/main","size":1}"""))!;

        Assert.Equal("event:1001", alert.Id);
        Assert.Null(alert.DiffHead);
        Assert.False(alert.HasDiff);
        Assert.EndsWith("/commits/main", alert.Url);
    }

    [Fact]
    public void A_push_whose_commits_are_not_a_list_still_produces_a_headline()
    {
        var payload = """{"ref":"refs/heads/main","size":2,"head":"abc1234","before":"def5678","commits":"nope"}""";

        var alert = EventTranslator.FromEvent(Event("PushEvent", payload))!;

        Assert.Equal("2 new commits on main", alert.Title);
        Assert.Null(alert.Detail);
    }

    [Fact]
    public void A_push_with_an_empty_commit_list_has_no_message_to_show()
    {
        var payload = """{"ref":"refs/heads/main","size":1,"head":"abc1234","commits":[]}""";

        Assert.Null(EventTranslator.FromEvent(Event("PushEvent", payload))!.Detail);
    }

    /// <summary>
    /// A tag push arrives as a ref under refs/tags, and the branch name must not be printed with
    /// the refs/ prefix still attached.
    /// </summary>
    [Theory]
    [InlineData("refs/heads/release/2.0", "release/2.0")]
    [InlineData("refs/tags/v1.0.0", "v1.0.0")]
    [InlineData("main", "main")]
    public void A_ref_is_shortened_however_it_is_written(string reference, string expected)
    {
        var payload = $$"""{"ref":"{{reference}}","size":1,"head":"abc1234"}""";

        Assert.Contains(expected, EventTranslator.FromEvent(Event("PushEvent", payload))!.Title);
    }

    // ---- Pull requests -----------------------------------------------------

    [Fact]
    public void A_pull_request_event_with_no_pull_request_is_skipped()
    {
        Assert.Null(EventTranslator.FromEvent(Event("PullRequestEvent", """{"action":"opened"}""")));
    }

    [Fact]
    public void A_pull_request_action_nobody_asked_about_is_skipped()
    {
        var payload = """{"action":"labeled","pull_request":{"number":7,"title":"Something"}}""";

        Assert.Null(EventTranslator.FromEvent(Event("PullRequestEvent", payload)));
    }

    [Fact]
    public void A_pull_request_with_no_number_still_opens_but_carries_no_file_list()
    {
        var payload = """{"action":"opened","pull_request":{"title":"Something"}}""";

        var alert = EventTranslator.FromEvent(Event("PullRequestEvent", payload))!;

        Assert.Null(alert.PullRequestNumber);
        Assert.Contains("opened", alert.Title);
    }

    [Fact]
    public void A_review_with_no_pull_request_attached_is_skipped()
    {
        var payload = """{"review":{"state":"approved"}}""";

        Assert.Null(EventTranslator.FromEvent(Event("PullRequestReviewEvent", payload)));
    }

    [Fact]
    public void A_review_state_that_is_not_recognised_reads_as_a_plain_review()
    {
        var payload = """
        {"review":{"state":"dismissed"},"pull_request":{"number":7,"title":"Something"}}
        """;

        var alert = EventTranslator.FromEvent(Event("PullRequestReviewEvent", payload))!;

        Assert.Contains("reviewed", alert.Title);
        Assert.Equal(AlertSeverity.Normal, alert.Severity);
    }

    // ---- Commits polled directly -------------------------------------------

    [Fact]
    public void A_commit_with_no_detail_at_all_still_becomes_an_alert()
    {
        var alert = EventTranslator.FromCommits(
            [new GhCommit { Sha = "abc1234" }],
            "acme/api-gateway",
            branch: "main",
            previousSha: null);

        Assert.Equal("commit:abc1234", alert.Id);
        Assert.Null(alert.Detail);
        Assert.Null(alert.Actor);
        Assert.Equal("https://github.com/acme/api-gateway/commit/abc1234", alert.Url);
    }

    [Fact]
    public void A_commit_message_of_only_whitespace_is_no_message()
    {
        var alert = EventTranslator.FromCommits(
            [new GhCommit { Sha = "abc1234", Commit = new GhCommitDetail { Message = "   \n  " } }],
            "acme/api-gateway",
            branch: "main",
            previousSha: null);

        Assert.Null(alert.Detail);
    }

    /// <summary>Cards show one line, so a commit body is cut down to its subject.</summary>
    [Fact]
    public void A_multi_line_commit_message_is_reduced_to_its_first_line()
    {
        var alert = EventTranslator.FromCommits(
            [
                new GhCommit
                {
                    Sha = "abc1234",
                    Commit = new GhCommitDetail { Message = "Fix the thing\n\nA long explanation follows." },
                }
            ],
            "acme/api-gateway",
            branch: "main",
            previousSha: null);

        Assert.Equal("Fix the thing", alert.Detail);
    }

    [Fact]
    public void A_commit_subject_longer_than_a_card_is_cut_with_an_ellipsis()
    {
        var subject = new string('x', 300);

        var alert = EventTranslator.FromCommits(
            [new GhCommit { Sha = "abc1234", Commit = new GhCommitDetail { Message = subject } }],
            "acme/api-gateway",
            branch: "main",
            previousSha: null);

        Assert.True(alert.Detail!.Length < 130);
        Assert.EndsWith("…", alert.Detail);
    }

    // ---- Workflow runs -----------------------------------------------------

    [Fact]
    public void A_run_with_no_name_is_still_described()
    {
        var alert = EventTranslator.FromWorkflowRun(
            new GhWorkflowRun { Id = 5, RunNumber = 5, Status = "completed" },
            "acme/api-gateway");

        Assert.StartsWith("Workflow finished", alert.Title);
        Assert.Equal(AlertSeverity.Normal, alert.Severity);
    }

    [Theory]
    [InlineData("success", AlertSeverity.Success)]
    [InlineData("failure", AlertSeverity.Error)]
    [InlineData("timed_out", AlertSeverity.Error)]
    [InlineData("cancelled", AlertSeverity.Warning)]
    [InlineData("action_required", AlertSeverity.Warning)]
    [InlineData("neutral", AlertSeverity.Normal)]
    [InlineData("skipped", AlertSeverity.Normal)]
    [InlineData(null, AlertSeverity.Normal)]
    public void Every_conclusion_maps_to_a_severity_including_the_ones_not_listed(
        string? conclusion,
        AlertSeverity expected)
    {
        var alert = EventTranslator.FromWorkflowRun(
            new GhWorkflowRun { Id = 5, RunNumber = 5, Name = "CI", Conclusion = conclusion },
            "acme/api-gateway");

        Assert.Equal(expected, alert.Severity);
    }

    // ---- Notifications -----------------------------------------------------

    [Fact]
    public void A_notification_with_no_repository_still_produces_an_alert()
    {
        var alert = EventTranslator.FromNotification(new GhNotification
        {
            Id = "n1",
            Reason = "mention",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal(AlertKind.Mention, alert.Kind);
        Assert.Equal(string.Empty, alert.Repository);
    }

    [Fact]
    public void A_notification_reason_nobody_has_mapped_falls_back_to_the_subject_type()
    {
        var alert = EventTranslator.FromNotification(new GhNotification
        {
            Id = "n1",
            Reason = "security_alert",
            UpdatedAt = DateTimeOffset.UtcNow,
            Subject = new GhNotificationSubject { Type = "Release", Title = "1.2.0" },
        });

        Assert.Equal(AlertKind.Release, alert.Kind);
    }

    [Fact]
    public void A_notification_with_neither_a_known_reason_nor_a_known_subject_is_still_shown()
    {
        var alert = EventTranslator.FromNotification(new GhNotification
        {
            Id = "n1",
            Reason = "who_knows",
            UpdatedAt = DateTimeOffset.UtcNow,
            Subject = new GhNotificationSubject { Type = "SomethingNew" },
        });

        Assert.Equal(AlertKind.Other, alert.Kind);
        Assert.False(string.IsNullOrWhiteSpace(alert.Title));
    }

    /// <summary>
    /// The subject URL is an API address, and following it in a browser shows JSON. Anything that
    /// cannot be rewritten has to fall back to somewhere a person can actually read.
    /// </summary>
    [Fact]
    public void A_subject_url_that_cannot_be_rewritten_falls_back_to_the_repository()
    {
        var alert = EventTranslator.FromNotification(new GhNotification
        {
            Id = "n1",
            Reason = "mention",
            UpdatedAt = DateTimeOffset.UtcNow,
            Subject = new GhNotificationSubject { Url = "not a url at all" },
            Repository = new GhRepository { FullName = "acme/api-gateway" },
        });

        Assert.Equal("https://github.com/acme/api-gateway", alert.Url);
    }
}
