using System.IO;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Sections inside sections. A section shows its own projects, then the sections inside it, each
/// a step further in; it folds with everything under it, counts everything under it, and a
/// section is dragged into another the way a project is. What is saved is the tree.
/// </summary>
public class NestedSectionTests : IDisposable
{
    private readonly List<string> _files = [];

    // ---- Layout ---------------------------------------------------------------

    [Fact]
    public void A_section_shows_its_projects_then_the_sections_inside_it_each_a_step_further_in()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(Work(Inner())), FourAlerts());

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Inner", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Order(flyout));

            Assert.Equal(0, Group(flyout, "acme/alpha").Depth);
            Assert.Equal(1, Group(flyout, "acme/gamma").Depth);
            Assert.Equal(2, Group(flyout, "acme/delta").Depth);
            Assert.True(Group(flyout, "acme/delta").IsInSection);
            Assert.Equal(0, Section(flyout, "Work").Depth);
            Assert.Equal(1, Section(flyout, "Inner").Depth);
            Assert.Same(Section(flyout, "Work"), Section(flyout, "Inner").Parent);

            // A section counts everything under it, the sections inside it included.
            Assert.Equal(2, Section(flyout, "Work").ProjectCount);
            Assert.Equal(2, Section(flyout, "Work").UnreadCount);
            Assert.Equal(1, Section(flyout, "Inner").ProjectCount);
            Assert.Equal(1, Section(flyout, "Inner").UnreadCount);
        });
    }

    [Fact]
    public void Folding_a_section_takes_the_sections_inside_it_off_the_screen_with_their_projects()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Work(Inner())), FourAlerts());

            Section(flyout, "Work").ToggleCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Order(flyout));

            // The inner section is folded away, not forgotten.
            var saved = Assert.Single(shell.SavedSections!);
            Assert.True(saved.IsCollapsed);
            Assert.Equal("Inner", Assert.Single(saved.Sections).Name);

            Section(flyout, "Work").ToggleCommand.Execute(null);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Inner", "acme/delta"], Rows(flyout));

            // Folding the inner one alone keeps the outer one as it was.
            flyout.Rows.OfType<ProjectSectionViewModel>().Single(s => s.Name == "Inner").ToggleCommand.Execute(null);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Inner"], Rows(flyout));
        });
    }

    [Fact]
    public void Collapse_all_folds_the_sections_inside_sections_too_and_expand_all_undoes_it()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(Work(Inner())), FourAlerts());

            // The first press folds the projects; the second the sections, the nested one included.
            flyout.CollapseAllCommand.Execute(null);
            flyout.CollapseAllCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work"], Rows(flyout));
            Assert.False(Section(flyout, "Inner").IsExpanded);

            flyout.ExpandAllCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Inner", "acme/delta"], Rows(flyout));
            Assert.True(Section(flyout, "Inner").IsExpanded);
        });
    }

    [Fact]
    public void While_showing_unread_only_a_section_with_nothing_to_show_takes_the_ones_inside_it_with_it()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(Work(Inner())),
                Alert("1", "acme/alpha"), Alert("3", "acme/gamma", read: true), Alert("4", "acme/delta", read: true));

            flyout.ToggleUnreadOnlyCommand.Execute(null);

            Assert.Equal(["acme/alpha"], Rows(flyout));
        });
    }

    // ---- The arrows -------------------------------------------------------------

    [Fact]
    public void Walking_a_project_down_out_of_a_section_puts_it_first_in_the_section_inside_it()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Work(Inner())), FourAlerts());

            Group(flyout, "acme/gamma").MoveDownCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "#Inner", "acme/gamma", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], shell.SavedOrder);

            var work = Assert.Single(shell.SavedSections!);
            Assert.Empty(work.Repositories);
            Assert.Equal(["acme/delta", "acme/gamma"], Assert.Single(work.Sections).Repositories.Order(StringComparer.Ordinal));

            // Inside the inner section a step is a swap; a step up out of it lands last in the outer one.
            Group(flyout, "acme/delta").MoveUpCommand.Execute(null);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "#Inner", "acme/delta", "acme/gamma"], Rows(flyout));

            Group(flyout, "acme/delta").MoveUpCommand.Execute(null);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/delta", "#Inner", "acme/gamma"], Rows(flyout));
            Assert.Equal(["acme/delta"], Assert.Single(shell.SavedSections!).Repositories);
        });
    }

    [Fact]
    public void The_arrows_move_a_section_among_its_siblings_only()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Work(Inner(), Section("Later"))), FourAlerts());

            var work = Section(flyout, "Work");
            var later = Section(flyout, "Later");

            Assert.False(work.CanMoveUp);
            Assert.False(work.CanMoveDown);
            Assert.True(later.CanMoveUp);
            Assert.False(later.CanMoveDown);

            later.MoveUpCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Later", "#Inner", "acme/delta"], Rows(flyout));
            Assert.Equal(["Later", "Inner"], Assert.Single(shell.SavedSections!).Sections.Select(s => s.Name));
            Assert.False(later.CanMoveUp);
            Assert.True(later.CanMoveDown);
        });
    }

    // ---- Dropping ---------------------------------------------------------------

    [Fact]
    public void Dropping_a_project_just_above_a_nested_header_puts_it_last_in_the_section_around_it()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Work(Inner())), FourAlerts());

            flyout.PlaceProject(Group(flyout, "acme/alpha"), Section(flyout, "Inner"), above: true);

            Assert.Equal(["acme/beta", "#Work", "acme/gamma", "acme/alpha", "#Inner", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/gamma", "acme/alpha"], Assert.Single(shell.SavedSections!).Repositories);

            // Dropped on the nested header itself: first inside it.
            flyout.PlaceProject(Group(flyout, "acme/beta"), Section(flyout, "Inner"), above: false);

            Assert.Equal(["#Work", "acme/gamma", "acme/alpha", "#Inner", "acme/beta", "acme/delta"], Rows(flyout));
            Assert.Equal(
                ["acme/beta", "acme/delta"],
                Assert.Single(Assert.Single(shell.SavedSections!).Sections).Repositories.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void Dropping_a_section_on_another_puts_it_inside_last_unfolds_it_and_saves_the_tree()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var work = Section("Work", "acme/gamma");
            work.IsCollapsed = true;
            var flyout = Build(shell, Settings(work, Section("Personal", "acme/delta")), FourAlerts());

            flyout.NestSection(Section(flyout, "Personal"), Section(flyout, "Work"));

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Personal", "acme/delta"], Rows(flyout));
            Assert.True(Section(flyout, "Work").IsExpanded);
            Assert.Equal(1, Section(flyout, "Personal").Depth);
            Assert.Equal(2, Group(flyout, "acme/delta").Depth);
            Assert.Equal(2, Section(flyout, "Work").ProjectCount);

            var saved = Assert.Single(shell.SavedSections!);
            Assert.Equal("Work", saved.Name);
            Assert.Equal("Personal", Assert.Single(saved.Sections).Name);
            Assert.Equal(["acme/delta"], saved.Sections[0].Repositories);
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], shell.SavedOrder);

            // Into itself, into one inside it, or where it already is: nothing changes and nothing is saved.
            shell.Forget();
            flyout.NestSection(Section(flyout, "Work"), Section(flyout, "Personal"));
            flyout.NestSection(Section(flyout, "Work"), Section(flyout, "Work"));
            flyout.NestSection(Section(flyout, "Personal"), Section(flyout, "Work"));

            Assert.Null(shell.SavedSections);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Personal", "acme/delta"], Rows(flyout));
        });
    }

    [Fact]
    public void Placing_a_section_beside_a_nested_one_puts_it_in_the_same_parent_and_beside_a_top_one_takes_it_out()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Work(Inner()), Section("Personal")), FourAlerts());

            flyout.PlaceSection(Section(flyout, "Personal"), Section(flyout, "Inner"), above: true);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Personal", "#Inner", "acme/delta"], Rows(flyout));
            var work = Assert.Single(shell.SavedSections!);
            Assert.Equal(["Personal", "Inner"], work.Sections.Select(s => s.Name));
            Assert.Equal(1, Section(flyout, "Personal").Depth);

            // Beside the top-level section: out of it, to the top level, with its projects.
            flyout.PlaceSection(Section(flyout, "Inner"), Section(flyout, "Work"), above: false);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "#Personal", "#Inner", "acme/delta"], Rows(flyout));
            Assert.Equal(["Work", "Inner"], shell.SavedSections!.Select(s => s.Name));
            Assert.Equal(["Personal"], shell.SavedSections![0].Sections.Select(s => s.Name));
            Assert.Equal(0, Section(flyout, "Inner").Depth);
            Assert.Equal(1, Group(flyout, "acme/delta").Depth);

            // Beside one of its own: refused, and nothing saved.
            shell.Forget();
            flyout.PlaceSection(Section(flyout, "Work"), Section(flyout, "Personal"), above: true);
            Assert.Null(shell.SavedSections);
        });
    }

    // ---- Making and unmaking ---------------------------------------------------

    [Fact]
    public void Adding_a_section_inside_another_lands_last_in_it_unfolds_it_and_opens_the_name()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var work = Section("Work", "acme/gamma");
            work.IsCollapsed = true;
            var flyout = Build(shell, Settings(work), FourAlerts());

            Section(flyout, "Work").AddChildCommand.Execute(null);

            Assert.True(Section(flyout, "Work").IsExpanded);
            Assert.Equal(["acme/alpha", "acme/beta", "acme/delta", "#Work", "acme/gamma", "#" + ProjectSection.DefaultName], Rows(flyout));

            var born = Section(flyout, ProjectSection.DefaultName);
            Assert.True(born.IsEditing);
            Assert.Equal(1, born.Depth);
            Assert.Equal(ProjectSection.DefaultName, Assert.Single(Assert.Single(shell.SavedSections!).Sections).Name);
        });
    }

    [Fact]
    public void Removing_a_section_lifts_its_projects_and_the_sections_inside_it_up_a_level()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(Work(Inner(Section("Deep")))), FourAlerts());

            Section(flyout, "Inner").RemoveCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "acme/delta", "#Deep"], Rows(flyout));
            var work = Assert.Single(shell.SavedSections!);
            Assert.Equal(["acme/gamma", "acme/delta"], work.Repositories);
            Assert.Equal(["Deep"], work.Sections.Select(s => s.Name));
            Assert.Equal(1, Section(flyout, "Deep").Depth);

            Section(flyout, "Work").RemoveCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta", "#Deep"], Rows(flyout));
            Assert.Equal(["Deep"], shell.SavedSections!.Select(s => s.Name));
            Assert.Equal(0, Group(flyout, "acme/delta").Depth);
        });
    }

    [Fact]
    public void Reading_a_section_reads_the_projects_in_the_sections_inside_it_too()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(Work(Inner())), FourAlerts());

            Section(flyout, "Work").MarkReadCommand.Execute(null);

            Assert.True(Group(flyout, "acme/gamma").Items.All(a => a.IsRead));
            Assert.True(Group(flyout, "acme/delta").Items.All(a => a.IsRead));
            Assert.Equal(0, Section(flyout, "Work").UnreadCount);
            Assert.Equal(0, Section(flyout, "Inner").UnreadCount);
            Assert.Equal(1, Group(flyout, "acme/alpha").UnreadCount);
        });
    }

    // ---- What is saved -----------------------------------------------------------

    [Fact]
    public void Sections_inside_sections_survive_a_save_and_a_load()
    {
        var path = NewFile();
        var store = new SettingsStore(path);

        var settings = new AppSettings
        {
            Sections = [Work(Inner(Section("Deep", "acme/omega")))],
        };

        store.Save(settings);
        var loaded = store.Load();

        var work = Assert.Single(loaded.Sections);
        Assert.Equal(["acme/gamma"], work.Repositories);
        var inner = Assert.Single(work.Sections);
        Assert.Equal(["acme/delta"], inner.Repositories);
        var deep = Assert.Single(inner.Sections);
        Assert.Equal(["acme/omega"], deep.Repositories);

        // A clone is its own tree.
        var clone = loaded.Clone();
        clone.Sections[0].Sections[0].Sections.Clear();
        Assert.Single(loaded.Sections[0].Sections[0].Sections);
    }

    [Fact]
    public void A_project_listed_at_two_depths_stays_where_it_was_read_first_and_a_blank_nested_name_gets_the_placeholder()
    {
        var settings = new AppSettings
        {
            Sections =
            [
                new ProjectSection
                {
                    Name = "Work",
                    Repositories = ["acme/gamma"],
                    Sections = [new ProjectSection { Name = " ", Repositories = ["acme/gamma", "acme/delta"] }, null!],
                },
            ],
        };

        settings.Normalise();

        var work = Assert.Single(settings.Sections);
        var inner = Assert.Single(work.Sections);
        Assert.Equal(ProjectSection.DefaultName, inner.Name);
        Assert.Equal(["acme/delta"], inner.Repositories);
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

    private static List<string> Order(FlyoutViewModel flyout) => [.. flyout.Groups.Select(g => g.Repository)];

    private static ProjectGroupViewModel Group(FlyoutViewModel flyout, string repository) =>
        flyout.Groups.Single(g => g.Repository == repository);

    /// <summary>A section by name, on screen or folded away inside another.</summary>
    private static ProjectSectionViewModel Section(FlyoutViewModel flyout, string name)
    {
        var shown = flyout.Rows.OfType<ProjectSectionViewModel>().SelectMany(s => s.SelfAndDescendants()).Distinct().ToList();
        return shown.Single(s => s.Name == name);
    }

    private static ProjectSection Section(string name, params string[] repositories) =>
        new() { Name = name, Repositories = [.. repositories] };

    /// <summary>Work holds gamma and whatever sections are handed in.</summary>
    private static ProjectSection Work(params ProjectSection[] inside) =>
        new() { Name = "Work", Repositories = ["acme/gamma"], Sections = [.. inside] };

    /// <summary>Inner holds delta and whatever sections are handed in.</summary>
    private static ProjectSection Inner(params ProjectSection[] inside) =>
        new() { Name = "Inner", Repositories = ["acme/delta"], Sections = [.. inside] };

    private static AppSettings Settings(params ProjectSection[] sections) =>
        new() { ProjectOrder = ["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Sections = [.. sections] };

    private static Alert[] FourAlerts() =>
        [Alert("1", "acme/alpha"), Alert("2", "acme/beta"), Alert("3", "acme/gamma"), Alert("4", "acme/delta")];

    private static Alert Alert(string id, string repository, bool read = false) => new()
    {
        Id = $"account|event:{id}",
        Kind = AlertKind.Issue,
        Title = $"Alert {id}",
        Repository = repository,
        Timestamp = DateTimeOffset.UtcNow,
        IsRead = read,
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
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-nested-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    /// <summary>A shell that remembers what the list last asked it to save.</summary>
    private sealed class RecordingShell : IShellCommands
    {
        public List<string>? SavedOrder { get; private set; }

        public List<ProjectSection>? SavedSections { get; private set; }

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
            SavedOrder = [.. preferences.ProjectOrder];
            SavedSections = preferences.Sections.Select(s => s.Clone()).ToList();
        }

        public void Forget()
        {
            SavedOrder = null;
            SavedSections = null;
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
