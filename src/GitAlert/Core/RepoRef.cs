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

        if (!IsValidOwner(owner) || !IsValidName(name))
        {
            return false;
        }

        repo = new RepoRef(owner, name);
        return true;
    }

    /// <summary>Whether a string is shaped like a GitHub login.</summary>
    /// <remarks>
    /// Public because the record's constructor is: a settings file names repositories by owner
    /// and name, and both go straight into request paths. Anything that could not be a login
    /// or a repository name is not a repository GitAlert can ask about.
    /// </remarks>
    public static bool IsValidOwner(string? owner) => owner is not null && OwnerPattern().IsMatch(owner);

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

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

    /// <summary>
    /// GitHub's own rule for a login: alphanumerics and hyphens, up to 39 characters, and no
    /// leading or trailing hyphen.
    /// </summary>
    /// <remarks>
    /// Deliberately strict, because it is also what tells a repository apart from a host. The
    /// scheme and host are stripped before splitting, so <c>https://gitlab.com/acme/thing</c>
    /// used to parse as the repository <c>gitlab.com/acme</c> - accepted, then reported later as
    /// not found. A login cannot contain a dot, so a host cannot pass for one.
    /// </remarks>
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$")]
    private static partial Regex OwnerPattern();

    /// <summary>
    /// Alphanumerics, dots, underscores and hyphens - but not <c>.</c> or <c>..</c>, which git
    /// reserves and which a URI would fold into the segment before them.
    /// </summary>
    [GeneratedRegex(@"^(?!\.{1,2}$)[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NamePattern();
}
