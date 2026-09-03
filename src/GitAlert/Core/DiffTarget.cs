using System.Text.RegularExpressions;

namespace GitAlert.Core;

/// <summary>
/// What an alert points at on GitHub, if anything: one commit, a range of commits, or a pull
/// request.
/// </summary>
public readonly partial record struct DiffTarget(string? Head, string? Base, int? PullRequest)
{
    public static DiffTarget None => default;

    public bool IsKnown => Head is not null || PullRequest is not null;

    /// <summary>
    /// Works out what to fetch for an alert.
    /// </summary>
    /// <remarks>
    /// Alerts recorded before diffs existed have none of the fields set, but they were never
    /// missing the information: a push alert's id ends in the head commit, and its URL is the
    /// commit or compare page. Recovering the target from those means an upgrade does not leave
    /// the whole existing history unreadable.
    /// </remarks>
    public static DiffTarget For(Alert alert)
    {
        if (alert.PullRequestNumber is { } stored)
        {
            return new DiffTarget(null, null, stored);
        }

        if (alert.DiffHead is not null)
        {
            return new DiffTarget(alert.DiffHead, alert.DiffBase, null);
        }

        // The URL is the better of the two fallbacks: a compare link carries both ends of the
        // range, where the id only ever carries the head.
        var fromUrl = FromUrl(alert.Url);

        if (fromUrl.IsKnown)
        {
            return fromUrl;
        }

        return FromId(alert.Id);
    }

    private static DiffTarget FromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return None;
        }

        var compare = CompareUrl().Match(url);

        if (compare.Success)
        {
            return new DiffTarget(compare.Groups[2].Value, compare.Groups[1].Value, null);
        }

        var commit = CommitUrl().Match(url);

        if (commit.Success)
        {
            return new DiffTarget(commit.Groups[1].Value, null, null);
        }

        var pull = PullUrl().Match(url);

        return pull.Success && int.TryParse(pull.Groups[1].Value, out var number)
            ? new DiffTarget(null, null, number)
            : None;
    }

    /// <summary>
    /// Push alert ids are <c>{account}|commit:{sha}</c>, which is enough on its own to fetch the
    /// commit even when nothing else survived.
    /// </summary>
    private static DiffTarget FromId(string id)
    {
        var body = id[(id.LastIndexOf('|') + 1)..];

        return body.StartsWith("commit:", StringComparison.Ordinal) && body.Length > "commit:".Length
            ? new DiffTarget(body["commit:".Length..], null, null)
            : None;
    }

    [GeneratedRegex(@"/compare/([0-9a-fA-F]{7,40})\.\.\.([0-9a-fA-F]{7,40})")]
    private static partial Regex CompareUrl();

    [GeneratedRegex(@"/commit/([0-9a-fA-F]{7,40})")]
    private static partial Regex CommitUrl();

    [GeneratedRegex(@"/pull/(\d+)")]
    private static partial Regex PullUrl();
}
