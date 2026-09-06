using System.IO;
using GitAlert.Configuration;
using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

/// <summary>Boards in the settings file: they round trip, and a hand-edited file is repaired rather than trusted.</summary>
public class BoardSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"gitalert-boards-{Guid.NewGuid():N}");
    private readonly string _path;

    public BoardSettingsTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "settings.json");
    }

    [Fact]
    public void Boards_round_trip_through_disk_with_the_status_only_switch()
    {
        var account = GitHubAccount.Create("deniz");
        var store = new SettingsStore(_path);

        store.Save(new AppSettings
        {
            Accounts = [account],
            Boards =
            [
                BoardSubscription.From(account.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap"),
                new BoardSubscription { AccountId = account.Id, Owner = "deniz", OwnerKind = BoardOwnerKind.User, Number = 3, Title = "Side projects", Enabled = false },
            ],
            OnlyStatusChangesOnBoards = false,
        });

        var loaded = store.Load();

        Assert.Equal(2, loaded.Boards.Count);
        Assert.Equal("acme/#12", loaded.Boards[0].Key);
        Assert.Equal(BoardOwnerKind.Organization, loaded.Boards[0].OwnerKind);
        Assert.Equal("Roadmap", loaded.Boards[0].Title);
        Assert.Equal("https://github.com/orgs/acme/projects/12", loaded.Boards[0].Url);
        Assert.Equal(BoardOwnerKind.User, loaded.Boards[1].OwnerKind);
        Assert.False(loaded.Boards[1].Enabled);
        Assert.False(loaded.OnlyStatusChangesOnBoards);
    }

    [Fact]
    public void A_settings_file_from_before_boards_existed_loads_with_none_and_the_switch_on()
    {
        File.WriteAllText(_path, """{ "accounts": [], "repositories": [], "pollIntervalMinutes": 2 }""");

        var loaded = new SettingsStore(_path).Load();

        Assert.Empty(loaded.Boards);
        Assert.True(loaded.OnlyStatusChangesOnBoards);
    }

    [Fact]
    public void A_hand_edited_board_list_is_repaired_rather_than_trusted()
    {
        var account = GitHubAccount.Create("deniz");

        var settings = new AppSettings
        {
            Accounts = [account],
            Boards =
            [
                new BoardSubscription { AccountId = account.Id, Owner = "acme", Number = 12, Title = "  " },
                new BoardSubscription { AccountId = account.Id, Owner = "acme", Number = 12, Title = "Twice" },
                new BoardSubscription { AccountId = account.Id, Owner = "../etc", Number = 4 },
                new BoardSubscription { AccountId = account.Id, Owner = "acme", Number = 0 },
                new BoardSubscription { AccountId = "gone", Owner = "acme", Number = 5 },
                new BoardSubscription { AccountId = "", Owner = "acme", Number = 6 },
                null!,
            ],
        };

        settings.Normalise();

        var board = Assert.Single(settings.Boards);
        Assert.Equal(12, board.Number);
        Assert.Equal("#12", board.Title);
    }

    [Fact]
    public void A_board_with_its_tick_off_counts_as_switched_off_by_its_key()
    {
        var account = GitHubAccount.Create("deniz");

        var settings = new AppSettings
        {
            Accounts = [account],
            Repositories = [new RepoSubscription { AccountId = account.Id, Owner = "acme", Name = "api", Enabled = false }],
            Boards = [new BoardSubscription { AccountId = account.Id, Owner = "acme", Number = 12, Enabled = false }],
        };

        Assert.Equal(["acme/api", "acme/#12"], settings.SwitchedOffRepositories.ToList());
        Assert.True(settings.IsSwitchedOff("acme/#12"));
        Assert.Equal(["acme/api", "acme/#12"], settings.WatchedNames.ToList());
        Assert.Single(settings.BoardsFor(account.Id));
        Assert.Empty(settings.BoardsFor("other"));
    }

    [Fact]
    public void A_clone_carries_the_boards_without_sharing_them()
    {
        var account = GitHubAccount.Create("deniz");
        var settings = new AppSettings
        {
            Accounts = [account],
            Boards = [BoardSubscription.From(account.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap")],
        };

        var clone = settings.Clone();
        clone.Boards[0].Title = "Renamed";

        Assert.Equal("Roadmap", settings.Boards[0].Title);
        Assert.Equal("acme/Roadmap", settings.Boards[0].DisplayName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
