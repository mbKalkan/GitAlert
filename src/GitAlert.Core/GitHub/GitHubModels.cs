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

    /// <summary>User or Organization, the way GitHub tags an owner.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class GhRepository
{
    [JsonPropertyName("full_name")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public GhUser? Owner { get; set; }

    [JsonPropertyName("private")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>When something was last pushed. Null on a repository that has never had a commit.</summary>
    [JsonPropertyName("pushed_at")]
    public DateTimeOffset? PushedAt { get; set; }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonPropertyName("fork")]
    public bool IsFork { get; set; }
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

    /// <summary>
    /// When the commit landed where it is: the committer date, falling back to the author date.
    /// </summary>
    /// <remarks>
    /// The author date is when the change was written and never moves. A rebase, a squash or a
    /// cherry-pick gives the commit a new committer date and leaves the author date alone, so
    /// stamping alerts with the author date dated a rebased branch by the week it was started:
    /// the push was today and the card said "6d", buried under everything from since.
    /// </remarks>
    [JsonIgnore]
    public DateTimeOffset? Date
    {
        get
        {
            if (Commit?.Committer?.Date is { } committed && committed != default)
            {
                return committed;
            }

            return Commit?.Author?.Date is { } authored && authored != default ? authored : null;
        }
    }
}

public sealed class GhCommitDetail
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("author")]
    public GhCommitAuthor? Author { get; set; }

    [JsonPropertyName("committer")]
    public GhCommitAuthor? Committer { get; set; }
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

/// <summary>A project board, from <c>/orgs/{org}/projectsV2/{n}</c> or <c>/users/{user}/projectsV2/{n}</c>.</summary>
public sealed class GhProject
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("short_description")]
    public string? ShortDescription { get; set; }

    [JsonPropertyName("public")]
    public bool IsPublic { get; set; }

    /// <summary>open | closed</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("owner")]
    public GhUser? Owner { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsClosed => string.Equals(State, "closed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One column of a board: a field, with its options when it is a single select.</summary>
public sealed class GhProjectField
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>title | single_select | text | number | date | iteration | assignees | labels | ...</summary>
    [JsonPropertyName("data_type")]
    public string? DataType { get; set; }

    [JsonPropertyName("options")]
    public List<GhProjectFieldOption>? Options { get; set; }

    [JsonIgnore]
    public bool IsSingleSelect => string.Equals(DataType, "single_select", StringComparison.OrdinalIgnoreCase);
}

public sealed class GhProjectFieldOption
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>GitHub sends the name twice, raw and as HTML; only the raw text is read.</summary>
    [JsonPropertyName("name")]
    public JsonElement Name { get; set; }
}

/// <summary>
/// A card on a board, from the board's <c>/items</c>. The content is the issue or pull request
/// behind it - or a draft - and stays raw because its shape depends on the type; the fields are
/// whichever the request asked for, each with a value shaped by its type.
/// </summary>
public sealed class GhProjectItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Issue | PullRequest | DraftIssue</summary>
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("archived_at")]
    public DateTimeOffset? ArchivedAt { get; set; }

    [JsonPropertyName("fields")]
    public List<GhProjectItemField> Fields { get; set; } = [];
}

public sealed class GhProjectItemField
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("data_type")]
    public string? DataType { get; set; }

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

/// <summary>One page of a board's items, and where the next page starts when there is one.</summary>
public sealed class GhProjectItemsPage
{
    public List<GhProjectItem> Items { get; init; } = [];

    /// <summary>The <c>after</c> cursor of the next page, from the <c>Link</c> header; null on the last.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>An organisation the token's user belongs to, from <c>/user/orgs</c>.</summary>
public sealed class GhOrganization
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;
}

/// <summary>A release, from <c>/repos/{owner}/{repo}/releases/latest</c>: what GitAlert's own newest version is.</summary>
public sealed class GhRelease
{
    /// <summary>The tag the release was cut from, <c>v2.6.0</c> for GitAlert's own.</summary>
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }
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
