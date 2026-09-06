using System.IO;
using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Boards in the flyout and in the settings: a board is a group like a repository, its alerts
/// count under their own chip, its card fills the pane instead of a diff, and the account card
/// takes a board link the way it takes a repository link.
/// </summary>
public class BoardListTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void A_board_is_a_group_named_by_its_title_and_tagged_as_a_board()
    {
        StaThread.Run(() =>
        {
            var account = GitHubAccount.Create("deniz");
            var settings = Settings(account);
            var flyout = Build(settings, account, Card(account, "board:12:13:moved:1", "Moved to In progress"));

            var group = Assert.Single(flyout.Groups, g => g.Repository == "acme/#12");
            Assert.True(group.IsBoard);
            Assert.Equal("Roadmap", group.DisplayName);
            Assert.Equal("acme/", group.OwnerPrefix);
            Assert.Equal("  board", group.Tag);
            Assert.False(group.CanLoadMore);
            Assert.Single(group.Items);

            var repository = Assert.Single(flyout.Groups, g => g.Repository == "acme/api-gateway");
            Assert.False(repository.IsBoard);
            Assert.Equal("api-gateway", repository.DisplayName);
            Assert.Equal(string.Empty, repository.Tag);
            Assert.True(repository.CanLoadMore);
        });
    }

    [Fact]
    public void A_watched_board_with_nothing_to_say_yet_is_still_listed()
    {
        StaThread.Run(() =>
        {
            var account = GitHubAccount.Create("deniz");
            var flyout = Build(Settings(account), account);

            Assert.Contains(flyout.Groups, g => g.Repository == "acme/#12" && g.DisplayName == "Roadmap" && g.Items.Count == 0);
        });
    }

    [Fact]
    public void Board_alerts_count_under_the_boards_chip_and_show_what_moved_on_their_line()
    {
        StaThread.Run(() =>
        {
            var account = GitHubAccount.Create("deniz");
            var flyout = Build(Settings(account), account, Card(account, "board:12:13:moved:1", "Moved to In progress"));

            var chip = Assert.Single(flyout.Filters, f => f.Filter == AlertFilter.Boards);
            Assert.Equal("Boards", chip.Label);
            Assert.Equal(1, chip.Count);
            Assert.Equal(0, Assert.Single(flyout.Filters, f => f.Filter == AlertFilter.More).Count);

            var row = Assert.Single(flyout.Alerts);
            Assert.Equal(AlertFilter.Boards, row.Group);
            Assert.Equal("Moved to In progress", row.PrimaryText);
            Assert.Equal("#13 Rate limit the poller", row.SecondaryText);
            Assert.Equal("Status · Todo → In progress", row.RowMeta);
            Assert.Contains("Status · Todo → In progress", row.Tooltip);
        });
    }

    [Fact]
    public async Task Picking_a_board_alert_fills_the_pane_with_the_card_and_asks_github_for_nothing()
    {
        await StaThread.RunAsync(async () =>
        {
            var account = GitHubAccount.Create("deniz");
            var flyout = Build(Settings(account), account, Card(account, "board:12:13:moved:1", "Moved to In progress"));
            var row = Assert.Single(flyout.Alerts);

            await flyout.SelectAlertCommand.ExecuteAsync(row);

            var card = flyout.Detail.Card;
            Assert.NotNull(card);
            Assert.True(flyout.Detail.HasCard);
            Assert.False(flyout.Detail.HasNotice);
            Assert.False(flyout.Detail.CanReload);
            Assert.Equal("acme / Roadmap", card.Board);
            Assert.Equal("acme / Roadmap", flyout.Detail.Caption);
            Assert.Equal("https://github.com/orgs/acme/projects/12", card.BoardUrl);
            Assert.Equal("Moved to In progress", card.Headline);
            Assert.Equal("#13 Rate limit the poller", card.Item);
            Assert.Equal("Status · Todo → In progress", card.Note);
            Assert.Equal("https://github.com/acme/api-gateway/issues/13", card.ItemUrl);
            Assert.Equal("Open issue", card.ItemLabel);
            Assert.Collection(card.Fields,
                f => Assert.Equal(("Status", "In progress"), (f.Name, f.Value)),
                f => Assert.Equal(("Priority", "P1"), (f.Name, f.Value)));
        });
    }

    /// <summary>An alert about a board no longer watched still has somewhere to point.</summary>
    [Fact]
    public async Task A_card_from_a_board_that_is_no_longer_watched_still_opens_the_board()
    {
        await StaThread.RunAsync(async () =>
        {
            var account = GitHubAccount.Create("deniz");
            var settings = Settings(account);
            settings.Boards.Clear();
            var flyout = Build(settings, account, Card(account, "board:12:13:moved:1", "Moved to In progress"));

            await flyout.SelectAlertCommand.ExecuteAsync(Assert.Single(flyout.Alerts));

            var card = flyout.Detail.Card!;
            Assert.Equal("acme/#12", card.Board);
            Assert.Equal("https://github.com/users/acme/projects/12", card.BoardUrl);

            var group = Assert.Single(flyout.Groups, g => g.Repository == "acme/#12");
            Assert.Equal("#12", group.DisplayName);
        });
    }

    // ---- The account card in settings -------------------------------------------

    [Fact]
    public async Task A_board_link_is_checked_with_the_token_and_listed_by_its_title()
    {
        await StaThread.RunAsync(async () =>
        {
            using var account = Account(request => request.Path switch
            {
                "/orgs/acme/projectsV2/12" => Responses.Ok("""{"id": 1, "number": 12, "title": "Roadmap", "state": "open"}"""),
                _ => Responses.Status(HttpStatusCode.NotFound),
            });

            account.NewBoardInput = "https://github.com/orgs/acme/projects/12";
            await account.AddBoardCommand.ExecuteAsync(null);

            var board = Assert.Single(account.Boards);
            Assert.Equal("Roadmap", board.Title);
            Assert.Equal(BoardOwnerKind.Organization, board.OwnerKind);
            Assert.Equal("#12 · organisation", board.Tag);
            Assert.Equal("No repositories yet · 1 board", account.RepositorySummary);
            Assert.Equal("Watching acme / Roadmap.", account.BoardMessage);
            Assert.False(account.IsBoardMessageError);
            Assert.Equal(string.Empty, account.NewBoardInput);

            var saved = Assert.Single(account.ToBoardSubscriptions());
            Assert.Equal(account.Id, saved.AccountId);
            Assert.Equal("acme/#12", saved.Key);
        });
    }

    /// <summary>A link that does not say whose board it is gets both endpoints tried, organisation first.</summary>
    [Fact]
    public async Task A_link_without_the_owner_kind_is_tried_as_an_organisation_and_then_as_a_user()
    {
        await StaThread.RunAsync(async () =>
        {
            using var account = Account(request => request.Path switch
            {
                "/users/deniz/projectsV2/3" => Responses.Ok("""{"id": 2, "number": 3, "title": "Side projects", "state": "open"}"""),
                _ => Responses.Status(HttpStatusCode.NotFound),
            });

            account.NewBoardInput = "deniz/projects/3";
            await account.AddBoardCommand.ExecuteAsync(null);

            var board = Assert.Single(account.Boards);
            Assert.Equal(BoardOwnerKind.User, board.OwnerKind);
            Assert.Equal("https://github.com/users/deniz/projects/3", board.Url);
        });
    }

    [Theory]
    [InlineData("not a board", "Paste a board link")]
    [InlineData("https://github.com/orgs/acme/projects/99", AccountViewModel.BoardScopeHint)]
    public async Task A_bad_link_or_a_token_without_the_scope_is_told_what_is_wrong(string input, string expected)
    {
        await StaThread.RunAsync(async () =>
        {
            using var account = Account(_ => Responses.Status(HttpStatusCode.NotFound));

            account.NewBoardInput = input;
            await account.AddBoardCommand.ExecuteAsync(null);

            Assert.Empty(account.Boards);
            Assert.True(account.IsBoardMessageError);
            Assert.Contains(expected, account.BoardMessage);
        });
    }

    [Fact]
    public async Task Finding_boards_lists_the_users_own_and_the_organisations_open_ones_to_tick()
    {
        await StaThread.RunAsync(async () =>
        {
            using var account = Account(request => request.Path switch
            {
                "/user/orgs" => Responses.Ok("""[{"login": "acme"}]"""),
                "/users/deniz/projectsV2" => Responses.Ok("""[{"id": 2, "number": 3, "title": "Side projects", "state": "open", "updated_at": "2026-09-01T10:00:00Z"}]"""),
                "/orgs/acme/projectsV2" => Responses.Ok("""[{"id": 1, "number": 12, "title": "Roadmap", "state": "open", "updated_at": "2026-09-05T10:00:00Z"}, {"id": 9, "number": 2, "title": "Old", "state": "closed"}]"""),
                _ => Responses.Status(HttpStatusCode.NotFound),
            });

            await account.DiscoverBoardsCommand.ExecuteAsync(null);

            Assert.True(account.HasDiscoveredBoards);
            Assert.Equal("2 boards available", account.BoardDiscoverySummary);
            Assert.Equal(["acme/#12", "deniz/#3"], account.DiscoveredBoards.Select(b => b.Key).ToList());

            account.DiscoveredBoards[0].IsWatched = true;
            Assert.Equal("Roadmap", Assert.Single(account.Boards).Title);

            account.RemoveBoardCommand.Execute(account.Boards[0]);
            Assert.Empty(account.Boards);
            Assert.False(account.DiscoveredBoards[0].IsWatched);
        });
    }

    // ---- Helpers ---------------------------------------------------------------

    private static AppSettings Settings(GitHubAccount account) => new()
    {
        Accounts = [account],
        Repositories = [RepoSubscription.From(account.Id, new RepoRef("acme", "api-gateway"))],
        Boards = [BoardSubscription.From(account.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap")],
    };

    private static Alert Card(GitHubAccount account, string id, string title) => new()
    {
        Id = $"{account.Id}|{id}",
        Kind = AlertKind.Board,
        Title = title,
        Detail = "#13 Rate limit the poller",
        Note = "Status · Todo → In progress",
        Repository = "acme/#12",
        Account = "deniz",
        AccountId = account.Id,
        Url = "https://github.com/acme/api-gateway/issues/13",
        Timestamp = DateTimeOffset.Now,
        Fields = [new AlertField("Status", "In progress"), new AlertField("Priority", "P1")],
    };

    private FlyoutViewModel Build(AppSettings settings, GitHubAccount account, params Alert[] alerts)
    {
        var store = new AlertStore(NewFile());
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        monitor.Configure(settings, new Dictionary<string, string> { [account.Id] = "ghp_token" });

        return new FlyoutViewModel(store, monitor, new SilentShell(), settings);
    }

    private static AccountViewModel Account(Func<RecordedRequest, HttpResponseMessage> respond) =>
        new(GitHubAccount.Create("deniz"), "ghp_token", new HttpClient(new StubHandler(respond)), _ => { });

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-boardlist-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        foreach (var file in _files.Where(File.Exists))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class SilentShell : IShellCommands
    {
        public void ShowSettings()
        {
        }

        public void HideFlyout()
        {
        }

        public void Quit()
        {
        }

        public void SaveListPreferences(ListPreferences preferences)
        {
        }

        public void UnreadChanged()
        {
        }
    }
}
