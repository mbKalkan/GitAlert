using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class RepoRefTests
{
    [Theory]
    [InlineData("acme/api-gateway")]
    [InlineData("github.com/acme/api-gateway")]
    [InlineData("www.github.com/acme/api-gateway")]
    [InlineData("https://github.com/acme/api-gateway")]
    [InlineData("https://github.com/acme/api-gateway/")]
    [InlineData("https://github.com/acme/api-gateway.git")]
    [InlineData("http://github.com/acme/api-gateway")]
    [InlineData("git@github.com:acme/api-gateway.git")]
    [InlineData("  https://github.com/acme/api-gateway  ")]
    public void Parses_every_shape_a_user_might_paste(string input)
    {
        Assert.True(RepoRef.TryParse(input, out var repo));
        Assert.Equal("acme", repo!.Owner);
        Assert.Equal("api-gateway", repo.Name);
        Assert.Equal("acme/api-gateway", repo.FullName);
    }

    [Theory]
    [InlineData("https://github.com/acme/api-gateway/pull/12")]
    [InlineData("https://github.com/acme/api-gateway/issues/44#issuecomment-1")]
    [InlineData("https://github.com/acme/api-gateway/actions/runs/9876")]
    [InlineData("https://github.com/acme/api-gateway/tree/main/src")]
    public void Ignores_everything_after_the_repository_segment(string deepLink)
    {
        Assert.True(RepoRef.TryParse(deepLink, out var repo));
        Assert.Equal("acme/api-gateway", repo!.FullName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://github.com/")]
    [InlineData("acme/repo with spaces")]
    public void Rejects_input_that_is_not_a_repository(string? input)
    {
        Assert.False(RepoRef.TryParse(input, out var repo));
        Assert.Null(repo);
    }

    /// <summary>
    /// The scheme and host are stripped before the remainder is split, so a link to somewhere
    /// else used to parse as a repository named after that host - <c>gitlab.com/acme</c> for a
    /// GitLab link - and was accepted, only to be reported as missing on the next poll.
    /// </summary>
    [Theory]
    [InlineData("https://gitlab.com/acme/api-gateway")]
    [InlineData("https://bitbucket.org/acme/api-gateway")]
    [InlineData("https://github.example.com/acme/api-gateway")]
    [InlineData("git@gitlab.com:acme/api-gateway.git")]
    public void Rejects_a_link_to_somewhere_that_is_not_github(string input)
    {
        Assert.False(RepoRef.TryParse(input, out var repo));
        Assert.Null(repo);
    }

    /// <summary>GitHub logins are alphanumerics and inner hyphens, and nothing else.</summary>
    [Theory]
    [InlineData("-acme/api-gateway")]
    [InlineData("acme-/api-gateway")]
    [InlineData("ac.me/api-gateway")]
    [InlineData("ac_me/api-gateway")]
    public void Rejects_an_owner_that_is_not_a_github_login(string input)
    {
        Assert.False(RepoRef.TryParse(input, out _));
    }

    /// <summary>
    /// Git reserves both, and a URI folds <c>..</c> into the segment before it - so a settings
    /// file naming a repository <c>..</c> would have sent the token to a different endpoint.
    /// </summary>
    [Theory]
    [InlineData("acme/.")]
    [InlineData("acme/..")]
    [InlineData("acme/../user")]
    [InlineData("https://github.com/acme/..")]
    public void Rejects_the_names_git_reserves(string input)
    {
        Assert.False(RepoRef.TryParse(input, out _));
    }

    /// <summary>The same rules, exposed for the settings file to check what it loaded.</summary>
    [Fact]
    public void The_validation_the_parser_uses_is_available_on_its_own()
    {
        Assert.True(RepoRef.IsValidOwner("acme"));
        Assert.True(RepoRef.IsValidName("api.gateway"));

        Assert.False(RepoRef.IsValidOwner(null));
        Assert.False(RepoRef.IsValidOwner("ac.me"));
        Assert.False(RepoRef.IsValidName(null));
        Assert.False(RepoRef.IsValidName("."));
        Assert.False(RepoRef.IsValidName(".."));
        Assert.False(RepoRef.IsValidName("api/../../user"));
    }

    /// <summary>A repository name, unlike an owner, is allowed dots and underscores.</summary>
    [Theory]
    [InlineData("acme/api.gateway")]
    [InlineData("acme/api_gateway")]
    [InlineData("acme/.github")]
    public void Accepts_the_punctuation_a_repository_name_may_carry(string input)
    {
        Assert.True(RepoRef.TryParse(input, out var repo));
        Assert.Equal("acme", repo!.Owner);
    }

    [Fact]
    public void Comparison_ignores_case_so_a_repo_cannot_be_added_twice()
    {
        var lower = RepoRef.Parse("acme/api-gateway");
        var upper = RepoRef.Parse("ACME/API-Gateway");

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }

    [Fact]
    public void Builds_the_browser_url()
    {
        Assert.Equal("https://github.com/acme/api-gateway", RepoRef.Parse("acme/api-gateway").HtmlUrl);
    }

    [Fact]
    public void Parse_throws_on_nonsense()
    {
        Assert.Throws<FormatException>(() => RepoRef.Parse("nonsense"));
    }
}
