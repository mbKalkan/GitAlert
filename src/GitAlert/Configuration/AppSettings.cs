using System.Text.Json.Serialization;
using GitAlert.Core;

namespace GitAlert.Configuration;

/// <summary>
/// Everything the user can configure. Serialised to <c>%APPDATA%\GitAlert\settings.json</c>.
/// Access tokens are <em>not</em> part of this file - see <see cref="SecureTokenStore"/>.
/// </summary>
public sealed class AppSettings
{
    public const int MinimumPollMinutes = 1;
    public const int MaximumPollMinutes = 180;

    /// <summary>
    /// The GitHub accounts GitAlert signs in as. Each one has its own token, and every watched
    /// repository is polled with the token of the account it was added under - which is what
    /// makes a work account and a personal account work side by side.
    /// </summary>
    public List<GitHubAccount> Accounts { get; set; } = [];

    public List<RepoSubscription> Repositories { get; set; } = [];

    /// <summary>How often GitAlert asks GitHub what changed.</summary>
    public int PollIntervalMinutes { get; set; } = 2;

    /// <summary>Kinds the user has switched off; anything absent is delivered.</summary>
    public HashSet<AlertKind> MutedKinds { get; set; } = [];

    /// <summary>
    /// Pre-multi-account setting, kept only so an existing settings file can be migrated onto
    /// the account it belonged to. <see cref="SettingsMigration"/> clears it.
    /// </summary>
    public bool? IncludeInbox { get; set; }

    /// <summary>Poll GitHub Actions runs for each repository.</summary>
    public bool WatchWorkflowRuns { get; set; } = true;

    /// <summary>When set, only failed / cancelled CI runs raise an alert.</summary>
    public bool OnlyFailedWorkflowRuns { get; set; }

    /// <summary>
    /// Skip activity the signed-in account caused itself. Off by default: the first thing anyone
    /// does with a notification app is push something and wait to see it appear, and silently
    /// filtering exactly that made GitAlert look broken.
    /// </summary>
    public bool IgnoreOwnActivity { get; set; }

    public bool ShowToasts { get; set; } = true;

    public bool PlaySound { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>How many alerts are kept on disk and shown in the flyout.</summary>
    public int MaxHistory { get; set; } = 300;

    /// <summary>
    /// The order the user put the projects in, most important first. Alphabetical is fair but
    /// useless: what matters is which repositories you care about today. Anything absent from
    /// this list follows it alphabetically.
    /// </summary>
    public List<string> ProjectOrder { get; set; } = [];

    /// <summary>Hide alerts that have already been read.</summary>
    public bool UnreadOnly { get; set; }

    /// <summary>
    /// Close the window as soon as focus moves elsewhere. Off by default: the window is a place to
    /// read a diff in, and a panel that vanishes the moment you reach for another window cannot be
    /// read in at all.
    /// </summary>
    public bool AutoHideWindow { get; set; }

    /// <summary>Keep the window above other windows, for watching a build while working.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>
    /// Where the window was last left. Null until it has been moved or resized once, which is the
    /// signal to open it beside the tray icon instead.
    /// </summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public bool IsMuted(AlertKind kind) => MutedKinds.Contains(kind);

    /// <summary>The repositories watched under one account.</summary>
    public IEnumerable<RepoSubscription> RepositoriesFor(string accountId) =>
        Repositories.Where(r => string.Equals(r.AccountId, accountId, StringComparison.Ordinal));

    public GitHubAccount? FindAccount(string? accountId) =>
        accountId is null
            ? null
            : Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));

    public AppSettings Clone() => new()
    {
        Accounts = Accounts.Select(a => a.Clone()).ToList(),
        Repositories = Repositories.Select(r => r.Clone()).ToList(),
        PollIntervalMinutes = PollIntervalMinutes,
        MutedKinds = [.. MutedKinds],
        IncludeInbox = IncludeInbox,
        WatchWorkflowRuns = WatchWorkflowRuns,
        OnlyFailedWorkflowRuns = OnlyFailedWorkflowRuns,
        IgnoreOwnActivity = IgnoreOwnActivity,
        ShowToasts = ShowToasts,
        PlaySound = PlaySound,
        StartWithWindows = StartWithWindows,
        Theme = Theme,
        MaxHistory = MaxHistory,
    };

    /// <summary>Clamps anything a hand-edited settings file may have got wrong.</summary>
    public void Normalise()
    {
        PollIntervalMinutes = Math.Clamp(PollIntervalMinutes, MinimumPollMinutes, MaximumPollMinutes);
        MaxHistory = Math.Clamp(MaxHistory, 20, 2000);

        Accounts = Accounts
            // The id names the account's token file, so an id that cannot be one is not an
            // account GitAlert can authenticate as.
            .Where(a => SecureTokenStore.IsValidAccountId(a.Id))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var known = Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        Repositories = Repositories
            .Where(r => !string.IsNullOrWhiteSpace(r.Owner) && !string.IsNullOrWhiteSpace(r.Name))
            // A repository whose account is gone has nothing to authenticate with. An empty
            // account id is different: that is a repository from a pre-multi-account settings
            // file, waiting for SettingsMigration to adopt it. Normalise runs while loading,
            // before the migration gets a chance, so dropping those would silently delete the
            // user's watch list on upgrade.
            .Where(r => string.IsNullOrEmpty(r.AccountId) || known.Contains(r.AccountId))
            // The same repository may be watched once per account, but not twice under one.
            .GroupBy(r => $"{r.AccountId}|{r.FullName}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}

/// <summary>A GitHub account GitAlert holds a token for.</summary>
public sealed class GitHubAccount
{
    /// <summary>
    /// Stable identity, generated when the account is added. It links repositories to the
    /// account and names the account's token file, so it must never change.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>The GitHub login, resolved from the token. Empty until the first successful call.</summary>
    public string Login { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Watch this account's notification inbox: mentions, review requests, assignments.</summary>
    public bool IncludeInbox { get; set; } = true;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Login) ? "Unverified account" : $"@{Login}";

    public static GitHubAccount Create(string login) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Login = login,
    };

    public GitHubAccount Clone() => new()
    {
        Id = Id,
        Login = Login,
        Enabled = Enabled,
        IncludeInbox = IncludeInbox,
    };
}

/// <summary>A repository the user asked GitAlert to watch, under a particular account.</summary>
public sealed class RepoSubscription
{
    /// <summary>The <see cref="GitHubAccount.Id"/> whose token is used to poll this repository.</summary>
    public string AccountId { get; set; } = string.Empty;

    public required string Owner { get; set; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Set when GitHub reported the repository as private, purely for the settings UI.</summary>
    public bool IsPrivate { get; set; }

    [JsonIgnore]
    public string FullName => $"{Owner}/{Name}";

    [JsonIgnore]
    public RepoRef Ref => new(Owner, Name);

    /// <summary>Key for per-repository sync state: the same repo under two accounts is two subjects.</summary>
    [JsonIgnore]
    public string StateKey => $"{AccountId}|{FullName}";

    public static RepoSubscription From(string accountId, RepoRef repo) =>
        new() { AccountId = accountId, Owner = repo.Owner, Name = repo.Name };

    public RepoSubscription Clone() => new()
    {
        AccountId = AccountId,
        Owner = Owner,
        Name = Name,
        Enabled = Enabled,
        IsPrivate = IsPrivate,
    };
}

public enum AppTheme
{
    System,
    Dark,
    Light,
}
