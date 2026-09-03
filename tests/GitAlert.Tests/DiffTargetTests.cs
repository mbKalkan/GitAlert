using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class DiffTargetTests
{
    private static Alert Push(string id, string? url, string? head = null, string? basis = null) => new()
    {
        Id = id,
        Kind = AlertKind.Push,
        Title = "New commit on main",
        Repository = "acme/api-gateway",
        Timestamp = DateTimeOffset.UtcNow,
        Url = url,
        DiffHead = head,
        DiffBase = basis,
    };

    [Fact]
    public void The_stored_fields_win_when_they_are_there()
    {
        var target = DiffTarget.For(Push("acct|commit:ccc", "https://github.com/a/b/commit/zzz", head: "ccc", basis: "aaa"));

        Assert.Equal("ccc", target.Head);
        Assert.Equal("aaa", target.Base);
    }

    /// <summary>
    /// Everything recorded before 1.2.0 has no diff fields at all. Those alerts are not lost
    /// causes: the commit is still named in the URL and again in the id.
    /// </summary>
    [Fact]
    public void An_alert_from_before_diffs_existed_recovers_its_commit_from_the_url()
    {
        var target = DiffTarget.For(Push(
            "fcd687050e48490e81fd06a69cd314fe|commit:ab74a7b9f7859ee49220d8fb2550495fc683c1c3",
            "https://github.com/acme/api-gateway/commit/ab74a7b9f7859ee49220d8fb2550495fc683c1c3"));

        Assert.True(target.IsKnown);
        Assert.Equal("ab74a7b9f7859ee49220d8fb2550495fc683c1c3", target.Head);
        Assert.Null(target.Base);
    }

    [Fact]
    public void A_compare_url_recovers_both_ends_of_the_range()
    {
        var target = DiffTarget.For(Push(
            "acct|commit:bbbbbbb",
            "https://github.com/acme/api-gateway/compare/aaaaaaa...bbbbbbb"));

        Assert.Equal("bbbbbbb", target.Head);
        Assert.Equal("aaaaaaa", target.Base);
    }

    [Fact]
    public void The_id_is_the_last_resort_when_there_is_no_usable_url()
    {
        var target = DiffTarget.For(Push("acct|commit:ddd1234", url: "https://github.com/acme/api-gateway"));

        Assert.Equal("ddd1234", target.Head);
    }

    [Fact]
    public void A_pull_request_url_recovers_its_number()
    {
        var alert = new Alert
        {
            Id = "acct|event:99",
            Kind = AlertKind.PullRequest,
            Title = "PR #88 opened",
            Repository = "acme/api-gateway",
            Timestamp = DateTimeOffset.UtcNow,
            Url = "https://github.com/acme/api-gateway/pull/88",
        };

        Assert.Equal(88, DiffTarget.For(alert).PullRequest);
    }

    [Fact]
    public void An_alert_that_points_at_nothing_stays_unknown()
    {
        var alert = new Alert
        {
            Id = "acct|run:9876",
            Kind = AlertKind.Workflow,
            Title = "CI failed",
            Repository = "acme/api-gateway",
            Timestamp = DateTimeOffset.UtcNow,
            Url = "https://github.com/acme/api-gateway/actions/runs/9876",
        };

        Assert.False(DiffTarget.For(alert).IsKnown);
        Assert.False(alert.HasDiff);
    }
}
