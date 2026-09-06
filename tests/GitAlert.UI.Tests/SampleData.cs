using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.UI.Tests;

/// <summary>A small day: two projects, three alerts, one push with a diff behind it.</summary>
internal static class SampleData
{
    public const string PushSha = "9f3c2a1d7b6e5f4a3c2b1a0f9e8d7c6b5a4f3e2d";

    /// <summary>What issue #77 says, already boiled down to plain text.</summary>
    public const string IssueBody =
        "When GitHub answers 403 with a retry-after header the poller keeps asking every two minutes, "
        + "so a throttled account never recovers.\n\nExpected: back off for as long as the header says.";

    /// <summary>
    /// With <paramref name="sectioned"/>, api-gateway sits under a section called Work; with
    /// <paramref name="withBoard"/>, the account also watches acme's Roadmap board.
    /// </summary>
    public static AppSettings Settings(GitHubAccount account, bool sectioned = false, bool withBoard = false) => new()
    {
        Accounts = [account],
        Repositories =
        [
            RepoSubscription.From(account.Id, new RepoRef("mbKalkan", "GitAlert")),
            RepoSubscription.From(account.Id, new RepoRef("acme", "api-gateway")),
        ],
        Boards = withBoard
            ? [BoardSubscription.From(account.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap")]
            : [],
        Sections = sectioned ? [new ProjectSection { Name = "Work", Repositories = ["acme/api-gateway"] }] : [],
        PollIntervalMinutes = 2,
    };

    /// <summary>A card that moved column on the Roadmap board, with the fields the pane shows.</summary>
    public static Alert BoardAlert(GitHubAccount account) => new()
    {
        Id = $"{account.Id}|board:12:13:moved:1",
        Kind = AlertKind.Board,
        Title = "Moved to In progress",
        Detail = "#87 Rate limit the poller when GitHub throttles",
        Note = "Status · Todo → In progress",
        Repository = "acme/#12",
        Account = "mbKalkan",
        AccountId = account.Id,
        Url = "https://github.com/acme/api-gateway/issues/87",
        Timestamp = DateTimeOffset.Now - TimeSpan.FromMinutes(6),
        Fields = [new AlertField("Status", "In progress"), new AlertField("Priority", "P1"), new AlertField("Assignees", "deniz-k")],
    };

    public static List<Alert> Alerts(GitHubAccount account)
    {
        var now = DateTimeOffset.Now;

        Alert Make(
            string id,
            AlertKind kind,
            string title,
            string repository,
            TimeSpan age,
            string? diffHead = null,
            bool read = false,
            string? detail = null,
            string? body = null,
            List<AlertField>? fields = null,
            string? url = null) => new()
        {
            Id = $"{account.Id}|{id}",
            Kind = kind,
            Title = title,
            Detail = detail,
            Repository = repository,
            Account = "mbKalkan",
            AccountId = account.Id,
            Actor = "mbKalkan",
            Url = url ?? $"https://github.com/{repository}",
            Timestamp = now - age,
            IsRead = read,
            DiffHead = diffHead,
            Body = body,
            Fields = fields,
        };

        return
        [
            Make($"commit:{PushSha}", AlertKind.Push, "New commit on main", "mbKalkan/GitAlert", TimeSpan.FromMinutes(12), diffHead: PushSha),
            Make("run:1", AlertKind.Workflow, "CI failed (#212)", "mbKalkan/GitAlert", TimeSpan.FromMinutes(9),
                url: "https://github.com/mbKalkan/GitAlert/actions/runs/212"),
            Make("event:2", AlertKind.Issue, "Issue #77 opened", "acme/api-gateway", TimeSpan.FromHours(5), read: true,
                detail: "Rate limit the poller when GitHub throttles",
                body: IssueBody,
                fields: [new AlertField("Labels", "bug, api"), new AlertField("Assignees", "deniz-k")]),
        ];
    }

    public static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitalert-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
