using System.Text.Json;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Services;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Two readings of a board and what they are worth saying. GitHub has no timeline for a board, so
/// everything the user hears about a card is inferred from where it stood and where it stands.
/// </summary>
public class BoardTranslatorTests
{
    private static readonly BoardSubscription Board = new()
    {
        AccountId = "acct",
        Owner = "acme",
        OwnerKind = BoardOwnerKind.Organization,
        Number = 12,
        Title = "Roadmap",
    };

    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    // ---- Reading a card ------------------------------------------------------

    [Fact]
    public void A_card_is_read_with_its_column_its_fields_and_the_issue_behind_it()
    {
        var item = Item(
            """
            {
              "id": 13,
              "content_type": "Issue",
              "content": { "number": 87, "title": "Rate limit the poller", "html_url": "https://github.com/acme/api-gateway/issues/87", "state": "open" },
              "updated_at": "2026-09-06T11:30:00Z",
              "archived_at": null,
              "fields": [
                { "id": 1, "name": "Title", "data_type": "title", "value": { "raw": "Rate limit the poller", "number": 87 } },
                { "id": 3, "name": "Status", "data_type": "single_select", "value": { "id": "o2", "name": { "raw": "In progress", "html": "In progress" }, "color": "BLUE" } },
                { "id": 4, "name": "Priority", "data_type": "single_select", "value": { "id": "p1", "name": { "raw": "P1", "html": "P1" } } },
                { "id": 5, "name": "Assignees", "data_type": "assignees", "value": [ { "login": "deniz-k" }, { "login": "octocat" } ] },
                { "id": 6, "name": "Iteration", "data_type": "iteration", "value": { "id": "it1", "title": { "raw": "Sprint 14", "html": "Sprint 14" }, "start_date": "2026-09-01" } },
                { "id": 7, "name": "Story points", "data_type": "number", "value": 5 },
                { "id": 8, "name": "Due", "data_type": "date", "value": "2026-09-12" },
                { "id": 9, "name": "Notes", "data_type": "text", "value": null }
              ]
            }
            """);

        var state = BoardTranslator.Snapshot(item, statusFieldId: 3);

        Assert.Equal("Issue", state.ContentType);
        Assert.Equal(87, state.Number);
        Assert.Equal("Rate limit the poller", state.Title);
        Assert.Equal("https://github.com/acme/api-gateway/issues/87", state.Url);
        Assert.Equal("In progress", state.Status);
        Assert.False(state.IsArchived);
        Assert.Equal("P1", state.Fields["Priority"]);
        Assert.Equal("deniz-k, octocat", state.Fields["Assignees"]);
        Assert.Equal("Sprint 14", state.Fields["Iteration"]);
        Assert.Equal("5", state.Fields["Story points"]);
        Assert.Equal("2026-09-12", state.Fields["Due"]);
        Assert.False(state.Fields.ContainsKey("Notes"));
        Assert.False(state.Fields.ContainsKey("Title"));
        Assert.Equal("#87 Rate limit the poller", BoardTranslator.Describe(state));
    }

    /// <summary>A draft has no issue behind it: its title lives in the title field, and it has no page.</summary>
    [Fact]
    public void A_draft_card_takes_its_title_from_the_title_field_and_has_no_page()
    {
        var item = Item(
            """
            {
              "id": 14,
              "content_type": "DraftIssue",
              "content": null,
              "updated_at": "2026-09-06T11:30:00Z",
              "archived_at": "2026-09-06T11:40:00Z",
              "fields": [
                { "id": 1, "name": "Title", "data_type": "title", "value": { "raw": "Think about caching", "html": "Think about caching" } },
                { "id": 3, "name": "Status", "data_type": "single_select", "value": null }
              ]
            }
            """);

        var state = BoardTranslator.Snapshot(item, statusFieldId: null);

        Assert.Equal("Think about caching", state.Title);
        Assert.Null(state.Url);
        Assert.Null(state.Status);
        Assert.True(state.IsArchived);
        Assert.Equal("Draft: Think about caching", BoardTranslator.Describe(state));
    }

    [Fact]
    public void The_status_field_is_the_one_called_status_or_failing_that_the_first_single_select()
    {
        var fields = new List<GhProjectField>
        {
            new() { Id = 1, Name = "Title", DataType = "title" },
            new() { Id = 4, Name = "Priority", DataType = "single_select" },
            new() { Id = 3, Name = "status", DataType = "single_select" },
        };

        Assert.Equal(3, BoardTranslator.StatusFieldOf(fields)!.Id);
        Assert.Equal(4, BoardTranslator.StatusFieldOf(fields.Where(f => f.Id != 3))!.Id);
        Assert.Null(BoardTranslator.StatusFieldOf(fields.Where(f => f.Id == 1)));
    }

    // ---- What changed --------------------------------------------------------

    [Fact]
    public void A_card_that_arrived_is_announced_with_the_column_it_landed_in()
    {
        var alerts = BoardTranslator.Diff(Board, Cards(), Cards(("13", "Todo")), onlyStatus: true, complete: true, Now);

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertKind.Board, alert.Kind);
        Assert.Equal("Added to Roadmap", alert.Title);
        Assert.Equal("#13 Card 13", alert.Detail);
        Assert.Equal("Status · Todo", alert.Note);
        Assert.Equal("acme/#12", alert.Repository);
        Assert.Equal("https://github.com/acme/api-gateway/issues/13", alert.Url);
        Assert.StartsWith("board:12:13:added:", alert.Id);
        Assert.Contains(alert.Fields!, f => f.Name == "Status" && f.Value == "Todo");
    }

    [Fact]
    public void A_card_that_changed_column_is_announced_as_a_move_with_both_ends()
    {
        var alerts = BoardTranslator.Diff(Board, Cards(("13", "Todo")), Cards(("13", "In progress")), onlyStatus: true, complete: true, Now);

        var alert = Assert.Single(alerts);
        Assert.Equal("Moved to In progress", alert.Title);
        Assert.Equal("Status · Todo → In progress", alert.Note);
        Assert.StartsWith("board:12:13:moved:", alert.Id);
    }

    [Fact]
    public void A_card_archived_or_gone_is_announced_and_a_card_left_alone_is_not()
    {
        var before = Cards(("13", "Done"), ("14", "Done"), ("15", "Todo"));
        var after = Cards(("14", "Done"), ("15", "Todo"));
        after["14"].IsArchived = true;

        var alerts = BoardTranslator.Diff(Board, before, after, onlyStatus: true, complete: true, Now);

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Title == "Archived" && a.Detail == "#14 Card 14");
        Assert.Contains(alerts, a => a.Title == "Removed from Roadmap" && a.Detail == "#13 Card 13" && a.Timestamp == Now);
        Assert.DoesNotContain(alerts, a => a.Detail == "#15 Card 15");
    }

    /// <summary>A reading that stopped short of the whole board cannot say a card is gone.</summary>
    [Fact]
    public void A_partial_reading_does_not_take_a_missing_card_for_a_removed_one()
    {
        var alerts = BoardTranslator.Diff(Board, Cards(("13", "Done")), Cards(), onlyStatus: true, complete: false, Now);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Other_fields_changing_are_news_only_when_asked_for()
    {
        var before = Cards(("13", "Todo"));
        before["13"].Fields["Priority"] = "P2";
        var after = Cards(("13", "Todo"));
        after["13"].Fields["Priority"] = "P1";
        after["13"].Fields["Iteration"] = "Sprint 14";

        Assert.Empty(BoardTranslator.Diff(Board, before, after, onlyStatus: true, complete: true, Now));

        var alert = Assert.Single(BoardTranslator.Diff(Board, before, after, onlyStatus: false, complete: true, Now));
        Assert.Equal("2 fields changed", alert.Title);
        Assert.Equal("Priority · P2 → P1, Iteration · none → Sprint 14", alert.Note);
        Assert.StartsWith("board:12:13:edited:", alert.Id);
    }

    [Fact]
    public void A_column_change_outranks_the_other_fields_that_changed_with_it()
    {
        var before = Cards(("13", "Todo"));
        before["13"].Fields["Priority"] = "P2";
        var after = Cards(("13", "Done"));
        after["13"].Fields["Priority"] = "P1";

        var alert = Assert.Single(BoardTranslator.Diff(Board, before, after, onlyStatus: false, complete: true, Now));
        Assert.Equal("Moved to Done", alert.Title);
    }

    // ---- Values ----------------------------------------------------------------

    [Theory]
    [InlineData("\"plain\"", "plain")]
    [InlineData("42", "42")]
    [InlineData("true", "yes")]
    [InlineData("null", null)]
    [InlineData("{ \"name\": { \"raw\": \"Done\", \"html\": \"<b>Done</b>\" } }", "Done")]
    [InlineData("{ \"title\": { \"raw\": \"Sprint 3\" }, \"start_date\": \"2026-09-01\" }", "Sprint 3")]
    [InlineData("[ { \"login\": \"a\" }, { \"login\": \"b\" } ]", "a, b")]
    [InlineData("[ { \"name\": \"bug\" }, { \"name\": \"ux\" } ]", "bug, ux")]
    [InlineData("{ \"colour\": \"red\" }", null)]
    [InlineData("\"   \"", null)]
    public void A_field_value_becomes_one_line_of_text_whatever_its_shape(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, BoardTranslator.DescribeValue(document.RootElement.Clone()));
    }

    // ---- Helpers ---------------------------------------------------------------

    private static GhProjectItem Item(string json) =>
        JsonSerializer.Deserialize<GhProjectItem>(json)!;

    private static Dictionary<string, BoardItemState> Cards(params (string Id, string? Status)[] cards)
    {
        var result = new Dictionary<string, BoardItemState>(StringComparer.Ordinal);

        foreach (var (id, status) in cards)
        {
            var state = new BoardItemState
            {
                ContentType = "Issue",
                Number = int.Parse(id),
                Title = $"Card {id}",
                Url = $"https://github.com/acme/api-gateway/issues/{id}",
                Status = status,
                UpdatedAt = Now - TimeSpan.FromMinutes(5),
            };

            if (status is not null)
            {
                state.Fields["Status"] = status;
            }

            result[id] = state;
        }

        return result;
    }
}
