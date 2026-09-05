using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.UI.Tests;

/// <summary>A small day: two projects, three alerts, one push with a diff behind it.</summary>
internal static class SampleData
{
    public const string PushSha = "9f3c2a1d7b6e5f4a3c2b1a0f9e8d7c6b5a4f3e2d";

    /// <summary>With <paramref name="sectioned"/>, api-gateway sits under a section called Work.</summary>
    public static AppSettings Settings(GitHubAccount account, bool sectioned = false) => new()
    {
        Accounts = [account],
        Repositories =
        [
            RepoSubscription.From(account.Id, new RepoRef("mbKalkan", "GitAlert")),
            RepoSubscription.From(account.Id, new RepoRef("acme", "api-gateway")),
        ],
        Sections = sectioned ? [new ProjectSection { Name = "Work", Repositories = ["acme/api-gateway"] }] : [],
        PollIntervalMinutes = 2,
    };

    public static List<Alert> Alerts(GitHubAccount account)
    {
        var now = DateTimeOffset.Now;

        Alert Make(string id, AlertKind kind, string title, string repository, TimeSpan age, string? diffHead = null, bool read = false) => new()
        {
            Id = $"{account.Id}|{id}",
            Kind = kind,
            Title = title,
            Repository = repository,
            Account = "mbKalkan",
            AccountId = account.Id,
            Actor = "mbKalkan",
            Url = $"https://github.com/{repository}",
            Timestamp = now - age,
            IsRead = read,
            DiffHead = diffHead,
        };

        return
        [
            Make($"commit:{PushSha}", AlertKind.Push, "New commit on main", "mbKalkan/GitAlert", TimeSpan.FromMinutes(12), diffHead: PushSha),
            Make("run:1", AlertKind.Workflow, "CI failed (#212)", "mbKalkan/GitAlert", TimeSpan.FromMinutes(9)),
            Make("event:2", AlertKind.Issue, "Issue #77 opened", "acme/api-gateway", TimeSpan.FromHours(5), read: true),
        ];
    }

    public static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitalert-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
