using System.Text.Json;
using GitAlert.Core;

namespace GitAlert.GitHub;

/// <summary>
/// Turns raw GitHub payloads into <see cref="Alert"/>s. This is the only place that knows what a
/// <c>PushEvent</c> payload looks like, so adding support for a new event type is a local change.
/// </summary>
public static class EventTranslator
{
    private const int MaxDetailLength = 120;

    /// <summary>
    /// Translates one timeline event. Returns <see langword="null"/> for event types GitAlert
    /// intentionally ignores, which keeps the flyout free of noise.
    /// </summary>
    public static Alert? FromEvent(GhEvent source)
    {
        var repository = source.Repo?.Name ?? string.Empty;
        var actor = source.Actor?.Name;
        var payload = source.Payload;

        return source.Type switch
        {
            "PushEvent" => Push(source, repository, actor, payload),
            "PullRequestEvent" => PullRequest(source, repository, actor, payload),
            "PullRequestReviewEvent" => Review(source, repository, actor, payload),
            "PullRequestReviewCommentEvent" => ReviewComment(source, repository, actor, payload),
            "IssuesEvent" => Issue(source, repository, actor, payload),
            "IssueCommentEvent" => IssueComment(source, repository, actor, payload),
            "CommitCommentEvent" => CommitComment(source, repository, actor, payload),
            "ReleaseEvent" => Release(source, repository, actor, payload),
            "CreateEvent" => Created(source, repository, actor, payload),
            "DeleteEvent" => Deleted(source, repository, actor, payload),
            "ForkEvent" => Fork(source, repository, actor, payload),
            "WatchEvent" => Star(source, repository, actor),
            "PublicEvent" => Build(source, AlertKind.Other, "Repository made public", null, repository, actor, RepoUrl(repository)),
            "MemberEvent" => Member(source, repository, actor, payload),
            "GollumEvent" => Wiki(source, repository, actor, payload),
            _ => null,
        };
    }

    private static Alert Push(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        var branch = ShortRef(payload.GetStringOrNull("ref"));
        var size = payload.GetIntOrDefault("distinct_size", payload.GetIntOrDefault("size", 1));
        var head = payload.GetStringOrNull("head");
        var before = payload.GetStringOrNull("before");

        var commits = payload.TryGetProperty("commits", out var list) && list.ValueKind == JsonValueKind.Array
            ? list
            : default;

        var firstMessage = commits.ValueKind == JsonValueKind.Array && commits.GetArrayLength() > 0
            ? commits[commits.GetArrayLength() - 1].GetStringOrNull("message")
            : null;

        var title = size == 1
            ? $"New commit on {branch}"
            : $"{size} new commits on {branch}";

        // A single commit links straight to the diff; a batch links to the compare view.
        var url = size == 1 && head is not null
            ? $"{RepoUrl(repository)}/commit/{head}"
            : before is not null && head is not null
                ? $"{RepoUrl(repository)}/compare/{before}...{head}"
                : $"{RepoUrl(repository)}/commits/{branch}";

        return Build(
            source,
            AlertKind.Push,
            title,
            FirstLine(firstMessage),
            repository,
            actor,
            url,
            // Identified by the head commit, not the event: the same push may reach us first
            // through the commits endpoint and only hours later through the events timeline.
            idOverride: head is null ? null : $"commit:{head}",
            diffHead: head,
            diffBase: size == 1 ? null : before);
    }

    private static Alert? PullRequest(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (!payload.TryGetProperty("pull_request", out var pr))
        {
            return null;
        }

        var action = payload.GetStringOrNull("action");
        var number = pr.GetIntOrDefault("number", payload.GetIntOrDefault("number", 0));
        var merged = pr.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True;

        var verb = action switch
        {
            "opened" => "opened",
            "reopened" => "reopened",
            "closed" when merged => "merged",
            "closed" => "closed",
            "ready_for_review" => "marked ready for review",
            "converted_to_draft" => "converted to draft",
            "synchronize" => null, // A push to the PR branch; the PushEvent already covers it.
            _ => null,
        };

        if (verb is null)
        {
            return null;
        }

        var severity = merged ? AlertSeverity.Success : AlertSeverity.Normal;

        return Build(
            source,
            AlertKind.PullRequest,
            $"PR #{number} {verb}",
            FirstLine(pr.GetStringOrNull("title")),
            repository,
            actor,
            pr.GetStringOrNull("html_url") ?? $"{RepoUrl(repository)}/pull/{number}",
            severity,
            pullRequestNumber: number > 0 ? number : null);
    }

    private static Alert? Review(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (!payload.TryGetProperty("review", out var review) || !payload.TryGetProperty("pull_request", out var pr))
        {
            return null;
        }

        var state = review.GetStringOrNull("state")?.ToLowerInvariant();
        var (label, severity) = state switch
        {
            "approved" => ("approved", AlertSeverity.Success),
            "changes_requested" => ("requested changes on", AlertSeverity.Warning),
            _ => ("reviewed", AlertSeverity.Normal),
        };

        var number = pr.GetIntOrDefault("number", 0);

        return Build(
            source,
            AlertKind.Review,
            $"{actor ?? "Someone"} {label} PR #{number}",
            FirstLine(pr.GetStringOrNull("title")),
            repository,
            actor,
            review.GetStringOrNull("html_url") ?? pr.GetStringOrNull("html_url"),
            severity);
    }

    private static Alert? ReviewComment(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (!payload.TryGetProperty("pull_request", out var pr))
        {
            return null;
        }

        var comment = payload.TryGetProperty("comment", out var c) ? c : default;
        var number = pr.GetIntOrDefault("number", 0);

        return Build(
            source,
            AlertKind.Comment,
            $"Review comment on PR #{number}",
            FirstLine(comment.GetStringOrNull("body")),
            repository,
            actor,
            comment.GetStringOrNull("html_url") ?? pr.GetStringOrNull("html_url"));
    }

    private static Alert? Issue(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (!payload.TryGetProperty("issue", out var issue))
        {
            return null;
        }

        var action = payload.GetStringOrNull("action");
        var verb = action switch
        {
            "opened" => "opened",
            "closed" => "closed",
            "reopened" => "reopened",
            _ => null,
        };

        if (verb is null)
        {
            return null;
        }

        var number = issue.GetIntOrDefault("number", 0);

        return Build(
            source,
            AlertKind.Issue,
            $"Issue #{number} {verb}",
            FirstLine(issue.GetStringOrNull("title")),
            repository,
            actor,
            issue.GetStringOrNull("html_url") ?? $"{RepoUrl(repository)}/issues/{number}");
    }

    private static Alert? IssueComment(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (payload.GetStringOrNull("action") != "created" || !payload.TryGetProperty("issue", out var issue))
        {
            return null;
        }

        var comment = payload.TryGetProperty("comment", out var c) ? c : default;
        var number = issue.GetIntOrDefault("number", 0);

        // GitHub models pull requests as issues, so the payload tells us which one this is.
        var isPullRequest = issue.TryGetProperty("pull_request", out _);
        var noun = isPullRequest ? "PR" : "issue";

        return Build(
            source,
            AlertKind.Comment,
            $"New comment on {noun} #{number}",
            FirstLine(comment.GetStringOrNull("body")) ?? FirstLine(issue.GetStringOrNull("title")),
            repository,
            actor,
            comment.GetStringOrNull("html_url") ?? issue.GetStringOrNull("html_url"));
    }

    private static Alert? CommitComment(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (!payload.TryGetProperty("comment", out var comment))
        {
            return null;
        }

        return Build(
            source,
            AlertKind.Comment,
            "New commit comment",
            FirstLine(comment.GetStringOrNull("body")),
            repository,
            actor,
            comment.GetStringOrNull("html_url"));
    }

    private static Alert? Release(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (payload.GetStringOrNull("action") != "published" || !payload.TryGetProperty("release", out var release))
        {
            return null;
        }

        var tag = release.GetStringOrNull("tag_name");
        var name = release.GetStringOrNull("name");
        var prerelease = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;

        return Build(
            source,
            AlertKind.Release,
            prerelease ? $"Pre-release {tag} published" : $"Release {tag} published",
            FirstLine(string.IsNullOrWhiteSpace(name) ? tag : name),
            repository,
            actor,
            release.GetStringOrNull("html_url") ?? $"{RepoUrl(repository)}/releases",
            AlertSeverity.Success);
    }

    private static Alert? Created(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        var refType = payload.GetStringOrNull("ref_type");
        var name = payload.GetStringOrNull("ref");

        // "repository" creations carry no ref and are not interesting for a watcher.
        if (refType is not ("branch" or "tag") || name is null)
        {
            return null;
        }

        var url = refType == "tag"
            ? $"{RepoUrl(repository)}/releases/tag/{Uri.EscapeDataString(name)}"
            : $"{RepoUrl(repository)}/tree/{Uri.EscapeDataString(name)}";

        return Build(source, AlertKind.Branch, $"{Capitalise(refType)} {name} created", null, repository, actor, url);
    }

    private static Alert? Deleted(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        var refType = payload.GetStringOrNull("ref_type");
        var name = payload.GetStringOrNull("ref");

        if (refType is not ("branch" or "tag") || name is null)
        {
            return null;
        }

        return Build(source, AlertKind.Branch, $"{Capitalise(refType)} {name} deleted", null, repository, actor, RepoUrl(repository));
    }

    private static Alert Fork(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        var forkee = payload.TryGetProperty("forkee", out var f) ? f : default;

        return Build(
            source,
            AlertKind.Fork,
            "Repository forked",
            forkee.GetStringOrNull("full_name"),
            repository,
            actor,
            forkee.GetStringOrNull("html_url") ?? RepoUrl(repository));
    }

    private static Alert Star(GhEvent source, string repository, string? actor) =>
        Build(
            source,
            AlertKind.Star,
            $"{actor ?? "Someone"} starred the repository",
            null,
            repository,
            actor,
            $"{RepoUrl(repository)}/stargazers");

    private static Alert? Member(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (payload.GetStringOrNull("action") != "added" || !payload.TryGetProperty("member", out var member))
        {
            return null;
        }

        return Build(
            source,
            AlertKind.Other,
            "Collaborator added",
            member.GetStringOrNull("login"),
            repository,
            actor,
            $"{RepoUrl(repository)}/settings/access");
    }

    private static Alert? Wiki(GhEvent source, string repository, string? actor, JsonElement payload)
    {
        if (!payload.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array
            || pages.GetArrayLength() == 0)
        {
            return null;
        }

        var page = pages[0];

        return Build(
            source,
            AlertKind.Other,
            $"Wiki page {page.GetStringOrNull("action") ?? "updated"}",
            page.GetStringOrNull("title"),
            repository,
            actor,
            page.GetStringOrNull("html_url") ?? $"{RepoUrl(repository)}/wiki");
    }

    /// <summary>
    /// Turns freshly seen commits into a single push alert, newest first.
    /// </summary>
    /// <remarks>
    /// Shares its identity with the equivalent <c>PushEvent</c> so that whichever source reports a
    /// push first wins and the other is de-duplicated away.
    /// </remarks>
    public static Alert FromCommits(
        IReadOnlyList<GhCommit> commits,
        string repository,
        string? branch,
        string? previousSha)
    {
        var newest = commits[0];
        var on = string.IsNullOrWhiteSpace(branch) ? string.Empty : $" on {branch}";

        var title = commits.Count == 1
            ? $"New commit{on}"
            : $"{commits.Count} new commits{on}";

        var url = commits.Count == 1 || string.IsNullOrEmpty(previousSha)
            ? newest.HtmlUrl ?? $"{RepoUrl(repository)}/commit/{newest.Sha}"
            : $"{RepoUrl(repository)}/compare/{previousSha}...{newest.Sha}";

        return new Alert
        {
            Id = $"commit:{newest.Sha}",
            Kind = AlertKind.Push,
            Title = title,
            Detail = FirstLine(newest.Commit?.Message),
            Repository = repository,
            Actor = newest.Author?.Login ?? newest.Commit?.Author?.Name,
            Url = url,
            Timestamp = newest.Commit?.Author?.Date ?? DateTimeOffset.Now,
            DiffHead = newest.Sha,

            // One commit is its own diff; several are shown as the net change across the range.
            DiffBase = commits.Count == 1 ? null : previousSha,
        };
    }

    /// <summary>Translates a completed GitHub Actions run.</summary>
    public static Alert FromWorkflowRun(GhWorkflowRun run, string repository)
    {
        var (label, severity) = run.Conclusion?.ToLowerInvariant() switch
        {
            "success" => ("passed", AlertSeverity.Success),
            "failure" => ("failed", AlertSeverity.Error),
            "timed_out" => ("timed out", AlertSeverity.Error),
            "cancelled" => ("cancelled", AlertSeverity.Warning),
            "action_required" => ("needs action", AlertSeverity.Warning),
            "neutral" => ("finished", AlertSeverity.Normal),
            _ => ("finished", AlertSeverity.Normal),
        };

        var workflow = string.IsNullOrWhiteSpace(run.Name) ? "Workflow" : run.Name!;
        var detail = string.IsNullOrWhiteSpace(run.DisplayTitle) ? run.HeadBranch : run.DisplayTitle;

        return new Alert
        {
            Id = $"run:{run.Id}",
            Kind = AlertKind.Workflow,
            Title = $"{workflow} {label} (#{run.RunNumber})",
            Detail = FirstLine(detail),
            Repository = repository,
            Actor = run.Actor?.Login,
            Url = run.HtmlUrl,
            Timestamp = run.UpdatedAt,
            Severity = severity,
        };
    }

    /// <summary>Translates an inbox notification (mention, review request, assignment, ...).</summary>
    public static Alert FromNotification(GhNotification notification)
    {
        var repository = notification.Repository?.FullName ?? string.Empty;
        var subject = notification.Subject;

        var kind = notification.Reason?.ToLowerInvariant() switch
        {
            "mention" or "team_mention" => AlertKind.Mention,
            "review_requested" => AlertKind.Review,
            "ci_activity" => AlertKind.Workflow,
            _ => subject?.Type switch
            {
                "PullRequest" => AlertKind.PullRequest,
                "Issue" => AlertKind.Issue,
                "Release" => AlertKind.Release,
                "Commit" => AlertKind.Push,
                "CheckSuite" => AlertKind.Workflow,
                "Discussion" => AlertKind.Comment,
                _ => AlertKind.Other,
            },
        };

        return new Alert
        {
            Id = $"inbox:{notification.Id}:{notification.UpdatedAt.ToUnixTimeSeconds()}",
            Kind = kind,
            Title = DescribeReason(notification.Reason),
            Detail = FirstLine(subject?.Title),
            Repository = repository,
            Actor = null,
            Url = ToBrowserUrl(subject?.Url, notification.Repository?.HtmlUrl ?? RepoUrl(repository)),
            Timestamp = notification.UpdatedAt,
        };
    }

    private static string DescribeReason(string? reason) => reason?.ToLowerInvariant() switch
    {
        "assign" => "You were assigned",
        "author" => "Activity on your thread",
        "comment" => "New comment on a thread you follow",
        "ci_activity" => "A workflow run finished",
        "invitation" => "You were invited to collaborate",
        "manual" => "Activity on a thread you subscribed to",
        "mention" => "You were mentioned",
        "team_mention" => "Your team was mentioned",
        "review_requested" => "Your review was requested",
        "security_alert" => "Security alert",
        "state_change" => "A thread you follow changed state",
        "subscribed" => "Activity in a repository you watch",
        _ => "New notification",
    };

    /// <summary>
    /// Notification subjects carry API URLs. Rewrite the handful of shapes that map cleanly to a
    /// browser URL and fall back to the repository page for the rest.
    /// </summary>
    public static string ToBrowserUrl(string? apiUrl, string fallback)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)
            || !Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');

        // repos/{owner}/{repo}/{resource}/{id}
        if (parts.Length < 5 || parts[0] != "repos")
        {
            return fallback;
        }

        var slug = $"{parts[1]}/{parts[2]}";
        var resource = parts[3];
        var id = parts[4];

        return resource switch
        {
            "pulls" => $"https://github.com/{slug}/pull/{id}",
            "issues" => $"https://github.com/{slug}/issues/{id}",
            "commits" => $"https://github.com/{slug}/commit/{id}",
            "discussions" => $"https://github.com/{slug}/discussions/{id}",
            "releases" => $"https://github.com/{slug}/releases",
            "check-suites" or "actions" => $"https://github.com/{slug}/actions",
            _ => fallback,
        };
    }

    private static Alert Build(
        GhEvent source,
        AlertKind kind,
        string title,
        string? detail,
        string repository,
        string? actor,
        string? url,
        AlertSeverity severity = AlertSeverity.Normal,
        string? idOverride = null,
        string? diffHead = null,
        string? diffBase = null,
        int? pullRequestNumber = null) =>
        new()
        {
            Id = idOverride ?? $"event:{source.Id}",
            Kind = kind,
            Title = title,
            Detail = detail,
            Repository = repository,
            Actor = actor,
            Url = url,
            Timestamp = source.CreatedAt,
            Severity = severity,
            DiffHead = diffHead,
            DiffBase = diffBase,
            PullRequestNumber = pullRequestNumber,
        };

    private static string RepoUrl(string repository) => $"https://github.com/{repository}";

    private static string ShortRef(string? gitRef) =>
        gitRef is null ? "(unknown)" :
        gitRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? gitRef["refs/heads/".Length..] :
        gitRef.StartsWith("refs/tags/", StringComparison.Ordinal) ? gitRef["refs/tags/".Length..] :
        gitRef;

    private static string Capitalise(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>Commit messages and comment bodies are multi-line; cards show one tidy line.</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();

        return line.Length <= MaxDetailLength ? line : line[..(MaxDetailLength - 1)].TrimEnd() + "…";
    }
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int GetIntOrDefault(this JsonElement element, string property, int fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;
}
