using GitAlert.Core;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

public class AlertViewModelTests
{
    private static Alert Push(string? detail) => new()
    {
        Id = "acc|commit:abc1234",
        Kind = AlertKind.Push,
        Title = "New commit on main",
        Detail = detail,
        Repository = "acme/api-gateway",
        Actor = "octocat",
        Timestamp = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero),
    };

    /// <summary>
    /// A push leads with its message, and the meta line under it carries the headline instead.
    /// The tooltip already opens with the headline, so it must not carry it a second time.
    /// </summary>
    [Fact]
    public void The_tooltip_names_the_headline_once()
    {
        var row = new AlertViewModel(Push("fix: the thing"));

        var lines = row.Tooltip.Split('\n');

        Assert.Equal("New commit on main", lines[0]);
        Assert.Equal("fix: the thing", lines[1]);
        Assert.Equal("acme/api-gateway · octocat", lines[2]);
        Assert.Single(lines, line => line.Contains("New commit on main"));
    }

    [Fact]
    public void The_row_meta_still_carries_the_headline_because_the_row_leads_with_the_message()
    {
        var row = new AlertViewModel(Push("fix: the thing"));

        Assert.Equal("fix: the thing", row.PrimaryText);
        Assert.Equal("New commit on main · octocat", row.RowMeta);
        Assert.Equal("acme/api-gateway · New commit on main · octocat", row.Meta);
    }

    [Fact]
    public void A_push_without_a_message_leads_with_the_headline()
    {
        var row = new AlertViewModel(Push(null));

        Assert.Equal("New commit on main", row.PrimaryText);
        Assert.Equal("octocat", row.RowMeta);
        Assert.StartsWith("New commit on main\nacme/api-gateway · octocat", row.Tooltip);
    }
}
