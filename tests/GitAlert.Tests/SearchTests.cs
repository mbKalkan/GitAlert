using System.IO;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The search box. Every word typed has to be found somewhere on an alert for it to stay in the
/// list; a project with nothing left drops out, a folded one with a match opens for the search,
/// and clearing the box gives the list back exactly as it was.
/// </summary>
public class SearchTests : IDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public void Every_word_has_to_be_found_on_the_alert_for_it_to_show()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Settings(), SampleAlerts());

            flyout.SearchText = "deploy";

            Assert.Equal(["Deploy to staging failed", "Deploy pipeline"], Titles(flyout));

            flyout.SearchText = "deploy staging";

            Assert.Equal(["Deploy to staging failed"], Titles(flyout));

            flyout.SearchText = "DEPLOY   STAGING ";

            Assert.Equal(["Deploy to staging failed"], Titles(flyout));
        });
    }

    [Fact]
    public void The_message_the_repository_the_actor_and_what_was_said_are_searched_too()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Settings(), SampleAlerts());

            flyout.SearchText = "throttles";
            Assert.Equal(["Issue #77 opened"], Titles(flyout));

            flyout.SearchText = "retry-after";
            Assert.Equal(["Issue #77 opened"], Titles(flyout));

            flyout.SearchText = "deniz";
            Assert.Equal(["Deploy pipeline"], Titles(flyout));

            flyout.SearchText = "api-gateway";
            Assert.Equal(["Issue #77 opened", "Deploy pipeline"], Titles(flyout));
        });
    }

    [Fact]
    public void A_board_is_found_by_the_name_on_its_header()
    {
        StaThread.Run(() =>
        {
            var account = GitHubAccount.Create("mbKalkan");
            var settings = Settings();
            settings.Accounts = [account];
            settings.Boards = [BoardSubscription.From(account.Id, new BoardRef("acme", BoardOwnerKind.Organization, 12), "Roadmap")];

            var flyout = Build(settings, [.. SampleAlerts(), BoardCard()]);

            flyout.SearchText = "roadmap";

            Assert.Equal(["Moved to In progress"], Titles(flyout));
            Assert.Equal(["acme/#12"], flyout.Groups.Select(g => g.Repository));
        });
    }

    [Fact]
    public void A_project_with_nothing_found_leaves_the_list_and_comes_back_when_the_box_is_cleared()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Settings(Section("Work", "acme/api-gateway")), SampleAlerts());

            Assert.Equal(["mbKalkan/GitAlert", "#Work", "acme/api-gateway"], Rows(flyout));

            flyout.SearchText = "staging";

            Assert.Equal(["mbKalkan/GitAlert"], Rows(flyout));

            flyout.SearchText = string.Empty;

            Assert.Equal(["mbKalkan/GitAlert", "#Work", "acme/api-gateway"], Rows(flyout));
        });
    }

    [Fact]
    public void A_folded_project_and_a_folded_section_open_for_the_search_and_fold_back_after_it()
    {
        StaThread.Run(() =>
        {
            var work = Section("Work", "acme/api-gateway");
            work.IsCollapsed = true;
            var settings = Settings(work);
            settings.ProjectFolds["mbKalkan/GitAlert"] = true;
            settings.ProjectFolds["acme/api-gateway"] = true;

            var shell = new RecordingShell();
            var flyout = Build(shell, settings, SampleAlerts());
            var gitalert = Group(flyout, "mbKalkan/GitAlert");
            var gateway = Group(flyout, "acme/api-gateway");

            Assert.False(gitalert.IsOpen);
            Assert.Equal(["mbKalkan/GitAlert", "#Work"], Rows(flyout));

            flyout.SearchText = "deploy";

            Assert.True(gitalert.IsOpen);
            Assert.True(gateway.IsOpen);
            Assert.False(gitalert.IsExpanded, "the fold itself is untouched");
            Assert.Equal(["mbKalkan/GitAlert", "#Work", "acme/api-gateway"], Rows(flyout));
            Assert.Equal(0, shell.Saves);

            flyout.SearchText = string.Empty;

            Assert.False(gitalert.IsOpen);
            Assert.False(gateway.IsOpen);
            Assert.Equal(["mbKalkan/GitAlert", "#Work"], Rows(flyout));
            Assert.Equal(0, shell.Saves);
        });
    }

    [Fact]
    public void Clicking_a_project_the_search_opened_folds_it_for_the_rest_of_that_search()
    {
        StaThread.Run(() =>
        {
            var settings = Settings();
            settings.ProjectFolds["mbKalkan/GitAlert"] = true;

            var flyout = Build(settings, SampleAlerts());
            var gitalert = Group(flyout, "mbKalkan/GitAlert");

            flyout.SearchText = "deploy";
            Assert.True(gitalert.IsOpen);

            gitalert.ToggleCommand.Execute(null);
            Assert.False(gitalert.IsOpen);

            gitalert.ToggleCommand.Execute(null);
            Assert.True(gitalert.IsOpen);
            Assert.True(gitalert.IsExpanded);

            // A new search opens it again, whatever was done during the last one.
            gitalert.ToggleCommand.Execute(null);
            flyout.SearchText = "deploy failed";
            Assert.True(gitalert.IsOpen);
        });
    }

    [Fact]
    public void The_chips_count_what_the_search_finds_and_unread_only_narrows_it_further()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Settings(), SampleAlerts());

            Assert.Equal(3, Chip(flyout, AlertFilter.All).Count);

            flyout.SearchText = "deploy";

            Assert.Equal(2, Chip(flyout, AlertFilter.All).Count);
            Assert.Equal(1, Chip(flyout, AlertFilter.Ci).Count);
            Assert.Equal(1, Chip(flyout, AlertFilter.Push).Count);
            Assert.Equal(0, Chip(flyout, AlertFilter.Issues).Count);

            flyout.ToggleUnreadOnlyCommand.Execute(null);
            Group(flyout, "acme/api-gateway").Items.Single().MarkRead();
            flyout.ToggleUnreadOnlyCommand.Execute(null);
            flyout.ToggleUnreadOnlyCommand.Execute(null);

            Assert.Equal(["Deploy to staging failed"], Titles(flyout));
            Assert.Equal(1, Chip(flyout, AlertFilter.All).Count);
        });
    }

    [Fact]
    public void A_search_that_finds_nothing_says_so_and_names_the_words()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(Settings(), SampleAlerts());

            flyout.SearchText = "kubernetes";

            Assert.True(flyout.IsEmpty);
            Assert.Contains("“kubernetes”", flyout.EmptyMessage, StringComparison.Ordinal);

            flyout.ClearSearchCommand.Execute(null);

            Assert.False(flyout.IsEmpty);
            Assert.Equal(string.Empty, flyout.SearchText);
            Assert.False(flyout.IsSearching);
        });
    }

    [Fact]
    public void An_alert_that_arrives_during_a_search_shows_only_when_it_matches()
    {
        StaThread.Run(() =>
        {
            var (flyout, store, _) = BuildWithStore(new RecordingShell(), Settings(), SampleAlerts());

            flyout.SearchText = "deploy";
            Assert.Equal(2, flyout.Alerts.Count);

            store.Add([
                Alert("9", AlertKind.Issue, "Deploy docs are wrong", "acme/api-gateway"),
                Alert("10", AlertKind.Issue, "Typo in README", "acme/api-gateway"),
            ]);
            flyout.Reload();

            Assert.Equal(["Deploy to staging failed", "Deploy pipeline", "Deploy docs are wrong"], Titles(flyout));
            Assert.True(Group(flyout, "acme/api-gateway").IsOpen, "a project rebuilt during a search is open for it too");
        });
    }

    // ---- Plumbing ----------------------------------------------------------

    private static List<string> Titles(FlyoutViewModel flyout) => [.. flyout.Alerts.Select(a => a.PrimaryText)];

    private static List<string> Rows(FlyoutViewModel flyout) =>
    [
        .. flyout.Rows.Select(r => r switch
        {
            ProjectGroupViewModel group => group.Repository,
            ProjectSectionViewModel section => "#" + section.Name,
            _ => "?",
        }),
    ];

    private static ProjectGroupViewModel Group(FlyoutViewModel flyout, string repository) =>
        flyout.Groups.Single(g => g.Repository == repository);

    private static FilterChipViewModel Chip(FlyoutViewModel flyout, AlertFilter filter) =>
        flyout.Filters.Single(c => c.Filter == filter);

    private static ProjectSection Section(string name, params string[] repositories) =>
        new() { Name = name, Repositories = [.. repositories] };

    private static AppSettings Settings(params ProjectSection[] sections) =>
        new() { ProjectOrder = ["mbKalkan/GitAlert", "acme/api-gateway"], Sections = [.. sections] };

    private static Alert[] SampleAlerts() =>
    [
        Alert("1", AlertKind.Workflow, "Deploy to staging failed", "mbKalkan/GitAlert", detail: "deploy.yml · main"),
        Alert("2", AlertKind.Issue, "Issue #77 opened", "acme/api-gateway",
            detail: "Rate limit the poller when GitHub throttles",
            body: "When GitHub answers 403 with a retry-after header the poller keeps asking."),
        Alert("3", AlertKind.Push, "New commit on main", "acme/api-gateway", detail: "Deploy pipeline", actor: "deniz-k"),
    ];

    private static Alert BoardCard() => new()
    {
        Id = "account|board:12:13:moved:1",
        Kind = AlertKind.Board,
        Title = "Moved to In progress",
        Detail = "#87 Rate limit the poller",
        Repository = "acme/#12",
        Timestamp = DateTimeOffset.UtcNow,
    };

    private static Alert Alert(
        string id,
        AlertKind kind,
        string title,
        string repository,
        string? detail = null,
        string? body = null,
        string? actor = null) => new()
    {
        Id = $"account|event:{id}",
        Kind = kind,
        Title = title,
        Detail = detail,
        Body = body,
        Actor = actor,
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(int.Parse(id)),
    };

    private FlyoutViewModel Build(AppSettings settings, params Alert[] alerts) => Build(new RecordingShell(), settings, alerts);

    private FlyoutViewModel Build(IShellCommands shell, AppSettings settings, params Alert[] alerts) =>
        BuildWithStore(shell, settings, alerts).Flyout;

    private (FlyoutViewModel Flyout, AlertStore Store, MonitorService Monitor) BuildWithStore(IShellCommands shell, AppSettings settings, params Alert[] alerts)
    {
        var store = new AlertStore(NewFile());
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        monitor.Configure(settings, new Dictionary<string, string>());

        return (new FlyoutViewModel(store, monitor, shell, settings), store, monitor);
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-search-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <summary>A shell that counts how often the list asked it to save.</summary>
    private sealed class RecordingShell : IShellCommands
    {
        public int Saves { get; private set; }

        public void ShowSettings()
        {
        }

        public void HideFlyout()
        {
        }

        public void Quit()
        {
        }

        public void SaveListPreferences(ListPreferences preferences) => Saves++;

        public void UnreadChanged()
        {
        }
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
}
