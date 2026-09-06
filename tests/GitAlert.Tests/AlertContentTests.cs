using System.Text.Json;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// What an alert carries for the pane beyond its headline: what the issue, comment or release
/// said, the fields that describe it, and the card the pane builds from them.
/// </summary>
public class AlertContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static GhEvent Event(string type, string payload) => new()
    {
        Id = "42",
        Type = type,
        Actor = new GhActor { Login = "deniz", DisplayLogin = "deniz" },
        Repo = new GhEventRepo { Name = "acme/api-gateway" },
        Payload = JsonDocument.Parse(payload).RootElement.Clone(),
        CreatedAt = Now,
    };

    // ---- From the timeline --------------------------------------------------

    [Fact]
    public void An_issue_brings_what_it_says_and_what_describes_it()
    {
        var alert = EventTranslator.FromEvent(Event("IssuesEvent", """
        {
          "action": "opened",
          "issue": {
            "number": 77,
            "title": "Rate limit the poller",
            "body": "<!-- template -->\n## What happened\n\nThe **poller** keeps asking.",
            "html_url": "https://github.com/acme/api-gateway/issues/77",
            "labels": [ { "name": "bug" }, { "name": "api" } ],
            "assignees": [ { "login": "deniz-k" } ],
            "milestone": { "title": "2.2" }
          }
        }
        """))!;

        Assert.Equal("What happened\n\nThe poller keeps asking.", alert.Body);
        Assert.Collection(alert.Fields!,
            f => Assert.Equal(("Labels", "bug, api"), (f.Name, f.Value)),
            f => Assert.Equal(("Assignees", "deniz-k"), (f.Name, f.Value)),
            f => Assert.Equal(("Milestone", "2.2"), (f.Name, f.Value)));
    }

    [Fact]
    public void An_issue_with_nothing_to_describe_it_carries_no_fields_rather_than_empty_ones()
    {
        var alert = EventTranslator.FromEvent(Event("IssuesEvent", """
        { "action": "closed", "issue": { "number": 77, "title": "Rate limit the poller", "body": null, "labels": [], "assignees": [], "milestone": null } }
        """))!;

        Assert.Null(alert.Body);
        Assert.Null(alert.Fields);
    }

    [Fact]
    public void A_comment_brings_its_text_and_names_the_issue_it_was_left_on()
    {
        var alert = EventTranslator.FromEvent(Event("IssueCommentEvent", """
        {
          "action": "created",
          "issue": { "number": 77, "title": "Rate limit the poller" },
          "comment": { "body": "Reproduced on the 4070.\n\nNot on the 3060.", "html_url": "https://github.com/acme/api-gateway/issues/77#issuecomment-1" }
        }
        """))!;

        Assert.Equal("Reproduced on the 4070.", alert.Detail);
        Assert.Equal("Reproduced on the 4070.\n\nNot on the 3060.", alert.Body);
        Assert.Equal(("Issue", "#77 Rate limit the poller"), (alert.Fields![0].Name, alert.Fields[0].Value));
    }

    [Fact]
    public void A_review_comment_names_the_pull_request_and_the_line_it_sits_on()
    {
        var alert = EventTranslator.FromEvent(Event("PullRequestReviewCommentEvent", """
        {
          "pull_request": { "number": 41, "title": "Retry throttled requests" },
          "comment": { "body": "Use the header.", "path": "src/Poller.cs", "line": 120, "html_url": "https://github.com/acme/api-gateway/pull/41#discussion_r1" }
        }
        """))!;

        Assert.Equal("Use the header.", alert.Body);
        Assert.Collection(alert.Fields!,
            f => Assert.Equal(("Pull request", "#41 Retry throttled requests"), (f.Name, f.Value)),
            f => Assert.Equal(("File", "src/Poller.cs:120"), (f.Name, f.Value)));
    }

    [Fact]
    public void A_release_brings_its_notes_its_tag_and_its_target()
    {
        var alert = EventTranslator.FromEvent(Event("ReleaseEvent", """
        {
          "action": "published",
          "release": { "tag_name": "v2.1.0", "name": "Boards", "body": "## Added\n- Boards\n", "target_commitish": "main", "prerelease": false, "html_url": "https://github.com/acme/api-gateway/releases/tag/v2.1.0" }
        }
        """))!;

        Assert.Equal("Added\n- Boards", alert.Body);
        Assert.Collection(alert.Fields!,
            f => Assert.Equal(("Tag", "v2.1.0"), (f.Name, f.Value)),
            f => Assert.Equal(("Target", "main"), (f.Name, f.Value)));
    }

    [Fact]
    public void A_push_carries_no_body_it_has_a_diff()
    {
        var alert = EventTranslator.FromEvent(Event("PushEvent", """
        { "ref": "refs/heads/main", "size": 1, "head": "bbb", "commits": [ { "message": "fix: retry" } ] }
        """))!;

        Assert.Null(alert.Body);
        Assert.Null(alert.Fields);
    }

    // ---- From a board -------------------------------------------------------

    [Fact]
    public void A_board_card_brings_what_its_issue_says_when_github_sends_it_but_the_state_does_not_keep_it()
    {
        var board = BoardSubscription.From("acct", new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap");
        var item = JsonSerializer.Deserialize<GhProjectItem>("""
        {
          "id": 13,
          "content_type": "Issue",
          "content": { "number": 87, "title": "Rate limit the poller", "body": "The **poller** keeps asking.", "html_url": "https://github.com/acme/api-gateway/issues/87" },
          "updated_at": "2026-09-06T11:30:00Z",
          "fields": [ { "id": 3, "name": "Status", "data_type": "single_select", "value": { "id": "o2", "name": { "raw": "In progress", "html": "In progress" } } } ]
        }
        """)!;

        var fresh = BoardTranslator.Snapshot(item, statusFieldId: 3);
        Assert.Equal("The poller keeps asking.", fresh.Body);

        var before = new BoardItemState { ContentType = "Issue", Number = 87, Title = "Rate limit the poller", Status = "Todo", UpdatedAt = Now };
        var alerts = BoardTranslator.Diff(
            board,
            new Dictionary<string, BoardItemState> { ["13"] = before },
            new Dictionary<string, BoardItemState> { ["13"] = fresh },
            onlyStatus: true,
            complete: true,
            Now);

        var moved = Assert.Single(alerts);
        Assert.Equal("Moved to In progress", moved.Title);
        Assert.Equal("The poller keeps asking.", moved.Body);

        // A thousand cards' worth of prose has no place in the state file.
        Assert.DoesNotContain("poller keeps asking", JsonSerializer.Serialize(fresh));
    }

    // ---- The card the pane builds ------------------------------------------

    private static Alert Alert(AlertKind kind, string title, string? detail = null, string? body = null, string? url = null, string? actor = "deniz") => new()
    {
        Id = "account|event:1",
        Kind = kind,
        Title = title,
        Detail = detail,
        Body = body,
        Actor = actor,
        Repository = "acme/api-gateway",
        Url = url ?? "https://github.com/acme/api-gateway/issues/77",
        Timestamp = DateTimeOffset.Now - TimeSpan.FromMinutes(5),
        Fields = [new AlertField("Labels", "bug")],
    };

    [Fact]
    public void An_issue_card_names_its_repository_who_did_it_and_where_to_go()
    {
        var card = AlertCardViewModel.ForRepository(Alert(AlertKind.Issue, "Issue #77 opened", "Rate limit the poller", "The poller keeps asking."));

        Assert.Equal("acme / api-gateway", card.Source);
        Assert.Equal("https://github.com/acme/api-gateway", card.SourceUrl);
        Assert.Equal("Open repository", card.SourceLabel);
        Assert.Equal(string.Empty, card.Caption);
        Assert.Equal("acme / api-gateway · by deniz · 5m ago", card.Meta);
        Assert.Equal("Issue #77 opened", card.Headline);
        Assert.Equal("Rate limit the poller", card.Item);
        Assert.True(card.ShowsItem);
        Assert.True(card.HasBody);
        Assert.True(card.HasFields);
        Assert.Equal("Open issue", card.ItemLabel);
        Assert.Equal("https://github.com/acme/api-gateway/issues/77", card.ItemUrl);
    }

    [Fact]
    public void A_comment_card_does_not_print_its_first_line_twice()
    {
        var card = AlertCardViewModel.ForRepository(Alert(
            AlertKind.Comment,
            "New comment on issue #77",
            "Reproduced on the 4070 with the 560…",
            "Reproduced on the 4070 with the 560 driver.\nNot on the 3060."));

        Assert.False(card.ShowsItem);
        Assert.Equal("Open comment", card.ItemLabel);
    }

    [Theory]
    [InlineData(AlertKind.Workflow, "Open run")]
    [InlineData(AlertKind.Release, "Open release")]
    [InlineData(AlertKind.Mention, "Open thread")]
    [InlineData(AlertKind.Star, "Open on GitHub")]
    public void The_way_to_the_thing_is_named_after_what_it_is(AlertKind kind, string label)
    {
        Assert.Equal(label, AlertCardViewModel.ForRepository(Alert(kind, "Something happened")).ItemLabel);
    }

    [Fact]
    public void An_alert_that_points_at_the_repository_itself_offers_one_button_not_two()
    {
        var card = AlertCardViewModel.ForRepository(Alert(AlertKind.Other, "Repository made public", url: "https://github.com/acme/api-gateway", actor: null));

        Assert.False(card.HasItemUrl);
        Assert.Equal("acme / api-gateway · 5m ago", card.Meta);
    }

    [Fact]
    public void A_board_card_captions_the_row_with_the_board_and_opens_the_board()
    {
        var card = AlertCardViewModel.ForBoard(Alert(AlertKind.Board, "Moved to In progress", "#87 Rate limit the poller", actor: null), "acme / Roadmap", "https://github.com/orgs/acme/projects/12");

        Assert.Equal("acme / Roadmap", card.Caption);
        Assert.Equal("Open board", card.SourceLabel);
        Assert.Equal("Open issue", card.ItemLabel);
        Assert.False(card.HasBody);
    }
}
