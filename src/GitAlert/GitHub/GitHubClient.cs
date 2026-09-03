using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GitAlert.Core;

namespace GitAlert.GitHub;

/// <summary>
/// A small, purpose-built GitHub REST client. It speaks only the handful of endpoints GitAlert
/// needs, and it is deliberately conditional-request first: every poll sends the previous ETag so
/// unchanged resources come back as 304 and cost nothing against the hourly rate limit.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    private const string ApiRoot = "https://api.github.com";
    private const string ApiVersion = "2022-11-28";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private string? _token;

    private static readonly string UserAgent =
        $"GitAlert/{typeof(GitHubClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    public GitHubClient(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });

        if (_ownsClient)
        {
            _http.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public RateLimitStatus RateLimit { get; private set; } = RateLimitStatus.Unknown;

    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

    /// <summary>The token this client authenticates with, so callers can tell when it changed.</summary>
    public string? Token => _token;

    public void SetToken(string? token) => _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    /// <summary>Verifies a token and returns who it belongs to.</summary>
    public async Task<GhUser> GetAuthenticatedUserAsync(CancellationToken ct = default)
    {
        var response = await SendAsync(HttpMethod.Get, "/user", etag: null, ct).ConfigureAwait(false);
        return await ReadJsonAsync<GhUser>(response, ct).ConfigureAwait(false)
            ?? throw new GitHubException(GitHubErrorKind.Unknown, "GitHub returned an empty user.");
    }

    /// <summary>Confirms the token can see a repository, and reports whether it is private.</summary>
    public async Task<GhRepository> GetRepositoryAsync(RepoRef repo, CancellationToken ct = default)
    {
        var response = await SendAsync(HttpMethod.Get, $"/repos/{repo.Owner}/{repo.Name}", etag: null, ct).ConfigureAwait(false);
        return await ReadJsonAsync<GhRepository>(response, ct).ConfigureAwait(false)
            ?? throw new GitHubException(GitHubErrorKind.NotFound, $"{repo.FullName} was not found.");
    }

    /// <summary>
    /// The repository activity timeline: pushes, pull requests, issues, comments, releases,
    /// branch and tag creation, forks and stars.
    /// </summary>
    public Task<ConditionalResponse<List<GhEvent>>> GetRepositoryEventsAsync(
        RepoRef repo,
        string? etag,
        CancellationToken ct = default) =>
        GetConditionalAsync<List<GhEvent>>($"/repos/{repo.Owner}/{repo.Name}/events?per_page=50", etag, ct);

    /// <summary>
    /// Commits on the default branch, newest first.
    /// </summary>
    /// <remarks>
    /// The events timeline is the richer source, but GitHub populates it lazily - for private
    /// repositories it can run hours or days behind, and a freshly created repository may have no
    /// events at all. This endpoint reflects a push immediately, so it is what makes push alerts
    /// actually timely.
    /// </remarks>
    public Task<ConditionalResponse<List<GhCommit>>> GetCommitsAsync(
        RepoRef repo,
        string? etag,
        CancellationToken ct = default) =>
        GetConditionalAsync<List<GhCommit>>($"/repos/{repo.Owner}/{repo.Name}/commits?per_page=20", etag, ct);

    /// <summary>One commit together with every file it touched and their unified diffs.</summary>
    public async Task<GhCommitWithFiles> GetCommitAsync(RepoRef repo, string sha, CancellationToken ct = default)
    {
        var path = $"/repos/{repo.Owner}/{repo.Name}/commits/{Uri.EscapeDataString(sha)}";
        var response = await SendAsync(HttpMethod.Get, path, etag: null, ct).ConfigureAwait(false);

        return await ReadJsonAsync<GhCommitWithFiles>(response, ct).ConfigureAwait(false)
            ?? throw new GitHubException(GitHubErrorKind.NotFound, $"Commit {Short(sha)} was not found.");
    }

    /// <summary>
    /// The combined diff between two commits. This is what a push of several commits should show:
    /// the net effect, the same view the compare page on github.com gives.
    /// </summary>
    public async Task<GhComparison> GetComparisonAsync(
        RepoRef repo,
        string basis,
        string head,
        CancellationToken ct = default)
    {
        var range = $"{Uri.EscapeDataString(basis)}...{Uri.EscapeDataString(head)}";
        var path = $"/repos/{repo.Owner}/{repo.Name}/compare/{range}";
        var response = await SendAsync(HttpMethod.Get, path, etag: null, ct).ConfigureAwait(false);

        return await ReadJsonAsync<GhComparison>(response, ct).ConfigureAwait(false)
            ?? throw new GitHubException(GitHubErrorKind.NotFound, "That range of commits was not found.");
    }

    /// <summary>The files a pull request changes.</summary>
    public async Task<List<GhFileChange>> GetPullRequestFilesAsync(
        RepoRef repo,
        int number,
        CancellationToken ct = default)
    {
        var path = $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/files?per_page=100";
        var response = await SendAsync(HttpMethod.Get, path, etag: null, ct).ConfigureAwait(false);

        return await ReadJsonAsync<List<GhFileChange>>(response, ct).ConfigureAwait(false) ?? [];
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>GitHub Actions runs, newest first. Not part of the events timeline.</summary>
    public Task<ConditionalResponse<GhWorkflowRunsPage>> GetWorkflowRunsAsync(
        RepoRef repo,
        string? etag,
        CancellationToken ct = default) =>
        GetConditionalAsync<GhWorkflowRunsPage>(
            $"/repos/{repo.Owner}/{repo.Name}/actions/runs?per_page=20&exclude_pull_requests=true",
            etag,
            ct);

    /// <summary>The signed-in user's notification inbox: mentions, review requests, assignments.</summary>
    public Task<ConditionalResponse<List<GhNotification>>> GetInboxAsync(
        string? etag,
        CancellationToken ct = default) =>
        GetConditionalAsync<List<GhNotification>>("/notifications?all=false&per_page=50", etag, ct);

    public async Task MarkInboxReadAsync(CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Put, "/notifications", etag: null);
        request.Content = new StringContent(
            $"{{\"last_read_at\":\"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\",\"read\":true}}");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await ExecuteAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "/notifications", ct).ConfigureAwait(false);
    }

    public async Task MarkThreadReadAsync(string threadId, CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"/notifications/threads/{threadId}", etag: null);
        using var response = await ExecuteAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"thread {threadId}", ct).ConfigureAwait(false);
    }

    private async Task<ConditionalResponse<T>> GetConditionalAsync<T>(string path, string? etag, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path, etag);
        using var response = await ExecuteAsync(request, ct).ConfigureAwait(false);

        CaptureRateLimit(response);

        var pollInterval = ReadPollInterval(response);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return ConditionalResponse<T>.Unchanged(etag, pollInterval);
        }

        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);

        return new ConditionalResponse<T>
        {
            NotModified = false,
            Value = await ReadJsonAsync<T>(response, ct).ConfigureAwait(false),
            ETag = response.Headers.ETag?.ToString(),
            PollInterval = pollInterval,
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? etag, CancellationToken ct)
    {
        using var request = CreateRequest(method, path, etag);
        var response = await ExecuteAsync(request, ct).ConfigureAwait(false);

        CaptureRateLimit(response);
        await EnsureSuccessAsync(response, path, ct).ConfigureAwait(false);
        return response;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? etag)
    {
        var request = new HttpRequestMessage(method, ApiRoot + path);

        // Set on the request rather than the client: one HttpClient is shared by every account.
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);

        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        if (!string.IsNullOrEmpty(etag))
        {
            // AddWithoutValidation keeps weak ETags ( W/"..." ) intact.
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        return request;
    }

    private async Task<HttpResponseMessage> ExecuteAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new GitHubException(GitHubErrorKind.Network, "The request to GitHub timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException(GitHubErrorKind.Network, "Cannot reach GitHub.", ex);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GitHubException(GitHubErrorKind.Unknown, "GitHub returned a response GitAlert could not read.", ex);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => GitHubErrorKind.Unauthorized,
            HttpStatusCode.NotFound => GitHubErrorKind.NotFound,
            HttpStatusCode.TooManyRequests => GitHubErrorKind.RateLimited,
            HttpStatusCode.Forbidden when IsRateLimited(response) => GitHubErrorKind.RateLimited,
            HttpStatusCode.Forbidden => GitHubErrorKind.Forbidden,
            >= HttpStatusCode.InternalServerError => GitHubErrorKind.ServerError,
            _ => GitHubErrorKind.Unknown,
        };

        var message = kind switch
        {
            GitHubErrorKind.NotFound => $"{Describe(what)} was not found, or the token cannot see it.",
            GitHubErrorKind.Unauthorized => "GitHub rejected the access token.",
            GitHubErrorKind.Forbidden => await ReadApiMessageAsync(response, ct).ConfigureAwait(false)
                ?? "GitHub refused the request.",
            GitHubErrorKind.RateLimited => "GitHub rate limit reached.",
            GitHubErrorKind.ServerError => $"GitHub returned {(int)response.StatusCode}.",
            _ => await ReadApiMessageAsync(response, ct).ConfigureAwait(false)
                ?? $"GitHub returned {(int)response.StatusCode}.",
        };

        throw new GitHubException(kind, message) { RetryAt = ResetTime(response) };
    }

    private static string Describe(string what) =>
        what.StartsWith("/repos/", StringComparison.Ordinal)
            ? string.Join('/', what[7..].Split('/').Take(2))
            : what;

    private static async Task<string?> ReadApiMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-remaining", out var values)
        && int.TryParse(values.FirstOrDefault(), out var remaining)
        && remaining == 0;

    private static DateTimeOffset? ResetTime(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-reset", out var values)
        && long.TryParse(values.FirstOrDefault(), out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;

    private void CaptureRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remainingValues)
            && int.TryParse(remainingValues.FirstOrDefault(), out var remaining)
            && response.Headers.TryGetValues("x-ratelimit-limit", out var limitValues)
            && int.TryParse(limitValues.FirstOrDefault(), out var limit))
        {
            RateLimit = new RateLimitStatus(remaining, limit, ResetTime(response) ?? DateTimeOffset.UtcNow);
        }
    }

    private static TimeSpan? ReadPollInterval(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-poll-interval", out var values)
        && int.TryParse(values.FirstOrDefault(), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
