using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace GitAlert.Core;

/// <summary>Who a GitHub project board belongs to: an organisation or a user.</summary>
public enum BoardOwnerKind
{
    /// <summary>Not said yet: a link like <c>acme/projects/12</c> could be either.</summary>
    Unknown,

    User,

    Organization,
}

/// <summary>
/// A GitHub project board - the new Projects, a table of issues and pull requests with a Status
/// column - named the way GitHub does: an owner and a number. Users paste the board's link;
/// <see cref="TryParse"/> takes it apart.
/// </summary>
/// <remarks>
/// A board is not a repository, but the flyout groups alerts by a <c>owner/name</c> string and
/// sections, ordering and read state all hang off that string. A board's <see cref="Key"/> is
/// shaped to fit the same slot without ever colliding with a repository: <c>acme/#12</c> cannot
/// be a repository name, because <c>#</c> is not a character git allows.
/// </remarks>
public sealed partial record BoardRef(string Owner, BoardOwnerKind OwnerKind, int Number)
{
    /// <summary>The string the flyout groups this board's alerts under: <c>owner/#number</c>.</summary>
    public string Key => $"{Owner}/#{Number}";

    public string HtmlUrl => OwnerKind == BoardOwnerKind.Organization
        ? $"https://github.com/orgs/{Owner}/projects/{Number}"
        : $"https://github.com/users/{Owner}/projects/{Number}";

    /// <summary>The REST path of the board itself; the fields and the items hang off it.</summary>
    public string ApiPath => OwnerKind == BoardOwnerKind.Organization
        ? $"/orgs/{Uri.EscapeDataString(Owner)}/projectsV2/{Number}"
        : $"/users/{Uri.EscapeDataString(Owner)}/projectsV2/{Number}";

    public override string ToString() => Key;

    public BoardRef AsOrganization() => this with { OwnerKind = BoardOwnerKind.Organization };

    public BoardRef AsUser() => this with { OwnerKind = BoardOwnerKind.User };

    /// <summary>
    /// Accepts, case-insensitively:
    /// <list type="bullet">
    /// <item><c>https://github.com/orgs/acme/projects/12</c>, with or without a view behind it</item>
    /// <item><c>https://github.com/users/deniz/projects/3</c></item>
    /// <item><c>acme/projects/12</c>, whose owner kind is then unknown</item>
    /// <item><c>acme/#12</c>, the key GitAlert itself writes</item>
    /// </list>
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out BoardRef? board)
    {
        board = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = SchemeAndHost().Replace(input.Trim(), string.Empty);
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 2 && segments[1].StartsWith('#'))
        {
            return TryCreate(segments[0], BoardOwnerKind.Unknown, segments[1][1..], out board);
        }

        if (segments.Length >= 4 && Is(segments[2], "projects"))
        {
            var kind = Is(segments[0], "orgs") ? BoardOwnerKind.Organization
                : Is(segments[0], "users") ? BoardOwnerKind.User
                : (BoardOwnerKind?)null;

            if (kind is { } known)
            {
                return TryCreate(segments[1], known, segments[3], out board);
            }
        }

        if (segments.Length >= 3 && Is(segments[1], "projects"))
        {
            return TryCreate(segments[0], BoardOwnerKind.Unknown, segments[2], out board);
        }

        return false;
    }

    /// <summary>Whether a group name is a board's key rather than a repository's full name.</summary>
    public static bool IsKey(string? value) => value is not null && KeyPattern().IsMatch(value);

    private static bool TryCreate(string owner, BoardOwnerKind kind, string number, [NotNullWhen(true)] out BoardRef? board)
    {
        board = null;

        if (!RepoRef.IsValidOwner(owner) || !int.TryParse(number, out var parsed) || parsed <= 0)
        {
            return false;
        }

        board = new BoardRef(owner, kind, parsed);
        return true;
    }

    private static bool Is(string segment, string expected) =>
        string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);

    // An organisation and a user cannot share a login, so the owner and the number name the board
    // whichever kind the link said - or did not say.
    public bool Equals(BoardRef? other) =>
        other is not null
        && Number == other.Number
        && string.Equals(Owner, other.Owner, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(Owner.ToLowerInvariant(), Number);

    [GeneratedRegex(@"^(?:[a-z][a-z0-9+.-]*://)?(?:www\.)?(?:github\.com)?/?", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeAndHost();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/#[1-9][0-9]*$")]
    private static partial Regex KeyPattern();
}
