using System.IO;
using GitAlert.Configuration;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// An install that predates multi-account support must keep working untouched: its single token
/// becomes one account and every repository is attached to it.
/// </summary>
public class SettingsMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"gitalert-migration-{Guid.NewGuid():N}");

    private readonly SecureTokenStore _tokens;

    public SettingsMigrationTests()
    {
        Directory.CreateDirectory(_directory);
        _tokens = new SecureTokenStore(_directory);
    }

    /// <summary>
    /// Produces a genuine DPAPI blob at the pre-multi-account path, by writing one through the
    /// store and moving it where the old layout kept it.
    /// </summary>
    private void WriteLegacyToken(string token)
    {
        _tokens.Write("scratch", token);

        File.Move(
            Path.Combine(_directory, "tokens", "scratch.bin"),
            Path.Combine(_directory, "token.bin"),
            overwrite: true);
    }

    [Fact]
    public void The_old_token_and_repositories_become_one_account()
    {
        WriteLegacyToken("ghp_example");

        var settings = new AppSettings
        {
            IncludeInbox = false,
            Repositories =
            [
                new RepoSubscription { Owner = "acme", Name = "api" },
                new RepoSubscription { Owner = "acme", Name = "web" },
            ],
        };

        Assert.True(SettingsMigration.Apply(settings, _tokens));

        var account = Assert.Single(settings.Accounts);
        Assert.All(settings.Repositories, r => Assert.Equal(account.Id, r.AccountId));

        // The old global inbox preference belonged to that one account.
        Assert.False(account.IncludeInbox);

        // The token moved to the account's own file and the legacy blob is gone.
        Assert.Equal("ghp_example", _tokens.Read(account.Id));
        Assert.False(_tokens.HasLegacy);

        // The legacy field is cleared so it cannot be migrated a second time.
        Assert.Null(settings.IncludeInbox);
    }

    [Fact]
    public void Migration_is_idempotent()
    {
        WriteLegacyToken("ghp_example");

        var settings = new AppSettings
        {
            Repositories = [new RepoSubscription { Owner = "acme", Name = "api" }],
        };

        Assert.True(SettingsMigration.Apply(settings, _tokens));
        var accountId = settings.Accounts[0].Id;

        Assert.False(SettingsMigration.Apply(settings, _tokens));
        Assert.Single(settings.Accounts);
        Assert.Equal(accountId, settings.Accounts[0].Id);
    }

    [Fact]
    public void Repositories_added_by_hand_are_adopted_by_the_first_account()
    {
        var account = GitHubAccount.Create("octocat");

        var settings = new AppSettings
        {
            Accounts = [account],
            Repositories =
            [
                new RepoSubscription { AccountId = account.Id, Owner = "acme", Name = "api" },
                new RepoSubscription { Owner = "acme", Name = "web" },
            ],
        };

        Assert.True(SettingsMigration.Apply(settings, _tokens));
        Assert.All(settings.Repositories, r => Assert.Equal(account.Id, r.AccountId));
    }

    [Fact]
    public void A_fresh_install_needs_no_migration()
    {
        var settings = new AppSettings();

        Assert.False(SettingsMigration.Apply(settings, _tokens));
        Assert.Empty(settings.Accounts);
    }

    [Fact]
    public void Tokens_are_stored_per_account_and_pruned_with_them()
    {
        var first = GitHubAccount.Create("first");
        var second = GitHubAccount.Create("second");

        _tokens.Write(first.Id, "token-one");
        _tokens.Write(second.Id, "token-two");

        Assert.Equal("token-one", _tokens.Read(first.Id));
        Assert.Equal("token-two", _tokens.Read(second.Id));

        var all = _tokens.ReadAll([first.Id, second.Id]);
        Assert.Equal(2, all.Count);

        _tokens.Prune([first.Id]);

        Assert.True(_tokens.Has(first.Id));
        Assert.False(_tokens.Has(second.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
