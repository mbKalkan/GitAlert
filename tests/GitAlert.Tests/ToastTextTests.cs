using GitAlert.Core;
using GitAlert.Services;
using Xunit;

namespace GitAlert.Tests;

/// <summary>What a toast says, written to fit a Windows balloon's 63-character title and 255 of text.</summary>
public class ToastTextTests
{
    private static Alert Alert(
        string title,
        string repository = "acme/api-gateway",
        string? detail = null,
        string? body = null,
        string? actor = null) => new()
    {
        Id = $"account|event:{Guid.NewGuid():N}",
        Kind = AlertKind.Issue,
        Title = title,
        Detail = detail,
        Body = body,
        Actor = actor,
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static string Plain(string key) => key;

    [Fact]
    public void One_alert_names_its_repository_its_headline_its_detail_and_the_first_line_of_what_was_said()
    {
        var (title, body) = ToastText.Compose(
            [Alert("Issue #77 opened", detail: "Rate limit the poller", body: "The poller keeps asking.\nEvery two minutes.")],
            Plain);

        Assert.Equal("acme/api-gateway - Issue #77 opened", title);
        Assert.Equal("Rate limit the poller\nThe poller keeps asking.", body);
    }

    [Fact]
    public void A_comment_does_not_read_its_own_first_line_twice()
    {
        var (_, body) = ToastText.Compose(
            [Alert("New comment on issue #77", detail: "Reproduced on the 4070 with the 560…", body: "Reproduced on the 4070 with the 560 driver.\nNot on the 3060.")],
            Plain);

        Assert.Equal("Reproduced on the 4070 with the 560…", body);
    }

    [Fact]
    public void An_alert_with_nothing_to_say_names_who_did_it_or_repeats_the_headline()
    {
        Assert.Equal("by deniz", ToastText.Compose([Alert("deniz starred the repository", actor: "deniz")], Plain).Body);
        Assert.Equal("Branch main created", ToastText.Compose([Alert("Branch main created")], Plain).Body);
    }

    [Fact]
    public void A_long_repository_name_gives_way_before_the_headline_does()
    {
        var alert = Alert("Issue #1234 reopened", repository: "ArkheonTechnologies/arkheon-platform-services");

        var (title, _) = ToastText.Compose([alert], Plain);

        Assert.Equal("arkheon-platform-services - Issue #1234 reopened", title);
        Assert.True(title.Length <= ToastText.MaxTitleLength);
    }

    [Fact]
    public void A_board_reads_by_its_title_and_loses_its_owner_the_same_way()
    {
        var alert = Alert("Moved to In review", repository: "ArkheonTechnologies/#4");

        var (title, _) = ToastText.Compose([alert], key => key == "ArkheonTechnologies/#4" ? "ArkheonTechnologies / Roadmap and long-term planning" : key);

        Assert.Equal("Roadmap and long-term planning - Moved to In review", title);
    }

    [Fact]
    public void When_even_the_short_form_is_too_long_the_headline_is_the_title()
    {
        var alert = Alert(new string('x', 70), repository: "acme/api-gateway");

        var (title, _) = ToastText.Compose([alert], Plain);

        Assert.Equal(ToastText.MaxTitleLength, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void A_batch_lists_each_alert_with_its_detail_on_its_own_line()
    {
        var (title, body) = ToastText.Compose(
            [
                Alert("Issue #77 opened", detail: "Rate limit the poller"),
                Alert("Moved to In review", repository: "acme/#4", detail: "#77 Rate limit the poller"),
                Alert("CI failed (#212)", repository: "mbKalkan/GitAlert"),
            ],
            key => key == "acme/#4" ? "acme / Roadmap" : key);

        Assert.Equal("3 new alerts", title);
        Assert.Equal(
            "acme/api-gateway: Issue #77 opened · Rate limit the poller\n"
            + "acme / Roadmap: Moved to In review · #77 Rate limit the poller\n"
            + "mbKalkan/GitAlert: CI failed (#212)",
            body);
    }

    [Fact]
    public void A_big_batch_names_three_and_counts_the_rest_within_the_balloon()
    {
        var alerts = Enumerable.Range(1, 12)
            .Select(i => Alert($"Issue #{i} opened", detail: new string('d', 200)))
            .ToList();

        var (title, body) = ToastText.Compose(alerts, Plain);

        Assert.Equal("12 new alerts", title);
        Assert.True(body.Length <= ToastText.MaxBodyLength, $"{body.Length} chars");

        var lines = body.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.All(lines.Take(3), line => Assert.EndsWith("…", line));
        Assert.Equal("+9 more", lines[3]);
    }

    [Fact]
    public void A_single_long_body_stays_within_the_balloon()
    {
        var (_, body) = ToastText.Compose([Alert("Issue #77 opened", detail: new string('t', 100), body: new string('b', 300))], Plain);

        Assert.Equal(ToastText.MaxBodyLength, body.Length);
        Assert.EndsWith("…", body);
    }
}
