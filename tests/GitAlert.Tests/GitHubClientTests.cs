using System.Net;
using System.Net.Http;
using GitAlert.Core;
using GitAlert.GitHub;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The client on the days GitHub does not simply answer: refusals, outages, throttling, nonsense
/// on the wire. Almost none of this is reachable by hand, and all of it decides what the user is
/// told and whether the next poll still works.
/// </summary>
public class GitHubClientTests
{
    private static readonly RepoRef Repo = new("acme", "api-gateway");

    private static (GitHubClient Client, StubHandler Handler) Build(
        Func<RecordedRequest, HttpResponseMessage> respond,
        string? token = "ghp_token")
    {
        var handler = new StubHandler(respond);
        var client = new GitHubClient(new HttpClient(handler));
        client.SetToken(token);
        return (client, handler);
    }

    // ---- What a refusal is called ------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitHubErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, GitHubErrorKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, GitHubErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, GitHubErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, GitHubErrorKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GitHubErrorKind.ServerError)]
    [InlineData(HttpStatusCode.UnprocessableEntity, GitHubErrorKind.Unknown)]
    public async Task A_refusal_is_named_by_what_the_user_can_do_about_it(
        HttpStatusCode status,
        GitHubErrorKind expected)
    {
        var (client, _) = Build(_ => Responses.Status(status));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Equal(expected, error.Kind);
    }

    /// <summary>
    /// GitHub says 403 both for a scope the token does not have and for a budget it has spent.
    /// They are told apart by the remaining count, and they need different advice.
    /// </summary>
    [Fact]
    public async Task A_forbidden_with_nothing_left_is_throttling_rather_than_a_missing_scope()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(11).ToUnixTimeSeconds();

        var (client, _) = Build(_ => Responses.Status(
            HttpStatusCode.Forbidden,
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-limit", "5000"),
            ("x-ratelimit-reset", reset.ToString())));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Equal(GitHubErrorKind.RateLimited, error.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), error.RetryAt);
        Assert.Contains("Rate limited until", error.UserMessage);
    }

    /// <summary>
    /// A secondary limit - too many requests in a burst - is a 403 with budget to spare and a
    /// retry-after. Read by the remaining count alone it was a missing scope, which sends
    /// people off to mint a new token for a problem that goes away on its own in a minute.
    /// </summary>
    [Fact]
    public async Task A_forbidden_with_a_retry_after_is_throttling_and_says_when_to_come_back()
    {
        var (client, _) = Build(_ => Responses.Json(
            HttpStatusCode.Forbidden,
            """{"message":"You have exceeded a secondary rate limit."}""",
            ("retry-after", "45"),
            ("x-ratelimit-remaining", "4990"),
            ("x-ratelimit-limit", "5000")));

        var before = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Equal(GitHubErrorKind.RateLimited, error.Kind);
        Assert.NotNull(error.RetryAt);
        Assert.InRange(error.RetryAt.Value, before.AddSeconds(44), DateTimeOffset.UtcNow.AddSeconds(46));
    }

    [Fact]
    public async Task A_forbidden_with_budget_left_is_a_missing_scope_and_says_what_github_said()
    {
        var (client, _) = Build(_ => Responses.Json(
            HttpStatusCode.Forbidden,
            """{"message":"Resource not accessible by personal access token"}""",
            ("x-ratelimit-remaining", "4998"),
            ("x-ratelimit-limit", "5000")));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Equal(GitHubErrorKind.Forbidden, error.Kind);
        Assert.Equal("Resource not accessible by personal access token", error.Message);
    }

    /// <summary>A missing repository and a repository the token cannot see look identical, and the
    /// message has to cover both without accusing the user of either.</summary>
    [Fact]
    public async Task A_missing_repository_is_named_in_the_message()
    {
        var (client, _) = Build(_ => Responses.Status(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Contains("acme/api-gateway", error.Message);
        Assert.Contains("token cannot see it", error.Message);
    }

    // ---- What happens to the connection ------------------------------------

    /// <summary>
    /// Responses are read with ResponseHeadersRead, so an undisposed one keeps its connection.
    /// Every non-success leaves by exception, which is exactly the path that used to skip the
    /// disposal - one leaked connection per poll, for every repository the token cannot see.
    /// </summary>
    [Fact]
    public async Task A_refused_response_is_released_rather_than_left_to_the_finaliser()
    {
        HttpResponseMessage? sent = null;
        var (client, _) = Build(_ => sent = Responses.Status(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.True(Responses.BodyOf(sent!).Disposed, "the response body was never closed");
    }

    [Fact]
    public async Task A_successful_response_is_released_too()
    {
        HttpResponseMessage? sent = null;
        var (client, _) = Build(_ => sent = Responses.Ok("""{"login":"octocat"}"""));

        await client.GetAuthenticatedUserAsync();

        Assert.True(Responses.BodyOf(sent!).Disposed);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_released_as_well()
    {
        HttpResponseMessage? sent = null;
        var (client, _) = Build(_ => sent = Responses.Ok("<html>a proxy sign-in page</html>"));

        await Assert.ThrowsAsync<GitHubException>(() => client.GetAuthenticatedUserAsync());

        Assert.True(Responses.BodyOf(sent!).Disposed);
    }

    /// <summary>
    /// Bodies are streamed, and HttpClient's timeout stops at the headers of a streamed
    /// response. A connection that went quiet after them used to hold the poll loop for as long
    /// as the app ran, with the status stuck on "Checking GitHub…".
    /// </summary>
    [Fact]
    public async Task A_body_that_stalls_after_the_headers_is_a_timeout_rather_than_a_hang()
    {
        var (client, _) = Build(_ => Responses.Stalled());
        client.BodyReadTimeout = TimeSpan.FromMilliseconds(200);

        var error = await Assert.ThrowsAsync<GitHubException>(
            () => client.GetRepositoryAsync(Repo).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(GitHubErrorKind.Network, error.Kind);
        Assert.Contains("timed out", error.Message);
    }

    /// <summary>The caller's own cancellation is still theirs, not dressed up as a network fault.</summary>
    [Fact]
    public async Task Cancelling_a_stalled_read_is_reported_as_a_cancellation()
    {
        var (client, _) = Build(_ => Responses.Stalled());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetRepositoryAsync(Repo, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ---- Nonsense on the wire ----------------------------------------------

    [Fact]
    public async Task A_body_that_is_not_json_is_reported_rather_than_thrown_raw()
    {
        var (client, _) = Build(_ => Responses.Ok("not json at all"));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetAuthenticatedUserAsync());

        Assert.Equal(GitHubErrorKind.Unknown, error.Kind);
        Assert.Contains("could not read", error.Message);
    }

    [Fact]
    public async Task A_body_of_literal_null_is_not_mistaken_for_a_user()
    {
        var (client, _) = Build(_ => Responses.Ok("null"));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetAuthenticatedUserAsync());

        Assert.Contains("empty user", error.Message);
    }

    /// <summary>
    /// A commit that regenerated something enormous would otherwise be deserialised in full,
    /// holding the whole patch plus the objects built from it.
    /// </summary>
    [Fact]
    public async Task A_response_without_end_stops_at_the_ceiling_instead_of_growing()
    {
        var (client, _) = Build(_ => Responses.Endless("[{\"sha\":\"aaaaaaa\",\"x\":\""));

        var error = await Assert.ThrowsAsync<GitHubException>(
            () => client.GetCommitHistoryAsync(Repo, page: 1));

        Assert.Contains("too large", error.Message);
    }

    /// <summary>An error body is a sentence, and reading one has to stay bounded too.</summary>
    [Fact]
    public async Task An_endless_error_body_does_not_hang_the_error_path()
    {
        var (client, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(new EndlessStream("{\"message\":\"")),
        });

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        // The message could not be read, so the client falls back to its own wording.
        Assert.Equal(GitHubErrorKind.Forbidden, error.Kind);
        Assert.Equal("GitHub refused the request.", error.Message);
    }

    // ---- When GitHub is not there at all -----------------------------------

    [Fact]
    public async Task An_unreachable_host_is_a_network_problem_rather_than_a_crash()
    {
        var (client, _) = Build(_ => throw new HttpRequestException("no such host"));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Equal(GitHubErrorKind.Network, error.Kind);
        Assert.Equal("Cannot reach GitHub.", error.UserMessage);
    }

    [Fact]
    public async Task A_request_that_times_out_is_a_network_problem_and_says_so()
    {
        var (client, _) = Build(_ => throw new TaskCanceledException("timed out"));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetRepositoryAsync(Repo));

        Assert.Equal(GitHubErrorKind.Network, error.Kind);
        Assert.Contains("timed out", error.Message);
    }

    /// <summary>
    /// Cancellation is the caller shutting the app down, not GitHub failing. Turning it into a
    /// network error would put a false problem on screen on the way out.
    /// </summary>
    [Fact]
    public async Task Cancelling_is_not_reported_as_a_network_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (client, _) = Build(_ => throw new TaskCanceledException("cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetRepositoryAsync(Repo, cts.Token));
    }

    // ---- Conditional requests ----------------------------------------------

    /// <summary>
    /// A weak etag carries its W/ prefix, and it has to go back out exactly as it came in - the
    /// header is compared byte for byte at the other end.
    /// </summary>
    [Fact]
    public async Task An_unchanged_resource_keeps_the_weak_etag_it_was_asked_with()
    {
        const string Weak = "W/\"abc123\"";

        var (client, handler) = Build(_ => Responses.Json(HttpStatusCode.NotModified, string.Empty));

        var result = await client.GetRepositoryEventsAsync(Repo, Weak);

        Assert.True(result.NotModified);
        Assert.Equal(Weak, result.ETag);
        Assert.Null(result.Value);
        Assert.Equal(Weak, handler.Requests[0].IfNoneMatch);
    }

    [Fact]
    public async Task No_etag_yet_means_no_conditional_header_at_all()
    {
        var (client, handler) = Build(_ => Responses.Ok("[]"));

        await client.GetRepositoryEventsAsync(Repo, etag: null);

        Assert.Null(handler.Requests[0].IfNoneMatch);
    }

    [Fact]
    public async Task A_changed_resource_hands_back_the_new_etag_to_ask_with_next_time()
    {
        var (client, _) = Build(_ => Responses.Ok("[]", ("ETag", "\"fresh\"")));

        var result = await client.GetRepositoryEventsAsync(Repo, "\"stale\"");

        Assert.False(result.NotModified);
        Assert.Equal("\"fresh\"", result.ETag);
    }

    [Fact]
    public async Task The_poll_interval_github_asks_for_is_carried_back()
    {
        var (client, _) = Build(_ => Responses.Ok("[]", ("x-poll-interval", "90")));

        var result = await client.GetInboxAsync(etag: null);

        Assert.Equal(TimeSpan.FromSeconds(90), result.PollInterval);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("")]
    public async Task A_poll_interval_that_is_not_a_number_is_ignored(string value)
    {
        var (client, _) = Build(_ => Responses.Ok("[]", ("x-poll-interval", value)));

        var result = await client.GetInboxAsync(etag: null);

        Assert.Null(result.PollInterval);
    }

    // ---- Rate limit bookkeeping --------------------------------------------

    [Fact]
    public async Task The_remaining_budget_is_read_off_a_successful_response()
    {
        var (client, _) = Build(_ => Responses.Ok(
            "[]",
            ("x-ratelimit-remaining", "4321"),
            ("x-ratelimit-limit", "5000"),
            ("x-ratelimit-reset", "1700000000")));

        await client.GetRepositoryEventsAsync(Repo, etag: null);

        Assert.True(client.RateLimit.IsKnown);
        Assert.Equal(4321, client.RateLimit.Remaining);
        Assert.Equal(5000, client.RateLimit.Limit);
    }

    [Fact]
    public async Task Half_a_set_of_rate_limit_headers_is_not_treated_as_a_reading()
    {
        var (client, _) = Build(_ => Responses.Ok("[]", ("x-ratelimit-remaining", "4321")));

        await client.GetRepositoryEventsAsync(Repo, etag: null);

        Assert.False(client.RateLimit.IsKnown);
    }

    // ---- What goes out on the wire -----------------------------------------

    [Fact]
    public async Task Every_request_identifies_itself_and_pins_the_api_version()
    {
        var (client, handler) = Build(_ => Responses.Ok("""{"login":"octocat"}"""));

        await client.GetAuthenticatedUserAsync();

        var sent = handler.Requests[0];
        Assert.StartsWith("GitAlert/", sent.UserAgent);
        Assert.Contains("application/vnd.github+json", sent.Accept);
        Assert.Equal("2022-11-28", sent.ApiVersion);
        Assert.Equal("Bearer ghp_token", sent.Authorization);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_sent_where_a_credential_would_go()
    {
        var (client, handler) = Build(_ => Responses.Ok("[]"), token: null);

        await client.GetRepositoryEventsAsync(Repo, etag: null);

        Assert.Null(handler.Requests[0].Authorization);
        Assert.False(client.HasToken);
    }

    [Theory]
    [InlineData("  ghp_padded  ", "Bearer ghp_padded")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public async Task A_pasted_token_is_trimmed_and_a_blank_one_is_no_token(string token, string? expected)
    {
        var (client, handler) = Build(_ => Responses.Ok("[]"), token);

        await client.GetRepositoryEventsAsync(Repo, etag: null);

        Assert.Equal(expected, handler.Requests[0].Authorization);
    }

    /// <summary>
    /// A ref can be a branch name, and a branch name can contain a slash. Left unescaped it would
    /// silently address a different path on the API.
    /// </summary>
    [Fact]
    public async Task A_ref_with_a_slash_in_it_stays_one_path_segment()
    {
        var (client, handler) = Build(_ => Responses.Ok("""{"sha":"abc","files":[]}"""));

        await client.GetCommitAsync(Repo, "feature/login");

        Assert.Equal("/repos/acme/api-gateway/commits/feature%2Flogin", handler.Requests[0].Path);
    }

    [Fact]
    public async Task A_page_number_below_one_is_lifted_rather_than_sent_as_it_is()
    {
        var (client, handler) = Build(_ => Responses.Ok("[]"));

        await client.GetCommitHistoryAsync(Repo, page: 0);

        Assert.Contains("page=1", handler.Requests[0].Query);
    }

    // ---- Paging ------------------------------------------------------------

    [Fact]
    public async Task Listing_repositories_stops_at_the_first_short_page()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"full_name":"acme/r{{i}}"}""")) + "]";

        var (client, handler) = Build(request =>
            Responses.Ok(request.Query.EndsWith("page=1") ? full : """[{"full_name":"acme/last"}]"""));

        var found = await client.GetMyRepositoriesAsync();

        Assert.Equal(101, found.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Listing_repositories_stops_on_an_empty_page_without_asking_again()
    {
        var (client, handler) = Build(_ => Responses.Ok("[]"));

        Assert.Empty(await client.GetMyRepositoriesAsync());
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A token that reaches thousands of repositories should not walk them all: the checklist
    /// stops being a way to choose anything long before that, and the search box takes over.
    /// </summary>
    [Fact]
    public async Task Listing_repositories_gives_up_rather_than_paging_forever()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"full_name":"acme/r{{i}}"}""")) + "]";

        var (client, handler) = Build(_ => Responses.Ok(full));

        var found = await client.GetMyRepositoriesAsync();

        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(500, found.Count);
    }

    [Fact]
    public async Task A_refusal_part_way_through_paging_is_reported_rather_than_returning_half()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"full_name":"acme/r{{i}}"}""")) + "]";

        var (client, _) = Build(request =>
            request.Query.EndsWith("page=1") ? Responses.Ok(full) : Responses.Status(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetMyRepositoriesAsync());

        Assert.Equal(GitHubErrorKind.Unauthorized, error.Kind);
    }

    // ---- Empty and missing payloads ----------------------------------------

    [Fact]
    public async Task A_pull_request_with_no_files_is_an_empty_list_rather_than_null()
    {
        var (client, _) = Build(_ => Responses.Ok("null"));

        Assert.Empty(await client.GetPullRequestFilesAsync(Repo, 12));
    }

    [Fact]
    public async Task A_commit_that_comes_back_as_null_is_reported_as_missing()
    {
        var (client, _) = Build(_ => Responses.Ok("null"));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetCommitAsync(Repo, "abcdef1234"));

        Assert.Equal(GitHubErrorKind.NotFound, error.Kind);
        Assert.Contains("abcdef1", error.Message);
    }

    [Fact]
    public async Task An_empty_history_page_is_an_empty_list()
    {
        var (client, _) = Build(_ => Responses.Ok("null"));

        Assert.Empty(await client.GetCommitHistoryAsync(Repo, page: 3));
    }
}
