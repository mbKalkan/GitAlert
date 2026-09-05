namespace GitAlert.GitHub;

public enum GitHubErrorKind
{
    /// <summary>No token, an expired token, or a revoked one.</summary>
    Unauthorized,

    /// <summary>Authenticated, but the token lacks the scope for this resource.</summary>
    Forbidden,

    /// <summary>The repository does not exist, or the token cannot see it.</summary>
    NotFound,

    /// <summary>
    /// The repository exists but has no commits: just created, or a wiki with no code behind it.
    /// GitHub answers 409 to anything that reads its history.
    /// </summary>
    EmptyRepository,

    RateLimited,

    /// <summary>DNS, TLS, proxy or plain "the laptop is offline".</summary>
    Network,

    ServerError,

    Unknown,
}

public sealed class GitHubException : Exception
{
    public GitHubException(GitHubErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public GitHubErrorKind Kind { get; }

    /// <summary>When rate limited, the moment the budget is restored.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>A short line suitable for the flyout status bar.</summary>
    public string UserMessage => Kind switch
    {
        GitHubErrorKind.Unauthorized => "Access token is invalid or expired.",
        GitHubErrorKind.Forbidden => "Token lacks the required scope.",
        GitHubErrorKind.NotFound => Message,
        GitHubErrorKind.EmptyRepository => Message,
        GitHubErrorKind.RateLimited => RetryAt is { } at
            ? $"Rate limited until {at.ToLocalTime():HH:mm}."
            : "GitHub rate limit reached.",
        GitHubErrorKind.Network => "Cannot reach GitHub.",
        GitHubErrorKind.ServerError => "GitHub is having trouble.",
        _ => Message,
    };
}
