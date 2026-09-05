using System.IO;
using GitAlert.Configuration;
using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"gitalert-settings-{Guid.NewGuid():N}");
    private readonly string _path;

    public SettingsTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "settings.json");
    }

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

    /// <summary>
    /// A name this build does not know is what a newer build wrote before the user went back a
    /// version. It used to cost the whole file: set aside as corrupt, every account forgotten.
    /// </summary>
    [Fact]
    public void A_name_this_build_does_not_know_costs_that_entry_rather_than_the_whole_file()
    {
        File.WriteAllText(
            _path,
            """{"pollIntervalMinutes":15,"theme":"Neon","mutedKinds":["Star","Hologram",{"odd":true},7000]}""");

        var settings = new SettingsStore(_path).Load();

        Assert.Equal(15, settings.PollIntervalMinutes);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Single(settings.MutedKinds);
        Assert.Contains(AlertKind.Star, settings.MutedKinds);
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Muted_kinds_and_the_theme_still_round_trip_by_name()
    {
        var store = new SettingsStore(_path);
        store.Save(new AppSettings { Theme = AppTheme.Dark, MutedKinds = [AlertKind.PullRequest] });

        var written = File.ReadAllText(_path);
        Assert.Contains("\"Dark\"", written);
        Assert.Contains("\"PullRequest\"", written);

        var loaded = store.Load();
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.IsMuted(AlertKind.PullRequest));
    }

    /// <summary>
    /// Roaming AppData is what sync agents watch, and a file one of them has open refuses the
    /// rename. That was an exception out of Save; now it is an answer the caller can show.
    /// </summary>
    [Fact]
    public void A_settings_file_that_cannot_be_written_is_reported_rather_than_thrown()
    {
        var store = new SettingsStore(_path);
        Assert.True(store.Save(new AppSettings()));

        using (Unwritable())
        {
            Assert.False(store.Save(new AppSettings { PollIntervalMinutes = 30 }));
        }

        Assert.True(store.Save(new AppSettings { PollIntervalMinutes = 30 }));
        Assert.Equal(30, store.Load().PollIntervalMinutes);
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

    [Fact]
    public void The_list_width_is_kept_as_a_share_and_a_bad_one_is_forgotten()
    {
        var store = new SettingsStore(_path);
        Assert.True(store.Save(new AppSettings { ListPaneShare = 0.4 }));

        Assert.Equal(0.4, store.Load().ListPaneShare);
        Assert.Equal(0.4, new AppSettings { ListPaneShare = 0.4 }.Clone().ListPaneShare);

        var edited = new AppSettings { ListPaneShare = 1.5 };
        edited.Normalise();

        Assert.Null(edited.ListPaneShare);
    }

    /// <summary>
    /// A settings file from before the change list moved under the alert still names the height
    /// of the bar that used to sit beneath it. It is simply not read; nothing else is lost.
    /// </summary>
    [Fact]
    public void A_settings_file_with_the_old_change_list_height_still_loads()
    {
        File.WriteAllText(_path, """{"filesPaneHeight": 240, "listPaneShare": 0.4, "pollIntervalMinutes": 7}""");

        var loaded = new SettingsStore(_path).Load();

        Assert.Equal(0.4, loaded.ListPaneShare);
        Assert.Equal(7, loaded.PollIntervalMinutes);
    }

    [Fact]
    public void The_dark_palette_round_trips_and_an_unknown_one_falls_back_to_the_default()
    {
        var store = new SettingsStore(_path);
        Assert.True(store.Save(new AppSettings { DarkPalette = DarkPalette.GitHub }));

        Assert.Contains("GitHub", File.ReadAllText(_path));
        Assert.Equal(DarkPalette.GitHub, store.Load().DarkPalette);
        Assert.Equal(DarkPalette.GitHub, new AppSettings { DarkPalette = DarkPalette.GitHub }.Clone().DarkPalette);

        File.WriteAllText(_path, "{\"darkPalette\":\"Neon\"}");
        Assert.Equal(DarkPalette.VsCode, store.Load().DarkPalette);
        Assert.Equal(DarkPalette.VsCode, new AppSettings().DarkPalette);
    }

    [Fact]
    public void Sections_round_trip_with_their_fold_and_their_projects()
    {
        var store = new SettingsStore(_path);
        var settings = new AppSettings
        {
            Sections =
            [
                new ProjectSection { Name = "Work", IsCollapsed = true, Repositories = ["acme/api", "acme/web"] },
                new ProjectSection { Name = "Personal" },
            ],
        };

        Assert.True(store.Save(settings));

        var loaded = store.Load();

        Assert.Equal(["Work", "Personal"], loaded.Sections.Select(s => s.Name));
        Assert.True(loaded.Sections[0].IsCollapsed);
        Assert.Equal(["acme/api", "acme/web"], loaded.Sections[0].Repositories);
        Assert.Empty(loaded.Sections[1].Repositories);

        var clone = settings.Clone();
        clone.Sections[0].Repositories.Add("acme/other");
        Assert.Equal(2, settings.Sections[0].Repositories.Count);
    }

    /// <summary>
    /// A hand-edited section list: a null entry, a blank name, a project listed twice and one
    /// listed under two sections. Each is repaired rather than costing the file.
    /// </summary>
    [Fact]
    public void A_hand_edited_section_list_is_repaired_rather_than_refused()
    {
        File.WriteAllText(
            _path,
            """{"sections":[null,{"name":"  ","repositories":["acme/api",null,"acme/api","acme/web"]},{"name":"Second","repositories":null},{"name":"Third","repositories":["acme/web","acme/cli"]}]}""");

        var settings = new SettingsStore(_path).Load();

        Assert.Equal([ProjectSection.DefaultName, "Second", "Third"], settings.Sections.Select(s => s.Name));
        Assert.Equal(["acme/api", "acme/web"], settings.Sections[0].Repositories);
        Assert.Empty(settings.Sections[1].Repositories);
        Assert.Equal(["acme/cli"], settings.Sections[2].Repositories);
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    /// <summary>
    /// Holds the settings file where the store cannot replace it, until disposed.
    /// </summary>
    /// <remarks>
    /// Windows does that with a share lock on the file. Unix lets anyone swap a locked file out,
    /// so there the folder loses its write bit instead and the store cannot even create its
    /// temporary file. Running the tests as root defeats that half, since root ignores the mode.
    /// </remarks>
    private IDisposable Unwritable()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        var mode = File.GetUnixFileMode(_folder);
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new Restore(() => File.SetUnixFileMode(_folder, mode));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
