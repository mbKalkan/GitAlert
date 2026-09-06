using System.IO;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Folding the list. "Collapse all" and "Expand all" work in steps - the projects and the sections
/// separately - so the outline survives a press, and the fold a project is left in by hand is
/// written down and comes back after a restart.
/// </summary>
public class ProjectFoldTests : IDisposable
{
    private readonly List<string> _files = [];

    // ---- In steps -----------------------------------------------------------------

    [Fact]
    public void Collapse_all_folds_the_projects_first_and_the_sections_only_when_no_project_on_screen_is_open()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            flyout.CollapseAllCommand.Execute(null);

            Assert.All(flyout.Groups, g => Assert.False(g.IsExpanded));
            Assert.True(Section(flyout, "Work").IsExpanded);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "acme/delta"], Rows(flyout));

            // Opening one project by hand makes the next press a projects press again.
            Group(flyout, "acme/gamma").IsExpanded = true;
            flyout.CollapseAllCommand.Execute(null);

            Assert.False(Group(flyout, "acme/gamma").IsExpanded);
            Assert.True(Section(flyout, "Work").IsExpanded);

            flyout.CollapseAllCommand.Execute(null);

            Assert.False(Section(flyout, "Work").IsExpanded);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work"], Rows(flyout));
        });
    }

    [Fact]
    public void Expand_all_opens_the_sections_first_and_the_projects_only_once_every_section_is_open()
    {
        StaThread.Run(() =>
        {
            var work = Section("Work", "acme/gamma", "acme/delta");
            work.IsCollapsed = true;
            var settings = Settings(work);
            settings.ProjectFolds["acme/alpha"] = true;
            settings.ProjectFolds["acme/gamma"] = true;

            var flyout = Build(new RecordingShell(), settings, FourAlerts());

            flyout.ExpandAllCommand.Execute(null);

            Assert.True(Section(flyout, "Work").IsExpanded);
            Assert.False(Group(flyout, "acme/alpha").IsExpanded);
            Assert.False(Group(flyout, "acme/gamma").IsExpanded);
            Assert.True(Group(flyout, "acme/beta").IsExpanded);

            flyout.ExpandAllCommand.Execute(null);

            Assert.All(flyout.Groups, g => Assert.True(g.IsExpanded));
        });
    }

    [Fact]
    public void With_no_sections_the_two_buttons_work_the_projects_in_one_press_and_a_press_with_nothing_to_do_does_nothing()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(), FourAlerts());

            flyout.CollapseAllCommand.Execute(null);
            Assert.All(flyout.Groups, g => Assert.False(g.IsExpanded));

            shell.Forget();
            flyout.CollapseAllCommand.Execute(null);
            Assert.Equal(0, shell.Saves);

            flyout.ExpandAllCommand.Execute(null);
            Assert.All(flyout.Groups, g => Assert.True(g.IsExpanded));
        });
    }

    // ---- Remembered ---------------------------------------------------------------

    [Fact]
    public void A_project_folded_by_hand_is_written_down_and_is_still_folded_after_a_restart()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(), FourAlerts());

            Group(flyout, "acme/beta").ToggleCommand.Execute(null);

            Assert.False(Group(flyout, "acme/beta").IsExpanded);
            Assert.True(shell.SavedFolds!["acme/beta"]);
            Assert.False(shell.SavedFolds.ContainsKey("acme/alpha"));

            // Back open, and that is remembered too - as a choice, not as an absence.
            Group(flyout, "acme/beta").ToggleCommand.Execute(null);
            Assert.False(shell.SavedFolds!["acme/beta"]);

            // A restart with the folds as saved: beta folded despite having something to show.
            var settings = Settings();
            settings.ProjectFolds["ACME/beta"] = true;
            var restarted = Build(new RecordingShell(), settings, FourAlerts());

            Assert.False(Group(restarted, "acme/beta").IsExpanded);
            Assert.True(Group(restarted, "acme/alpha").IsExpanded);
        });
    }

    [Fact]
    public void Folding_everything_writes_the_folds_down_once()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(), FourAlerts());
            shell.Forget();

            flyout.CollapseAllCommand.Execute(null);

            Assert.Equal(1, shell.Saves);
            Assert.All(shell.SavedFolds!.Values, folded => Assert.True(folded));
            Assert.Equal(4, shell.SavedFolds!.Count);
        });
    }

    [Fact]
    public void The_folds_survive_a_save_and_a_load_without_regard_to_case_and_a_null_or_blank_entry_is_dropped()
    {
        var path = NewFile();
        var store = new SettingsStore(path);

        var settings = new AppSettings();
        settings.ProjectFolds["acme/api"] = true;
        settings.ProjectFolds["acme/web"] = false;
        store.Save(settings);

        var loaded = store.Load();
        Assert.True(loaded.ProjectFolds["ACME/API"]);
        Assert.False(loaded.ProjectFolds["acme/web"]);

        var clone = loaded.Clone();
        clone.ProjectFolds["acme/other"] = true;
        Assert.False(loaded.ProjectFolds.ContainsKey("acme/other"));

        File.WriteAllText(path, """{"projectFolds":null}""");
        Assert.Empty(store.Load().ProjectFolds);

        File.WriteAllText(path, """{"projectFolds":{"":true," ":false,"acme/api":true,"ACME/api":false}}""");
        var tidied = store.Load();
        Assert.Single(tidied.ProjectFolds);
        Assert.True(tidied.ProjectFolds["acme/api"]);
    }

    // ---- Plumbing ----------------------------------------------------------

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

    private static ProjectSectionViewModel Section(FlyoutViewModel flyout, string name) =>
        flyout.Rows.OfType<ProjectSectionViewModel>().Single(s => s.Name == name);

    private static ProjectSection Section(string name, params string[] repositories) =>
        new() { Name = name, Repositories = [.. repositories] };

    private static AppSettings Settings(params ProjectSection[] sections) =>
        new() { ProjectOrder = ["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Sections = [.. sections] };

    private static Alert[] FourAlerts() =>
        [Alert("1", "acme/alpha"), Alert("2", "acme/beta"), Alert("3", "acme/gamma"), Alert("4", "acme/delta")];

    private static Alert Alert(string id, string repository) => new()
    {
        Id = $"account|event:{id}",
        Kind = AlertKind.Issue,
        Title = $"Alert {id}",
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
    };

    private FlyoutViewModel Build(IShellCommands shell, AppSettings settings, params Alert[] alerts)
    {
        var store = new AlertStore(NewFile());
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        return new FlyoutViewModel(store, monitor, shell, settings);
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-folds-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <summary>A shell that remembers what the list last asked it to save, and how often.</summary>
    private sealed class RecordingShell : IShellCommands
    {
        public Dictionary<string, bool>? SavedFolds { get; private set; }

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

        public void SaveListPreferences(ListPreferences preferences)
        {
            Saves++;
            SavedFolds = new Dictionary<string, bool>(preferences.ProjectFolds, StringComparer.OrdinalIgnoreCase);
        }

        public void Forget()
        {
            Saves = 0;
            SavedFolds = null;
        }

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
