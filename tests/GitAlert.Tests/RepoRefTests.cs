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
