using System.IO;
using System.Text.Json;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Platform;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Moving the settings between machines as one file: what the file carries and what it leaves
/// out, how an import treats the accounts already here, and how the window takes it in.
/// </summary>
public class SettingsPortabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gitalert-portability-{Guid.NewGuid():N}");

    public SettingsPortabilityTests() => Directory.CreateDirectory(_root);

    // ---- The file ---------------------------------------------------------------

    [Fact]
    public void The_export_carries_everything_but_the_window_and_the_startup_entry()
    {
        var settings = Sample();
        settings.WindowLeft = 100;
        settings.WindowTop = 200;
        settings.WindowWidth = 900;
        settings.WindowHeight = 600;
        settings.ListPaneShare = 0.4;
        settings.StartWithWindows = true;

        var json = SettingsPortability.Export(settings, "2.6.0", new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("gitalert").GetProperty("format").GetInt32());
        Assert.Equal("2.6.0", root.GetProperty("gitalert").GetProperty("version").GetString());

        var exported = root.GetProperty("settings");
        Assert.Equal(2, exported.GetProperty("accounts").GetArrayLength());
        Assert.Equal(3, exported.GetProperty("repositories").GetArrayLength());
        Assert.Equal(1, exported.GetProperty("boards").GetArrayLength());
        Assert.Equal("Work", exported.GetProperty("sections")[0].GetProperty("name").GetString());
        Assert.False(exported.GetProperty("startWithWindows").GetBoolean());
        Assert.False(exported.TryGetProperty("windowLeft", out _));
        Assert.False(exported.TryGetProperty("listPaneShare", out _));
        Assert.DoesNotContain("ghp_", json, StringComparison.Ordinal);

        // What was exported is untouched.
        Assert.Equal(100, settings.WindowLeft);
        Assert.True(settings.StartWithWindows);
    }

    [Fact]
    public void An_import_brings_the_file_and_keeps_this_machine_s_window_and_startup_entry()
    {
        var exported = Sample();
        var json = SettingsPortability.Export(exported, "2.6.0");

        var here = new AppSettings { WindowLeft = 10, WindowTop = 20, WindowWidth = 800, WindowHeight = 500, ListPaneShare = 0.55, StartWithWindows = true };
        var import = SettingsPortability.Import(json, here);

        Assert.Equal(2, import.Accounts);
        Assert.Equal(3, import.Repositories);
        Assert.Equal(1, import.Boards);
        Assert.Equal(2, import.Sections);
        Assert.Equal(0, import.AccountsMatchedByLogin);
        Assert.Equal("GitAlert 2.6.0", import.WrittenBy);

        var settings = import.Settings;
        Assert.Equal(10, settings.WindowLeft);
        Assert.Equal(0.55, settings.ListPaneShare);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(5, settings.PollIntervalMinutes);
        Assert.Contains(AlertKind.Star, settings.MutedKinds);
        Assert.Equal(["acme/api", "acme/web", "me/dotfiles"], settings.Repositories.Select(r => r.FullName));
        Assert.Equal("Inner", settings.Sections[0].Sections[0].Name);
        Assert.True(settings.ProjectFolds["acme/web"]);
    }

    [Fact]
    public void An_account_already_here_by_login_keeps_its_id_so_its_token_keeps_working()
    {
        var exported = Sample();
        var json = SettingsPortability.Export(exported, "2.6.0");

        var localWork = GitHubAccount.Create("work-user");
        var here = new AppSettings { Accounts = [localWork] };

        var import = SettingsPortability.Import(json, here);

        Assert.Equal(1, import.AccountsMatchedByLogin);

        var work = import.Settings.Accounts.Single(a => a.Login == "work-user");
        Assert.Equal(localWork.Id, work.Id);
        Assert.Equal(2, import.Settings.RepositoriesFor(localWork.Id).Count());
        Assert.Single(import.Settings.BoardsFor(localWork.Id));

        var personal = import.Settings.Accounts.Single(a => a.Login == "personal-user");
        Assert.Equal(exported.Accounts[1].Id, personal.Id);
        Assert.Single(import.Settings.RepositoriesFor(personal.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"settings":{}}""")]
    [InlineData("""[1,2,3]""")]
    public void A_file_that_is_not_an_export_is_refused_in_plain_words(string json)
    {
        var error = Assert.Throws<SettingsImportException>(() => SettingsPortability.Import(json, new AppSettings()));

        Assert.Equal("That is not a GitAlert settings file.", error.Message);
    }

    [Fact]
    public void A_file_from_a_newer_gitalert_says_to_update_first()
    {
        var json = """{"gitalert":{"format":9,"version":"9.0.0"},"settings":{}}""";

        var error = Assert.Throws<SettingsImportException>(() => SettingsPortability.Import(json, new AppSettings()));

        Assert.Contains("GitAlert 9.0.0", error.Message, StringComparison.Ordinal);
        Assert.Contains("Update GitAlert first", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_edited_export_is_tidied_the_way_a_settings_file_is()
    {
        var json = """
            {"gitalert":{"format":1,"version":"2.6.0"},
             "settings":{"pollIntervalMinutes":999,"accounts":[{"id":"0123456789abcdef0123456789abcdef","login":"x"}],
                         "repositories":[{"accountId":"0123456789abcdef0123456789abcdef","owner":"acme","name":"api"},{"accountId":"gone","owner":"acme","name":"lost"},null],
                         "sections":[{"name":"  ","repositories":["acme/api","acme/api"]}]}}
            """;

        var import = SettingsPortability.Import(json, new AppSettings());

        Assert.Equal(AppSettings.MaximumPollMinutes, import.Settings.PollIntervalMinutes);
        Assert.Single(import.Settings.Repositories);
        Assert.Equal("New section", import.Settings.Sections[0].Name);
        Assert.Single(import.Settings.Sections[0].Repositories);
    }

    // ---- The window -------------------------------------------------------------

    [Fact]
    public void Importing_fills_the_window_and_save_writes_it_and_tells_the_shell_the_list_changed()
    {
        var store = new SettingsStore(Path.Combine(_root, "settings.json"));
        var tokens = new SecureTokenStore(new PlainProtector(), _root);
        var local = GitHubAccount.Create("work-user");
        tokens.Write(local.Id, "ghp_local");
        store.Save(new AppSettings { Accounts = [local], Repositories = [Repo(local.Id, "old", "thing")], PollIntervalMinutes = 1 });

        var host = new RecordingHost();
        using var window = new SettingsViewModel(store, tokens, host, new StartupOff());

        Assert.Single(window.Accounts);
        Assert.Equal(1, window.PollIntervalMinutes);

        var file = Path.Combine(_root, "export.json");
        File.WriteAllText(file, SettingsPortability.Export(Sample(), "2.6.0"));

        Assert.True(window.ImportFrom(file));

        Assert.False(window.IsMessageError);
        Assert.Contains("2 accounts, 3 repositories, 1 board and 2 sections from export.json", window.Message, StringComparison.Ordinal);
        Assert.Contains("One account needs its token", window.Message, StringComparison.Ordinal);

        Assert.Equal(2, window.Accounts.Count);
        Assert.Equal(5, window.PollIntervalMinutes);
        Assert.False(window.Kinds.Single(k => k.Kind == AlertKind.Star).IsEnabled);

        var work = window.Accounts.Single(a => a.Login == "work-user");
        Assert.Equal(local.Id, work.Id);
        Assert.True(work.HasStoredToken, "the login matched, so the token here still applies");
        Assert.Equal(2, work.Repositories.Count);

        var personal = window.Accounts.Single(a => a.Login == "personal-user");
        Assert.False(personal.HasStoredToken);

        // Nothing is on disk until Save.
        Assert.Single(store.Load().Accounts);

        window.SaveCommand.Execute(null);

        Assert.Equal(["apply with list", "close saved"], host.Calls);
        Assert.True(host.LastListReplaced);

        var saved = store.Load();
        Assert.Equal(2, saved.Accounts.Count);
        Assert.Equal(["acme/api", "acme/web", "me/dotfiles"], saved.Repositories.Select(r => r.FullName));
        Assert.Equal("Work", saved.Sections[0].Name);
        Assert.Equal("ghp_local", tokens.Read(local.Id));

        // A second, ordinary save no longer claims the list changed.
        window.SaveCommand.Execute(null);
        Assert.Equal("apply", host.Calls[^2]);
    }

    [Fact]
    public void A_bad_file_is_reported_and_the_window_keeps_what_it_had()
    {
        var store = new SettingsStore(Path.Combine(_root, "settings.json"));
        var tokens = new SecureTokenStore(new PlainProtector(), _root);
        var local = GitHubAccount.Create("work-user");
        store.Save(new AppSettings { Accounts = [local], PollIntervalMinutes = 1 });

        using var window = new SettingsViewModel(store, tokens, new RecordingHost(), new StartupOff());

        Assert.False(window.Import("{\"hello\":1}", "wrong.json"));

        Assert.True(window.IsMessageError);
        Assert.Equal("That is not a GitAlert settings file.", window.Message);
        Assert.Single(window.Accounts);
        Assert.Equal(1, window.PollIntervalMinutes);

        Assert.False(window.ImportFrom(Path.Combine(_root, "missing.json")));
        Assert.Contains("Could not read missing.json", window.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_export_from_the_window_is_what_the_window_shows_saved_or_not()
    {
        var store = new SettingsStore(Path.Combine(_root, "settings.json"));
        var tokens = new SecureTokenStore(new PlainProtector(), _root);
        var local = GitHubAccount.Create("work-user");
        tokens.Write(local.Id, "ghp_local");
        store.Save(new AppSettings { Accounts = [local], PollIntervalMinutes = 1 });

        using var window = new SettingsViewModel(store, tokens, new RecordingHost(), new StartupOff());
        window.PollIntervalMinutes = 15;

        var file = Path.Combine(_root, "out.json");
        window.ExportTo(file);

        Assert.False(window.IsMessageError);
        Assert.Contains("Exported the settings to out.json", window.Message, StringComparison.Ordinal);

        var json = File.ReadAllText(file);
        Assert.DoesNotContain("ghp_local", json, StringComparison.Ordinal);

        var back = SettingsPortability.Import(json, new AppSettings());
        Assert.Equal(15, back.Settings.PollIntervalMinutes);
        Assert.Equal("work-user", back.Settings.Accounts.Single().Login);

        // The file on disk is still the old one: exporting is not saving.
        Assert.Equal(1, store.Load().PollIntervalMinutes);
    }

    [Fact]
    public void The_suggested_file_name_carries_the_day()
    {
        Assert.Equal("GitAlert-settings-2026-09-06.json", SettingsPortability.SuggestedFileName(new DateTimeOffset(2026, 9, 6, 15, 0, 0, TimeSpan.Zero)));
    }

    // ---- Plumbing ----------------------------------------------------------

    private static RepoSubscription Repo(string accountId, string owner, string name) =>
        new() { AccountId = accountId, Owner = owner, Name = name };

    /// <summary>Two accounts, three repositories, a board, a nested section, one fold, and a few switches turned.</summary>
    private static AppSettings Sample()
    {
        var work = GitHubAccount.Create("work-user");
        var personal = GitHubAccount.Create("personal-user");

        var settings = new AppSettings
        {
            Accounts = [work, personal],
            Repositories = [Repo(work.Id, "acme", "api"), Repo(work.Id, "acme", "web"), Repo(personal.Id, "me", "dotfiles")],
            Boards = [BoardSubscription.From(work.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap")],
            Sections =
            [
                new ProjectSection
                {
                    Name = "Work",
                    Repositories = ["acme/api"],
                    Sections = [new ProjectSection { Name = "Inner", Repositories = ["acme/web"] }],
                },
            ],
            ProjectOrder = ["acme/web", "acme/api"],
            PollIntervalMinutes = 5,
            MutedKinds = [AlertKind.Star],
            Theme = AppTheme.Light,
        };

        settings.ProjectFolds["acme/web"] = true;
        return settings;
    }

    private sealed class RecordingHost : ISettingsHost
    {
        public List<string> Calls { get; } = [];

        public bool LastListReplaced { get; private set; }

        public void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens, bool listReplaced)
        {
            LastListReplaced = listReplaced;
            Calls.Add(listReplaced ? "apply with list" : "apply");
        }

        public void ResetMonitorState() => Calls.Add("reset");

        public void ClearHistory() => Calls.Add("clear");

        public void CloseSettings(bool saved) => Calls.Add(saved ? "close saved" : "close");
    }

    private sealed class StartupOff : IStartupRegistrar
    {
        public bool IsEnabled => false;

        public bool SetEnabled(bool enabled) => true;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
