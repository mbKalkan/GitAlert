using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Board links as people paste them. A board is named by an owner and a number, and the owner is
/// an organisation or a user depending on which of two endpoints answers for it.
/// </summary>
public class BoardRefTests
{
    [Theory]
    [InlineData("https://github.com/orgs/acme/projects/12", "acme", BoardOwnerKind.Organization, 12)]
    [InlineData("https://github.com/orgs/acme/projects/12/views/3?layout=board", "acme", BoardOwnerKind.Organization, 12)]
    [InlineData("github.com/users/deniz-k/projects/3", "deniz-k", BoardOwnerKind.User, 3)]
    [InlineData("HTTPS://GITHUB.COM/USERS/deniz/PROJECTS/7/", "deniz", BoardOwnerKind.User, 7)]
    [InlineData("acme/projects/12", "acme", BoardOwnerKind.Unknown, 12)]
    [InlineData("acme/#12", "acme", BoardOwnerKind.Unknown, 12)]
    public void A_board_link_is_taken_apart_into_owner_kind_and_number(
        string input,
        string owner,
        BoardOwnerKind kind,
        int number)
    {
        Assert.True(BoardRef.TryParse(input, out var board));
        Assert.Equal(owner, board.Owner);
        Assert.Equal(kind, board.OwnerKind);
        Assert.Equal(number, board.Number);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme/api-gateway")]
    [InlineData("https://github.com/acme/api-gateway/pull/12")]
    [InlineData("https://github.com/orgs/acme/projects/zero")]
    [InlineData("https://github.com/orgs/acme/projects/0")]
    [InlineData("https://gitlab.com/orgs/acme/projects/12")]
    [InlineData("acme/#")]
    public void Anything_that_is_not_a_board_link_is_refused(string input)
    {
        Assert.False(BoardRef.TryParse(input, out _));
    }

    /// <summary>
    /// The flyout groups by a name, and a board's name must never be mistaken for a repository's:
    /// git does not allow a hash in a repository name, so <c>owner/#12</c> is safe to tell apart.
    /// </summary>
    [Fact]
    public void The_key_is_shaped_so_it_cannot_be_a_repository_name()
    {
        var board = new BoardRef("acme", BoardOwnerKind.Organization, 12);

        Assert.Equal("acme/#12", board.Key);
        Assert.True(BoardRef.IsKey(board.Key));
        Assert.False(RepoRef.TryParse(board.Key, out _));
        Assert.False(BoardRef.IsKey("acme/api-gateway"));
        Assert.False(BoardRef.IsKey("acme/#0"));
    }

    [Fact]
    public void The_endpoints_and_the_page_follow_the_owner_kind()
    {
        var org = new BoardRef("acme", BoardOwnerKind.Organization, 12);
        var user = new BoardRef("deniz", BoardOwnerKind.User, 3);

        Assert.Equal("/orgs/acme/projectsV2/12", org.ApiPath);
        Assert.Equal("https://github.com/orgs/acme/projects/12", org.HtmlUrl);
        Assert.Equal("/users/deniz/projectsV2/3", user.ApiPath);
        Assert.Equal("https://github.com/users/deniz/projects/3", user.HtmlUrl);
        Assert.Equal(BoardOwnerKind.Organization, user.AsOrganization().OwnerKind);
    }

    /// <summary>An organisation and a user cannot share a login, so the kind does not tell boards apart.</summary>
    [Fact]
    public void Two_refs_are_the_same_board_whatever_the_link_said_about_the_owner()
    {
        Assert.Equal(new BoardRef("Acme", BoardOwnerKind.Organization, 12), new BoardRef("acme", BoardOwnerKind.Unknown, 12));
        Assert.NotEqual(new BoardRef("acme", BoardOwnerKind.Organization, 12), new BoardRef("acme", BoardOwnerKind.Organization, 13));
    }
}
