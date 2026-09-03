using System.IO;
using GitAlert.Configuration;
using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gitalert-settings-{Guid.NewGuid():N}.json");

    private static RepoSubscription Repo(string accountId, string owner, string name, bool isPrivate = false) =>
        new() { AccountId = accountId, Owner = owner, Name = name, IsPrivate = isPrivate };

    [Fact]
    public void A_missing_file_yields_usable_defaults()
    {
        var settings = new SettingsStore(_path).Load();

        Assert.Equal(2, settings.PollIntervalMinutes);
        Assert.Empty(settings.Accounts);
        Assert.Empty(settings.Repositories);
        Assert.Empty(settings.MutedKinds);
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var store = new SettingsStore(_path);
        var account = GitHubAccount.Create("octocat");

        store.Save(new AppSettings
        {
            PollIntervalMinutes = 15,
            Theme = AppTheme.Light,
            MutedKinds = [AlertKind.Star, AlertKind.Fork],
            Accounts = [account],
            Repositories = [Repo(account.Id, "acme", "api", isPrivate: true)],
        });

        var loaded = store.Load();

        Assert.Equal(15, loaded.PollIntervalMinutes);
        Assert.Equal(AppTheme.Light, loaded.Theme);
        Assert.True(loaded.IsMuted(AlertKind.Star));
        Assert.False(loaded.IsMuted(AlertKind.Push));

        var reloaded = Assert.Single(loaded.Accounts);
        Assert.Equal("octocat", reloaded.Login);
        Assert.Equal(account.Id, reloaded.Id);

        var repository = Assert.Single(loaded.Repositories);
        Assert.Equal("acme/api", repository.FullName);
        Assert.Equal(account.Id, repository.AccountId);
        Assert.True(repository.IsPrivate);
    }

    [Fact]
    public void Two_accounts_can_watch_their_own_repositories()
    {
        var work = GitHubAccount.Create("work-user");
        var personal = GitHubAccount.Create("personal-user");

        var settings = new AppSettings
        {
            Accounts = [work, personal],
            Repositories =
            [
                Repo(work.Id, "acme", "api"),
                Repo(work.Id, "acme", "web"),
                Repo(personal.Id, "me", "dotfiles"),
            ],
        };

        settings.Normalise();

        Assert.Equal(2, settings.RepositoriesFor(work.Id).Count());
        Assert.Single(settings.RepositoriesFor(personal.Id));
        Assert.Equal(personal, settings.FindAccount(personal.Id));
    }

    [Fact]
    public void The_same_repository_may_be_watched_by_two_accounts_but_not_twice_by_one()
    {
        var first = GitHubAccount.Create("first");
        var second = GitHubAccount.Create("second");

        var settings = new AppSettings
        {
            Accounts = [first, second],
            Repositories =
            [
                Repo(first.Id, "acme", "api"),
                Repo(first.Id, "ACME", "API"),
                Repo(second.Id, "acme", "api"),
            ],
        };

        settings.Normalise();

        Assert.Equal(2, settings.Repositories.Count);
        Assert.Single(settings.RepositoriesFor(first.Id));
        Assert.Single(settings.RepositoriesFor(second.Id));
    }

    /// <summary>
    /// The id names the account's token file. A hand-edited one that could not be a file name
    /// has no token to go with it, so the account is not one GitAlert can sign in as.
    /// </summary>
    [Fact]
    public void An_account_whose_id_could_not_name_a_token_file_is_dropped()
    {
        var good = GitHubAccount.Create("keeper");

        var settings = new AppSettings
        {
            Accounts =
            [
                good,
                new GitHubAccount { Id = @"..\..\escape", Login = "attacker" },
                new GitHubAccount { Id = "   ", Login = "blank" },
            ],
            Repositories = [Repo(good.Id, "acme", "api"), Repo(@"..\..\escape", "acme", "other")],
        };

        settings.Normalise();

        Assert.Single(settings.Accounts);
        Assert.Equal(good.Id, settings.Accounts[0].Id);

        // Its repositories go with it: nothing is left pointing at an account that is not there.
        Assert.Single(settings.Repositories);
        Assert.Equal("acme/api", settings.Repositories[0].FullName);
    }

    /// <summary>
    /// The owner and the name go straight into a request path, sent with the account's token.
    /// The record's constructor does no checking, so the settings file is where it has to happen.
    /// </summary>
    [Fact]
    public void A_repository_whose_owner_or_name_could_not_be_a_path_segment_is_dropped()
    {
        var account = GitHubAccount.Create("octocat");

        var settings = new AppSettings
        {
            Accounts = [account],
            Repositories =
            [
                Repo(account.Id, "acme", "api"),
                Repo(account.Id, "acme", ".."),
                Repo(account.Id, "..", "api"),
                Repo(account.Id, "acme", "api?per_page=1"),
                Repo(account.Id, "acme", "api/../../user"),
                Repo(account.Id, "acme/../../user", "api"),
            ],
        };

        settings.Normalise();

        var kept = Assert.Single(settings.Repositories);
        Assert.Equal("acme/api", kept.FullName);
    }

    /// <summary>
    /// Every list in the file is a valid place for a hand-edited <c>null</c>, and every reader
    /// walks them. Loading used to throw on the first one, past the corrupt-file handling.
    /// </summary>
    [Fact]
    public void Lists_a_hand_edited_file_set_to_null_come_back_empty_rather_than_crashing()
    {
        File.WriteAllText(
            _path,
            """{"accounts":null,"repositories":[null],"mutedKinds":null,"projectOrder":[null,"acme/api"]}""");

        var settings = new SettingsStore(_path).Load();

        Assert.Empty(settings.Accounts);
        Assert.Empty(settings.Repositories);
        Assert.Empty(settings.MutedKinds);
        Assert.Equal(["acme/api"], settings.ProjectOrder);
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void A_repository_whose_account_is_gone_is_dropped()
    {
        var settings = new AppSettings
        {
            Accounts = [],
            Repositories = [Repo("missing-account", "acme", "api")],
        };

        settings.Normalise();

        Assert.Empty(settings.Repositories);
    }

    [Fact]
    public void A_hand_edited_interval_is_clamped_rather_than_trusted()
    {
        var settings = new AppSettings { PollIntervalMinutes = 0, MaxHistory = 5 };
        settings.Normalise();

        Assert.Equal(AppSettings.MinimumPollMinutes, settings.PollIntervalMinutes);
        Assert.Equal(20, settings.MaxHistory);

        settings.PollIntervalMinutes = 10_000;
        settings.Normalise();
        Assert.Equal(AppSettings.MaximumPollMinutes, settings.PollIntervalMinutes);
    }

    [Fact]
    public void A_corrupt_settings_file_is_set_aside_instead_of_blocking_startup()
    {
        File.WriteAllText(_path, "not json at all");

        var settings = new SettingsStore(_path).Load();

        Assert.Equal(2, settings.PollIntervalMinutes);
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Clone_is_deep_enough_that_editing_a_copy_is_safe()
    {
        var account = GitHubAccount.Create("octocat");

        var original = new AppSettings
        {
            Accounts = [account],
            Repositories = [Repo(account.Id, "acme", "api")],
            MutedKinds = [AlertKind.Star],
        };

        var copy = original.Clone();
        copy.Repositories[0].Enabled = false;
        copy.Accounts[0].IncludeInbox = false;
        copy.MutedKinds.Add(AlertKind.Push);

        Assert.True(original.Repositories[0].Enabled);
        Assert.True(original.Accounts[0].IncludeInbox);
        Assert.False(original.IsMuted(AlertKind.Push));
    }

    [Fact]
    public void An_account_without_a_login_still_shows_something_sensible()
    {
        Assert.Equal("Unverified account", GitHubAccount.Create(string.Empty).DisplayName);
        Assert.Equal("@octocat", GitHubAccount.Create("octocat").DisplayName);
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".corrupt" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        GC.SuppressFinalize(this);
    }
}
