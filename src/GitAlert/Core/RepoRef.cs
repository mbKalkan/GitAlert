using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitAlert.Core;

/// <summary>
/// An <c>owner/name</c> pair on GitHub. Users paste whatever they have at hand -
/// a browser URL, a clone URL or the short slug - and <see cref="TryParse"/> normalises it.
/// </summary>
public sealed partial record RepoRef(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public string HtmlUrl => $"https://github.com/{Owner}/{Name}";

    public override string ToString() => FullName;

    /// <summary>
    /// Accepts, case-insensitively:
    /// <list type="bullet">
    /// <item><c>owner/repo</c></item>
    /// <item><c>github.com/owner/repo</c> (with or without scheme, <c>www.</c>, <c>.git</c>, trailing slash)</item>
    /// <item><c>https://github.com/owner/repo/pull/12</c> and other deep links</item>
    /// <item><c>git@github.com:owner/repo.git</c></item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out RepoRef? repo)
    {
        repo = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();

        // git@github.com:owner/repo.git
        var scp = ScpStyleUrl().Match(text);
        if (scp.Success)
        {
            return TryCreate(scp.Groups["owner"].Value, scp.Groups["name"].Value, out repo);
        }

        // Strip scheme and host so the remainder is always "owner/repo[/...]".
        text = SchemeAndHost().Replace(text, string.Empty);

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        return TryCreate(segments[0], segments[1], out repo);
    }

    public static RepoRef Parse(string input) =>
        TryParse(input, out var repo)
            ? repo
            : throw new FormatException($"'{input}' is not a GitHub repository reference.");

    private static bool TryCreate(string owner, string name, [NotNullWhen(true)] out RepoRef? repo)
    {
        repo = null;

        name = name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;

        if (!SegmentPattern().IsMatch(owner) || !SegmentPattern().IsMatch(name))
        {
            return false;
        }

        repo = new RepoRef(owner, name);
        return true;
    }

    // Two refs mean the same repo regardless of how the user capitalised them.
    public bool Equals(RepoRef? other) =>
        other is not null
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(Owner.ToLowerInvariant(), Name.ToLowerInvariant());

    [GeneratedRegex(@"^git@github\.com:(?<owner>[^/]+)/(?<name>[^/]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ScpStyleUrl();

    [GeneratedRegex(@"^(?:[a-z][a-z0-9+.-]*://)?(?:www\.)?(?:github\.com)?/?", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeAndHost();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex SegmentPattern();
}
