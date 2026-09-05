using System.IO;
using System.Net.Http;
using System.Threading;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// Sections: named groups of projects in the list, folded and unfolded as one. The loose projects
/// come first, then each section with its projects; the arrows walk a project across the edges
/// between them, a drop on a section header puts it inside, and everything that changes is saved
/// through the shell in the shape the settings keep it.
/// </summary>
public class ProjectSectionTests : IDisposable
{
    private readonly List<string> _files = [];

    // ---- Layout ---------------------------------------------------------------

    [Fact]
    public void Loose_projects_come_first_then_each_section_with_its_projects_in_the_saved_order()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(order: ["acme/alpha", "acme/gamma", "acme/beta", "acme/delta"],
                Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Order(flyout));
            Assert.False(Group(flyout, "acme/alpha").IsInSection);
            Assert.True(Group(flyout, "acme/gamma").IsInSection);
            Assert.Equal(2, Section(flyout, "Work").ProjectCount);
        });
    }

    [Fact]
    public void A_folded_section_keeps_its_projects_out_of_the_rows_but_not_out_of_the_list()
    {
        StaThread.Run(() =>
        {
            var work = Section("Work", "acme/gamma", "acme/delta");
            work.IsCollapsed = true;

            var flyout = Build(new RecordingShell(), Settings(order: Ordered, work), FourAlerts());

            Assert.Equal(["acme/alpha", "acme/beta", "#Work"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Order(flyout));
            Assert.False(Section(flyout, "Work").IsExpanded);
        });
    }

    [Fact]
    public void Folding_a_section_takes_its_projects_off_the_screen_and_is_saved()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Section(flyout, "Work").ToggleCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work"], Rows(flyout));
            Assert.True(Assert.Single(shell.SavedSections!).IsCollapsed);

            Section(flyout, "Work").ToggleCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "acme/delta"], Rows(flyout));
            Assert.False(Assert.Single(shell.SavedSections!).IsCollapsed);
        });
    }

    [Fact]
    public void Collapse_all_folds_every_project_and_section_and_expand_all_undoes_it()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Assert.All(flyout.Groups, g => Assert.True(g.IsExpanded));

            flyout.CollapseAllCommand.Execute(null);

            Assert.All(flyout.Groups, g => Assert.False(g.IsExpanded));
            Assert.False(Section(flyout, "Work").IsExpanded);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work"], Rows(flyout));
            Assert.True(Assert.Single(shell.SavedSections!).IsCollapsed);

            flyout.ExpandAllCommand.Execute(null);

            Assert.All(flyout.Groups, g => Assert.True(g.IsExpanded));
            Assert.True(Section(flyout, "Work").IsExpanded);
            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "acme/delta"], Rows(flyout));
        });
    }

    // ---- The arrows -------------------------------------------------------------

    [Fact]
    public void Walking_a_project_down_across_a_section_edge_puts_it_first_in_that_section()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Group(flyout, "acme/beta").MoveDownCommand.Execute(null);

            Assert.Equal(["acme/alpha", "#Work", "acme/beta", "acme/gamma", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], shell.SavedOrder);
            Assert.Contains("acme/beta", Assert.Single(shell.SavedSections!).Repositories);

            // Inside the section a step is a swap, as it always was.
            Group(flyout, "acme/beta").MoveDownCommand.Execute(null);

            Assert.Equal(["acme/alpha", "#Work", "acme/gamma", "acme/beta", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/gamma", "acme/beta", "acme/delta"], shell.SavedOrder);
        });
    }

    [Fact]
    public void Walking_a_project_up_out_of_a_section_leaves_it_loose_at_the_end_of_the_loose_ones()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Group(flyout, "acme/gamma").MoveUpCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "#Work", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/delta"], Assert.Single(shell.SavedSections!).Repositories);
        });
    }

    /// <summary>A project walked into a folded section would otherwise simply vanish.</summary>
    [Fact]
    public void Walking_into_a_folded_section_unfolds_it()
    {
        StaThread.Run(() =>
        {
            var work = Section("Work", "acme/gamma", "acme/delta");
            work.IsCollapsed = true;

            var flyout = Build(new RecordingShell(), Settings(order: Ordered, work), FourAlerts());

            Group(flyout, "acme/beta").MoveDownCommand.Execute(null);

            Assert.True(Section(flyout, "Work").IsExpanded);
            Assert.Equal(["acme/alpha", "#Work", "acme/beta", "acme/gamma", "acme/delta"], Rows(flyout));
        });
    }

    [Fact]
    public void The_first_project_of_the_first_section_can_still_walk_out_when_nothing_is_loose()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(order: Ordered, Section("Work", "acme/alpha", "acme/beta")),
                Alert("1", "acme/alpha"), Alert("2", "acme/beta"));

            Assert.Equal(["#Work", "acme/alpha", "acme/beta"], Rows(flyout));
            Assert.True(Group(flyout, "acme/alpha").CanMoveUp);

            Group(flyout, "acme/alpha").MoveUpCommand.Execute(null);

            Assert.Equal(["acme/alpha", "#Work", "acme/beta"], Rows(flyout));
            Assert.False(Group(flyout, "acme/alpha").CanMoveUp);
        });
    }

    [Fact]
    public void An_empty_section_is_a_place_the_arrows_can_reach()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(order: Ordered, Section("Work"), Section("Personal", "acme/gamma")),
                Alert("1", "acme/alpha"), Alert("2", "acme/beta"), Alert("3", "acme/gamma"));

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "#Personal", "acme/gamma"], Rows(flyout));

            Group(flyout, "acme/beta").MoveDownCommand.Execute(null);

            Assert.Equal(["acme/alpha", "#Work", "acme/beta", "#Personal", "acme/gamma"], Rows(flyout));

            Group(flyout, "acme/beta").MoveDownCommand.Execute(null);

            Assert.Equal(["acme/alpha", "#Work", "#Personal", "acme/beta", "acme/gamma"], Rows(flyout));
        });
    }

    [Fact]
    public void The_arrows_grey_out_at_the_very_top_and_the_very_bottom_only()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Assert.False(Group(flyout, "acme/alpha").CanMoveUp);
            Assert.True(Group(flyout, "acme/alpha").CanMoveDown);
            Assert.True(Group(flyout, "acme/beta").CanMoveDown);
            Assert.True(Group(flyout, "acme/gamma").CanMoveUp);
            Assert.False(Group(flyout, "acme/delta").CanMoveDown);
        });
    }

    // ---- Drops -----------------------------------------------------------------

    [Fact]
    public void Dropping_a_project_on_a_section_header_puts_it_first_there_and_unfolds_the_section()
    {
        StaThread.Run(() =>
        {
            var work = Section("Work", "acme/gamma", "acme/delta");
            work.IsCollapsed = true;

            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, work), FourAlerts());

            flyout.PlaceProject(Group(flyout, "acme/alpha"), Section(flyout, "Work"), above: false);

            Assert.Equal(["acme/beta", "#Work", "acme/alpha", "acme/gamma", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/beta", "acme/alpha", "acme/gamma", "acme/delta"], shell.SavedOrder);
            Assert.False(Assert.Single(shell.SavedSections!).IsCollapsed);
        });
    }

    [Fact]
    public void Dropping_a_project_just_above_a_section_header_puts_it_last_before_the_section()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell,
                Settings(order: Ordered, Section("Work", "acme/gamma"), Section("Personal", "acme/delta")), FourAlerts());

            // Above the first section: the end of the loose projects.
            flyout.PlaceProject(Group(flyout, "acme/gamma"), Section(flyout, "Work"), above: true);

            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "#Work", "#Personal", "acme/delta"], Rows(flyout));

            // Above a later section: the end of the one before it.
            flyout.PlaceProject(Group(flyout, "acme/alpha"), Section(flyout, "Personal"), above: true);

            Assert.Equal(["acme/beta", "acme/gamma", "#Work", "acme/alpha", "#Personal", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/beta", "acme/gamma", "acme/alpha", "acme/delta"], shell.SavedOrder);
        });
    }

    [Fact]
    public void Dropping_a_project_below_one_inside_a_section_moves_it_in_beside_it()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            flyout.PlaceProject(Group(flyout, "acme/alpha"), Group(flyout, "acme/gamma"), above: false);

            Assert.Equal(["acme/beta", "#Work", "acme/gamma", "acme/alpha", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/beta", "acme/gamma", "acme/alpha", "acme/delta"], shell.SavedOrder);
        });
    }

    // ---- The sections themselves ------------------------------------------------

    [Fact]
    public void A_new_section_lands_at_the_end_with_its_name_open_for_typing()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma")), Alert("1", "acme/alpha"), Alert("2", "acme/gamma"));

            flyout.AddSectionCommand.Execute(null);

            var added = Assert.IsType<ProjectSectionViewModel>(flyout.Rows[^1]);

            Assert.Equal(ProjectSection.DefaultName, added.Name);
            Assert.True(added.IsEditing);
            Assert.Equal(ProjectSection.DefaultName, added.EditedName);
            Assert.True(added.IsExpanded);
            Assert.Equal(["Work", ProjectSection.DefaultName], shell.SavedSections!.Select(s => s.Name));
        });
    }

    [Fact]
    public void Committing_a_typed_name_keeps_it_while_a_blank_or_a_cancel_keeps_the_old_one()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/alpha")), Alert("1", "acme/alpha"));
            var section = Section(flyout, "Work");

            section.RenameCommand.Execute(null);
            Assert.True(section.IsEditing);
            Assert.Equal("Work", section.EditedName);

            section.EditedName = "  Client work ";
            section.CommitRenameCommand.Execute(null);

            Assert.False(section.IsEditing);
            Assert.Equal("Client work", section.Name);
            Assert.Equal("Client work", Assert.Single(shell.SavedSections!).Name);

            section.RenameCommand.Execute(null);
            section.EditedName = "   ";
            section.CommitRenameCommand.Execute(null);

            Assert.False(section.IsEditing);
            Assert.Equal("Client work", section.Name);

            section.RenameCommand.Execute(null);
            section.EditedName = "Something else";
            section.CancelRenameCommand.Execute(null);

            Assert.False(section.IsEditing);
            Assert.Equal("Client work", section.Name);
        });
    }

    [Fact]
    public void Moving_a_section_carries_its_projects_and_rewrites_the_order()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell,
                Settings(order: Ordered, Section("Work", "acme/gamma"), Section("Personal", "acme/delta")), FourAlerts());

            Assert.False(Section(flyout, "Work").CanMoveUp);
            Assert.True(Section(flyout, "Work").CanMoveDown);
            Assert.False(Section(flyout, "Personal").CanMoveDown);

            Section(flyout, "Personal").MoveUpCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Personal", "acme/delta", "#Work", "acme/gamma"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/delta", "acme/gamma"], shell.SavedOrder);
            Assert.Equal(["Personal", "Work"], shell.SavedSections!.Select(s => s.Name));
        });
    }

    [Fact]
    public void Removing_a_section_leaves_its_projects_loose_after_the_other_loose_ones()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            Section(flyout, "Work").RemoveCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], Rows(flyout));
            Assert.Empty(shell.SavedSections!);
            Assert.All(flyout.Groups, g => Assert.False(g.IsInSection));
        });
    }

    [Fact]
    public void Reading_a_section_reads_every_project_under_it_and_its_badge_follows()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());
            var work = Section(flyout, "Work");

            Assert.Equal(2, work.UnreadCount);
            Assert.True(work.HasUnread);
            Assert.Equal("2", work.CountText);

            work.MarkReadCommand.Execute(null);

            Assert.Equal(0, work.UnreadCount);
            Assert.Equal("2", work.CountText);
            Assert.All(Group(flyout, "acme/gamma").Items, a => Assert.True(a.IsRead));
            Assert.All(Group(flyout, "acme/delta").Items, a => Assert.True(a.IsRead));
            Assert.Contains(Group(flyout, "acme/alpha").Items, a => !a.IsRead);
        });
    }

    [Fact]
    public void While_showing_unread_only_a_section_with_nothing_unread_stays_out_of_the_rows()
    {
        StaThread.Run(() =>
        {
            var flyout = Build(new RecordingShell(), Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")),
                Alert("1", "acme/alpha"), Alert("2", "acme/beta"), Alert("3", "acme/gamma", read: true), Alert("4", "acme/delta", read: true));

            flyout.ToggleUnreadOnlyCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta"], Rows(flyout));

            // Nothing to walk into either: the section is not on screen.
            Assert.False(Group(flyout, "acme/beta").CanMoveDown);

            flyout.ToggleUnreadOnlyCommand.Execute(null);

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/gamma", "acme/delta"], Rows(flyout));
        });
    }

    /// <summary>
    /// A project switched off in settings is out of sight, but it keeps its section and its place
    /// in it for when the tick comes back.
    /// </summary>
    [Fact]
    public void A_switched_off_project_keeps_its_section_while_others_move_around_it()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var (flyout, store) = BuildWithStore(shell, Settings(order: Ordered, Section("Work", "acme/gamma", "acme/delta")), FourAlerts());

            store.Hide(["acme/gamma"]);
            flyout.Reload();

            Assert.Equal(["acme/alpha", "acme/beta", "#Work", "acme/delta"], Rows(flyout));

            Group(flyout, "acme/beta").MoveDownCommand.Execute(null);

            Assert.Equal(["acme/alpha", "#Work", "acme/beta", "acme/delta"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"], shell.SavedOrder);
            Assert.Contains("acme/gamma", Assert.Single(shell.SavedSections!).Repositories);
        });
    }

    [Fact]
    public void Dropping_a_section_on_another_puts_it_above_or_below_with_its_projects()
    {
        StaThread.Run(() =>
        {
            var shell = new RecordingShell();
            var flyout = Build(shell,
                Settings(order: Ordered, Section("Work", "acme/gamma"), Section("Personal", "acme/delta"), Section("Archive")),
                FourAlerts());

            flyout.PlaceSection(Section(flyout, "Archive"), Section(flyout, "Work"), above: true);

            Assert.Equal(["acme/alpha", "acme/beta", "#Archive", "#Work", "acme/gamma", "#Personal", "acme/delta"], Rows(flyout));
            Assert.Equal(["Archive", "Work", "Personal"], shell.SavedSections!.Select(s => s.Name));

            flyout.PlaceSection(Section(flyout, "Work"), Section(flyout, "Personal"), above: false);

            Assert.Equal(["acme/alpha", "acme/beta", "#Archive", "#Personal", "acme/delta", "#Work", "acme/gamma"], Rows(flyout));
            Assert.Equal(["acme/alpha", "acme/beta", "acme/delta", "acme/gamma"], shell.SavedOrder);

            // Dropped where it already is, or on itself: nothing changes and nothing is saved.
            shell.Forget();
            flyout.PlaceSection(Section(flyout, "Personal"), Section(flyout, "Archive"), above: false);
            flyout.PlaceSection(Section(flyout, "Work"), Section(flyout, "Work"), above: true);

            Assert.Null(shell.SavedSections);
            Assert.Equal(["acme/alpha", "acme/beta", "#Archive", "#Personal", "acme/delta", "#Work", "acme/gamma"], Rows(flyout));
        });
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

    private static ProjectSectionViewModel Section(FlyoutViewModel flyout, string name) =>
        flyout.Rows.OfType<ProjectSectionViewModel>().Single(s => s.Name == name);

    /// <summary>
    /// The order the tests start from. Without one the projects would sort alphabetically, and
    /// delta before gamma is not what the sentences below say.
    /// </summary>
    private static List<string> Ordered => ["acme/alpha", "acme/beta", "acme/gamma", "acme/delta"];

    private static ProjectSection Section(string name, params string[] repositories) =>
        new() { Name = name, Repositories = [.. repositories] };

    private static AppSettings Settings(List<string> order, params ProjectSection[] sections) =>
        new() { ProjectOrder = order, Sections = [.. sections] };

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

    private FlyoutViewModel Build(IShellCommands shell, AppSettings settings, params Alert[] alerts) =>
        BuildWithStore(shell, settings, alerts).Flyout;

    private (FlyoutViewModel Flyout, AlertStore Store) BuildWithStore(IShellCommands shell, AppSettings settings, params Alert[] alerts)
    {
        var store = new AlertStore(NewFile());
        store.Add(alerts);

        var monitor = new MonitorService(
            store,
            new StateStore(NewFile()),
            new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))));

        return (new FlyoutViewModel(store, monitor, shell, settings), store);
    }

    private string NewFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitalert-sections-{Guid.NewGuid():N}.json");
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

        /// <summary>Drops what was saved so far, so the next assertion sees only what follows.</summary>
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
