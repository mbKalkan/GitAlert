using GitAlert.Core;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The badge beside a project. It counts what is unread there, and reading an alert happens on
/// the row rather than on the group, so the group has to be told.
/// </summary>
public class ProjectGroupViewModelTests
{
    private static AlertViewModel Row(string id, bool read = false) =>
        new(new Alert
        {
            Id = id,
            Kind = AlertKind.Push,
            Title = $"Alert {id}",
            Repository = "acme/api-gateway",
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = read,
        });

    private static ProjectGroupViewModel Group(params AlertViewModel[] rows)
    {
        var group = new ProjectGroupViewModel("acme/api-gateway", "account");
        group.SetAlerts(rows);
        return group;
    }

    [Fact]
    public void The_badge_shows_what_is_unread_while_anything_is()
    {
        var group = Group(Row("a"), Row("b"), Row("c", read: true));

        Assert.Equal(2, group.UnreadCount);
        Assert.True(group.HasUnread);
        Assert.Equal("2", group.CountText);
    }

    [Fact]
    public void Reading_an_alert_lowers_the_badge_once_the_group_is_told()
    {
        var first = Row("a");
        var group = Group(first, Row("b"));

        first.MarkRead();
        group.Recount();

        Assert.Equal(1, group.UnreadCount);
        Assert.Equal("1", group.CountText);
    }

    /// <summary>
    /// With nothing unread the badge falls back to how much is in the project at all, so a
    /// project you have read through still says how much it holds rather than going blank.
    /// </summary>
    [Fact]
    public void Reading_everything_leaves_the_badge_showing_the_size_of_the_project()
    {
        var rows = new[] { Row("a"), Row("b") };
        var group = Group(rows);

        foreach (var row in rows)
        {
            row.MarkRead();
        }

        group.Recount();

        Assert.Equal(0, group.UnreadCount);
        Assert.False(group.HasUnread);
        Assert.Equal("2", group.CountText);
    }

    [Fact]
    public void Recounting_raises_a_change_for_the_text_the_badge_binds_to()
    {
        var first = Row("a");
        var group = Group(first, Row("b"));

        var changed = new List<string?>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        first.MarkRead();
        group.Recount();

        Assert.Contains(nameof(ProjectGroupViewModel.CountText), changed);
        Assert.Contains(nameof(ProjectGroupViewModel.HasUnread), changed);
    }
}
