using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitAlert.GitHub;

/// <summary>
/// Only the slices of GitHub's payloads GitAlert actually reads. Event payloads stay as raw
/// <see cref="JsonElement"/> because their shape depends on <see cref="GhEvent.Type"/>;
/// <see cref="EventTranslator"/> is the single place that interprets them.
/// </summary>
public sealed class GhUser
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

public sealed class GhRepository
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("private")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }
}

public sealed class GhActor
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("display_login")]
    public string? DisplayLogin { get; set; }

    public string Name => string.IsNullOrWhiteSpace(DisplayLogin) ? Login : DisplayLogin!;
}

public sealed class GhEventRepo
{
    /// <summary>Always the <c>owner/name</c> slug on the events endpoints.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class GhEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("actor")]
    public GhActor? Actor { get; set; }

    [JsonPropertyName("repo")]
    public GhEventRepo? Repo { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A commit from <c>/repos/{owner}/{repo}/commits</c>. Unlike the events timeline this is
/// authoritative and immediate, which is what makes it a usable source for push alerts.
/// </summary>
public sealed class GhCommit
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("commit")]
    public GhCommitDetail? Commit { get; set; }

    /// <summary>The GitHub account behind the commit; null when the email matches no account.</summary>
    [JsonPropertyName("author")]
    public GhUser? Author { get; set; }
}

public sealed class GhCommitDetail
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("author")]
    public GhCommitAuthor? Author { get; set; }
}

public sealed class GhCommitAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }
}

/// <summary>One file touched by a commit, comparison or pull request.</summary>
public sealed class GhFileChange
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>added | removed | modified | renamed | copied | changed | unchanged</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonPropertyName("changes")]
    public int Changes { get; set; }

    /// <summary>
    /// The unified diff for this file. Absent for binary files and for files whose diff GitHub
    /// considers too large to inline, which the detail pane has to say out loud rather than
    /// rendering as an empty file.
    /// </summary>
    [JsonPropertyName("patch")]
    public string? Patch { get; set; }

    [JsonPropertyName("previous_filename")]
    public string? PreviousFilename { get; set; }

    [JsonPropertyName("blob_url")]
    public string? BlobUrl { get; set; }
}

public sealed class GhChangeStats
{
    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

/// <summary>A single commit with its file list, from <c>/repos/{o}/{r}/commits/{sha}</c>.</summary>
public sealed class GhCommitWithFiles
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("commit")]
    public GhCommitDetail? Commit { get; set; }

    [JsonPropertyName("author")]
    public GhUser? Author { get; set; }

    [JsonPropertyName("stats")]
    public GhChangeStats? Stats { get; set; }

    [JsonPropertyName("files")]
    public List<GhFileChange> Files { get; set; } = [];
}

/// <summary>A range of commits, from <c>/repos/{o}/{r}/compare/{base}...{head}</c>.</summary>
public sealed class GhComparison
{
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("total_commits")]
    public int TotalCommits { get; set; }

    [JsonPropertyName("commits")]
    public List<GhCommit> Commits { get; set; } = [];

    [JsonPropertyName("files")]
    public List<GhFileChange> Files { get; set; } = [];
}

public sealed class GhWorkflowRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("display_title")]
    public string? DisplayTitle { get; set; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; set; }

    [JsonPropertyName("run_number")]
    public int RunNumber { get; set; }

    /// <summary>queued | in_progress | completed</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>success | failure | cancelled | timed_out | action_required | neutral | skipped</summary>
    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("actor")]
    public GhUser? Actor { get; set; }
}

public sealed class GhWorkflowRunsPage
{
    [JsonPropertyName("workflow_runs")]
    public List<GhWorkflowRun> WorkflowRuns { get; set; } = [];
}

public sealed class GhNotificationSubject
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>An API URL; <see cref="EventTranslator"/> converts it to a browser URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>PullRequest | Issue | Commit | Release | CheckSuite | Discussion | RepositoryVulnerabilityAlert</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class GhNotification
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("unread")]
    public bool Unread { get; set; }

    /// <summary>assign | mention | review_requested | subscribed | comment | ci_activity | ...</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("subject")]
    public GhNotificationSubject? Subject { get; set; }

    [JsonPropertyName("repository")]
    public GhRepository? Repository { get; set; }
}

/// <summary>
/// The result of a conditional GET. A <see cref="NotModified"/> response costs no rate-limit
/// budget on GitHub, which is why every poll sends the previous ETag.
/// </summary>
public sealed class ConditionalResponse<T>
{
    public bool NotModified { get; init; }

    public T? Value { get; init; }

    public string? ETag { get; init; }

    /// <summary>The <c>x-poll-interval</c> GitHub asks clients to honour, when present.</summary>
    public TimeSpan? PollInterval { get; init; }

    public static ConditionalResponse<T> Unchanged(string? etag, TimeSpan? pollInterval = null) =>
        new() { NotModified = true, ETag = etag, PollInterval = pollInterval };
}

/// <summary>Snapshot of the <c>x-ratelimit-*</c> headers from the most recent response.</summary>
public readonly record struct RateLimitStatus(int Remaining, int Limit, DateTimeOffset ResetsAt)
{
    public static RateLimitStatus Unknown => new(-1, -1, DateTimeOffset.MinValue);

    public bool IsKnown => Limit > 0;
}
