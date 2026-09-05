using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The token an account card holds for the save, and the box that offers to replace it. Opening
/// that box used to throw the pending token away: a just-added account, whose only copy of its
/// token was pending, was then saved with none.
/// </summary>
public class AccountTokenTests
{
    [Fact]
    public void Opening_and_cancelling_the_replace_box_keeps_the_token_waiting_for_the_save()
    {
        using var account = new AccountViewModel(GitHubAccount.Create("octocat"), "ghp_new", Http(), _ => { })
        {
            PendingToken = "ghp_new",
        };

        account.BeginReplaceTokenCommand.Execute(null);
        account.CancelReplaceTokenCommand.Execute(null);

        Assert.Equal("ghp_new", account.PendingToken);
        Assert.False(account.IsReplacingToken);
    }

    [Fact]
    public async Task A_replacement_that_works_becomes_the_pending_token_and_the_notice_hears_of_it()
    {
        using var account = new AccountViewModel(GitHubAccount.Create("octocat"), token: null, Http(), _ => { });
        var raised = new List<string>();
        account.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        Assert.False(account.HasStoredToken);

        account.BeginReplaceTokenCommand.Execute(null);
        account.ReplacementToken = " ghp_fresh \n";
        await account.ApplyTokenCommand.ExecuteAsync(null);

        Assert.Equal("ghp_fresh", account.PendingToken);
        Assert.Null(account.ReplacementToken);
        Assert.True(account.HasStoredToken);
        Assert.Contains(nameof(AccountViewModel.HasStoredToken), raised);
        Assert.False(account.IsReplacingToken);
        Assert.Equal("octocat", account.Login);
    }

    [Fact]
    public async Task A_replacement_github_refuses_leaves_the_pending_token_alone()
    {
        using var account = new AccountViewModel(GitHubAccount.Create("octocat"), "ghp_old", Http(HttpStatusCode.Unauthorized), _ => { })
        {
            PendingToken = "ghp_old",
        };

        account.BeginReplaceTokenCommand.Execute(null);
        account.ReplacementToken = "ghp_bad";
        await account.ApplyTokenCommand.ExecuteAsync(null);

        Assert.Equal("ghp_old", account.PendingToken);
        Assert.True(account.IsReplacingToken);
        Assert.True(account.HasMessage);
    }

    private static HttpClient Http(HttpStatusCode status = HttpStatusCode.OK) =>
        new(new StubHandler(_ => status == HttpStatusCode.OK
            ? Responses.Ok("""{"login":"octocat"}""")
            : Responses.Status(status)));
}
