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

    /// <summary>
    /// The project boards watched, each under the account whose token can read it. A board is
    /// read the way a repository is: once per check, and only when something on it moved.
    /// </summary>
    public List<BoardSubscription> Boards { get; set; } = [];

    /// <summary>How often GitAlert asks GitHub what changed.</summary>
    public int PollIntervalMinutes { get; set; } = 2;

    /// <summary>Kinds the user has switched off; anything absent is delivered.</summary>
    [JsonConverter(typeof(AlertKindSetConverter))]
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
    /// On a board, only a card arriving, changing Status, being archived or going away is news.
    /// Off, and any field on a card changing is news too: a priority, an iteration, a date.
    /// </summary>
    public bool OnlyStatusChangesOnBoards { get; set; } = true;

    /// <summary>
    /// Skip activity the signed-in account caused itself. Off by default: the first thing anyone
    /// does with a notification app is push something and wait to see it appear, and silently
    /// filtering exactly that made GitAlert look broken.
    /// </summary>
    public bool IgnoreOwnActivity { get; set; }

    public bool ShowToasts { get; set; } = true;

    public bool PlaySound { get; set; } = true;

    public bool StartWithWindows { get; set; }

    [JsonConverter(typeof(AppThemeConverter))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Which dark palette to paint with, whenever the theme comes out dark.</summary>
    [JsonConverter(typeof(DarkPaletteConverter))]
    public DarkPalette DarkPalette { get; set; } = DarkPalette.VsCode;

    /// <summary>How many alerts are kept on disk and shown in the flyout.</summary>
    public int MaxHistory { get; set; } = 300;

    /// <summary>
    /// The order the user put the projects in, most important first. Alphabetical is fair but
    /// useless: what matters is which repositories you care about today. Anything absent from
    /// this list follows it alphabetically.
    /// </summary>
    public List<string> ProjectOrder { get; set; } = [];

    /// <summary>
    /// The sections the user grouped projects under, in the order they are shown. A project in
    /// none of them sits above the sections, loose. Sections hold membership and their fold; the
    /// order of the projects inside one is still <see cref="ProjectOrder"/>.
    /// </summary>
    public List<ProjectSection> Sections { get; set; } = [];

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

    /// <summary>
    /// The alert list's share of the window's width, between 0 and 1, as the splitter beside it
    /// was left. Null until it has been dragged once.
    /// </summary>
    public double? ListPaneShare { get; set; }

    public bool IsMuted(AlertKind kind) => MutedKinds.Contains(kind);

    /// <summary>
    /// The repositories and boards on the watch list with their tick off, by the name the flyout
    /// groups them under. They are neither polled nor shown, and their history waits for the tick
    /// to come back. A name watched under two accounts is off only when it is off under both.
    /// </summary>
    public IEnumerable<string> SwitchedOffRepositories =>
        Repositories
            .Select(r => (Key: r.FullName, r.Enabled))
            .Concat(Boards.Select(b => (b.Key, b.Enabled)))
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.All(r => !r.Enabled))
            .Select(g => g.Key);

    public bool IsSwitchedOff(string repository) =>
        SwitchedOffRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase);

    /// <summary>The repositories watched under one account.</summary>
    public IEnumerable<RepoSubscription> RepositoriesFor(string accountId) =>
        Repositories.Where(r => string.Equals(r.AccountId, accountId, StringComparison.Ordinal));

    /// <summary>The boards watched under one account.</summary>
    public IEnumerable<BoardSubscription> BoardsFor(string accountId) =>
        Boards.Where(b => string.Equals(b.AccountId, accountId, StringComparison.Ordinal));

    /// <summary>Every name the flyout may group under: the repositories and the boards, as watched.</summary>
    public IEnumerable<string> WatchedNames =>
        Repositories.Select(r => r.FullName).Concat(Boards.Select(b => b.Key));

    public GitHubAccount? FindAccount(string? accountId) =>
        accountId is null
            ? null
            : Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));

    public AppSettings Clone() => new()
    {
        Accounts = Accounts.Select(a => a.Clone()).ToList(),
        Repositories = Repositories.Select(r => r.Clone()).ToList(),
        Boards = Boards.Select(b => b.Clone()).ToList(),
        PollIntervalMinutes = PollIntervalMinutes,
        MutedKinds = [.. MutedKinds],
        IncludeInbox = IncludeInbox,
        WatchWorkflowRuns = WatchWorkflowRuns,
        OnlyFailedWorkflowRuns = OnlyFailedWorkflowRuns,
        OnlyStatusChangesOnBoards = OnlyStatusChangesOnBoards,
        IgnoreOwnActivity = IgnoreOwnActivity,
        ShowToasts = ShowToasts,
        PlaySound = PlaySound,
        StartWithWindows = StartWithWindows,
        Theme = Theme,
        DarkPalette = DarkPalette,
        MaxHistory = MaxHistory,
        ProjectOrder = [.. ProjectOrder],
        Sections = Sections.Select(s => s.Clone()).ToList(),
        UnreadOnly = UnreadOnly,
        AutoHideWindow = AutoHideWindow,
        AlwaysOnTop = AlwaysOnTop,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        ListPaneShare = ListPaneShare,
    };

    /// <summary>Clamps anything a hand-edited settings file may have got wrong.</summary>
    public void Normalise()
    {
        // JSON null is a valid value for every one of these, and every reader below walks them.
        Accounts ??= [];
        Repositories ??= [];
        Boards ??= [];
        MutedKinds ??= [];
        ProjectOrder ??= [];
        ProjectOrder.RemoveAll(string.IsNullOrWhiteSpace);
        NormaliseSections();

        PollIntervalMinutes = Math.Clamp(PollIntervalMinutes, MinimumPollMinutes, MaximumPollMinutes);
        MaxHistory = Math.Clamp(MaxHistory, 20, 2000);

        // The splitter is stored as a number a hand-edited file can get wrong; a bad one is
        // simply forgotten rather than applied.
        if (ListPaneShare is { } share && (double.IsNaN(share) || share <= 0 || share >= 1))
        {
            ListPaneShare = null;
        }

        Accounts = Accounts
            .Where(a => a is not null)
            // The id names the account's token file, so an id that cannot be one is not an
            // account GitAlert can authenticate as.
            .Where(a => SecureTokenStore.IsValidAccountId(a.Id))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        foreach (var account in Accounts)
        {
            account.Login ??= string.Empty;
        }

        var known = Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        Repositories = Repositories
            .Where(r => r is not null)
            // The owner and the name are spliced into request paths and sent with the account's
            // token. Only what could be a login and a repository name gets that far; a segment
            // like ".." would otherwise be folded into a different endpoint by the URI itself.
            .Where(r => RepoRef.IsValidOwner(r.Owner) && RepoRef.IsValidName(r.Name))
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

        foreach (var repository in Repositories)
        {
            repository.AccountId ??= string.Empty;
        }

        Boards = Boards
            .Where(b => b is not null)
            // The owner goes into a request path and the number is what GitHub numbers boards
            // by, so anything that could not be either is not a board GitAlert can ask about.
            .Where(b => RepoRef.IsValidOwner(b.Owner) && b.Number > 0)
            // Unlike a repository, a board never came from a pre-account settings file: one
            // without an account has nothing to read it with.
            .Where(b => !string.IsNullOrEmpty(b.AccountId) && known.Contains(b.AccountId))
            .GroupBy(b => $"{b.AccountId}|{b.Key}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var board in Boards)
        {
            board.Title = string.IsNullOrWhiteSpace(board.Title) ? $"#{board.Number}" : board.Title.Trim();
        }
    }

    /// <summary>
    /// A section is a name, a membership list and the sections inside it, all of which a
    /// hand-edited file can break: a blank name gets a placeholder, a null entry goes, and a
    /// project listed under two sections - at any depth - stays in the first one read.
    /// </summary>
    private void NormaliseSections() =>
        Sections = Tidy(Sections, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static List<ProjectSection> Tidy(List<ProjectSection>? sections, HashSet<string> claimed)
    {
        var kept = new List<ProjectSection>();

        foreach (var section in sections ?? [])
        {
            if (section is null)
            {
                continue;
            }

            section.Name = string.IsNullOrWhiteSpace(section.Name) ? ProjectSection.DefaultName : section.Name.Trim();
            section.Repositories ??= [];
            section.Repositories = section.Repositories
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Where(claimed.Add)
                .ToList();
            section.Sections = Tidy(section.Sections, claimed);

            kept.Add(section);
        }

        return kept;
    }
}

/// <summary>
/// A named group in the flyout list, folded and unfolded as one: its own projects, then the
/// sections inside it, each with theirs, as deep as the user cares to go.
/// </summary>
public sealed class ProjectSection
{
    /// <summary>What a section is called until the user types a name.</summary>
    public const string DefaultName = "New section";

    public string Name { get; set; } = DefaultName;

    /// <summary>Folded: the header shows, nothing under it does.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>The projects grouped directly here, by full name. Their order comes from the project order.</summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>The sections inside this one, in the order they are shown, after its projects.</summary>
    public List<ProjectSection> Sections { get; set; } = [];

    /// <summary>Whether the project is directly in this section, not in one inside it.</summary>
    public bool Contains(string repository) =>
        Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase);

    /// <summary>This section and every one inside it, top to bottom, the way the list shows them.</summary>
    public IEnumerable<ProjectSection> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in Sections)
        {
            foreach (var section in child.SelfAndDescendants())
            {
                yield return section;
            }
        }
    }

    public ProjectSection Clone() => new()
    {
        Name = Name,
        IsCollapsed = IsCollapsed,
        Repositories = [.. Repositories],
        Sections = Sections.Select(s => s.Clone()).ToList(),
    };
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

/// <summary>A project board the user asked GitAlert to watch, under a particular account.</summary>
public sealed class BoardSubscription
{
    /// <summary>The <see cref="GitHubAccount.Id"/> whose token is used to read this board.</summary>
    public string AccountId { get; set; } = string.Empty;

    public required string Owner { get; set; }

    /// <summary>Whether the owner is an organisation or a user: the two live on different endpoints.</summary>
    [JsonConverter(typeof(BoardOwnerKindConverter))]
    public BoardOwnerKind OwnerKind { get; set; }

    public required int Number { get; set; }

    /// <summary>The board's name as GitHub showed it when it was added; the list is named by it.</summary>
    public string Title { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>The name the flyout groups this board's alerts under.</summary>
    [JsonIgnore]
    public string Key => Ref.Key;

    [JsonIgnore]
    public BoardRef Ref => new(Owner, OwnerKind, Number);

    [JsonIgnore]
    public string Url => Ref.HtmlUrl;

    /// <summary>The name the status line and the failures call it by.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Key : $"{Owner}/{Title}";

    /// <summary>Key for per-board sync state: the same board under two accounts is two subjects.</summary>
    [JsonIgnore]
    public string StateKey => $"{AccountId}|{Key}";

    public static BoardSubscription From(string accountId, BoardRef board, string title) => new()
    {
        AccountId = accountId,
        Owner = board.Owner,
        OwnerKind = board.OwnerKind,
        Number = board.Number,
        Title = title,
    };

    public BoardSubscription Clone() => new()
    {
        AccountId = AccountId,
        Owner = Owner,
        OwnerKind = OwnerKind,
        Number = Number,
        Title = Title,
        Enabled = Enabled,
    };
}

/// <summary>An owner kind this build has not heard of is asked about as a user first, like an unknown one.</summary>
public sealed class BoardOwnerKindConverter() : TolerantEnumConverter<BoardOwnerKind>(BoardOwnerKind.Unknown);

public enum AppTheme
{
    System,
    Dark,
    Light,
}

/// <summary>A theme this build has not heard of follows Windows, which is what a fresh install does.</summary>
public sealed class AppThemeConverter() : TolerantEnumConverter<AppTheme>(AppTheme.System);

/// <summary>The dark palettes on offer. Light has one look; dark has a choice.</summary>
public enum DarkPalette
{
    /// <summary>VS Code's Dark Modern: neutral greys and a single blue.</summary>
    VsCode,

    /// <summary>GitHub's own dark theme, so the window matches the site the alerts come from.</summary>
    GitHub,
}

/// <summary>A palette this build has not heard of falls back to the default, as a fresh install has.</summary>
public sealed class DarkPaletteConverter() : TolerantEnumConverter<DarkPalette>(DarkPalette.VsCode);
