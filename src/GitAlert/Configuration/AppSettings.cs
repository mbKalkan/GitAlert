using System.Text.Json.Serialization;
using GitAlert.Core;

namespace GitAlert.Configuration;

/// <summary>
/// Everything the user can configure. Serialised to <c>%APPDATA%\GitAlert\settings.json</c>.
/// The access token is <em>not</em> part of this file - see <see cref="SecureTokenStore"/>.
/// </summary>
public sealed class AppSettings
{
    public const int MinimumPollMinutes = 1;
    public const int MaximumPollMinutes = 180;

    public List<RepoSubscription> Repositories { get; set; } = [];

    /// <summary>How often GitAlert asks GitHub what changed.</summary>
    public int PollIntervalMinutes { get; set; } = 2;

    /// <summary>Kinds the user has switched off; anything absent is delivered.</summary>
    public HashSet<AlertKind> MutedKinds { get; set; } = [];

    /// <summary>Also poll the personal inbox (<c>/notifications</c>): mentions, review requests, assignments.</summary>
    public bool IncludeInbox { get; set; } = true;

    /// <summary>Poll GitHub Actions runs for each repository.</summary>
    public bool WatchWorkflowRuns { get; set; } = true;

    /// <summary>When set, only failed / cancelled CI runs raise an alert.</summary>
    public bool OnlyFailedWorkflowRuns { get; set; }

    /// <summary>Ignore activity produced by the signed-in user themselves.</summary>
    public bool IgnoreOwnActivity { get; set; } = true;

    public bool ShowToasts { get; set; } = true;

    public bool PlaySound { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>How many alerts are kept on disk and shown in the flyout.</summary>
    public int MaxHistory { get; set; } = 300;

    public bool IsMuted(AlertKind kind) => MutedKinds.Contains(kind);

    public AppSettings Clone() => new()
    {
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
        Repositories = Repositories
            .Where(r => !string.IsNullOrWhiteSpace(r.Owner) && !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}

/// <summary>A repository the user asked GitAlert to watch.</summary>
public sealed class RepoSubscription
{
    public required string Owner { get; set; }

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Set when GitHub reported the repository as private, purely for the settings UI.</summary>
    public bool IsPrivate { get; set; }

    [JsonIgnore]
    public string FullName => $"{Owner}/{Name}";

    [JsonIgnore]
    public RepoRef Ref => new(Owner, Name);

    public static RepoSubscription From(RepoRef repo) => new() { Owner = repo.Owner, Name = repo.Name };

    public RepoSubscription Clone() => new()
    {
        Owner = Owner,
        Name = Name,
        Enabled = Enabled,
        IsPrivate = IsPrivate,
    };
}

public enum ThemeMode
{
    System,
    Dark,
    Light,
}
