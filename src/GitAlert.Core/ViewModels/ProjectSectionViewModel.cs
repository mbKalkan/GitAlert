using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Configuration;

namespace GitAlert.ViewModels;

/// <summary>
/// A named group of projects in the list, folded and unfolded as one. It wraps the section as the
/// settings keep it, so what the user does to it - the name, the fold, which projects are in it -
/// is what gets saved, with no second copy to fall out of step.
/// </summary>
public sealed partial class ProjectSectionViewModel : ObservableObject
{
    /// <summary>Told about anything worth saving: a fold, a name, a membership change.</summary>
    private readonly Action<ProjectSectionViewModel> _changed;

    /// <summary>Told which way the section should move among the others.</summary>
    private readonly Action<ProjectSectionViewModel, int> _move;

    /// <summary>Dissolves the section. Its projects are the list's to place, so the list does it.</summary>
    private readonly Action<ProjectSectionViewModel> _remove;

    /// <summary>Reads everything the section's projects are showing.</summary>
    private readonly Action<ProjectSectionViewModel> _markRead;

    /// <summary>True while the name is being typed over; the header shows a text box instead.</summary>
    [ObservableProperty]
    private bool _isEditing;

    /// <summary>The name as typed so far. Only a committed edit reaches <see cref="Name"/>.</summary>
    [ObservableProperty]
    private string _editedName = string.Empty;

    /// <summary>False for the first and last section, so the arrows can grey out there.</summary>
    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    /// <summary>How many projects the section is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(ShowCount))]
    private int _projectCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    /// <summary>Where a dragged project would land relative to this section, while one hovers over it.</summary>
    [ObservableProperty]
    private DropMarker _dropMarker;

    public ProjectSectionViewModel(
        ProjectSection model,
        Action<ProjectSectionViewModel>? changed = null,
        Action<ProjectSectionViewModel, int>? move = null,
        Action<ProjectSectionViewModel>? remove = null,
        Action<ProjectSectionViewModel>? markRead = null)
    {
        Model = model;
        _changed = changed ?? (_ => { });
        _move = move ?? ((_, _) => { });
        _remove = remove ?? (_ => { });
        _markRead = markRead ?? (_ => { });
    }

    /// <summary>The section as the settings hold it. Membership and fold live here.</summary>
    public ProjectSection Model { get; }

    public string Name
    {
        get => Model.Name;
        private set => SetProperty(Model.Name, value, Model, (m, v) => m.Name = v);
    }

    public bool IsExpanded
    {
        get => !Model.IsCollapsed;
        set => SetProperty(!Model.IsCollapsed, value, Model, (m, v) => m.IsCollapsed = !v);
    }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>Unread if there is any, otherwise how many projects the section holds.</summary>
    public string CountText => UnreadCount > 0 ? UnreadCount.ToString() : ProjectCount.ToString();

    /// <summary>An empty section says nothing; a number there would read as a count of nothing.</summary>
    public bool ShowCount => ProjectCount > 0;

    public bool Contains(string repository) => Model.Contains(repository);

    /// <summary>Puts a project in the section. Where it sits among the others is the list's order.</summary>
    public void Add(string repository)
    {
        if (!Contains(repository))
        {
            Model.Repositories.Add(repository);
        }
    }

    public void Remove(string repository) =>
        Model.Repositories.RemoveAll(r => string.Equals(r, repository, StringComparison.OrdinalIgnoreCase));

    /// <summary>Clicking the header folds the section, or unfolds it.</summary>
    [RelayCommand]
    private void Toggle()
    {
        IsExpanded = !IsExpanded;
        _changed(this);
    }

    /// <summary>Opens the name for typing, with the current one selected so typing replaces it.</summary>
    [RelayCommand]
    public void Rename()
    {
        EditedName = Name;
        IsEditing = true;
    }

    /// <summary>
    /// Keeps what was typed. A blank is not a name: the box closes and the old name stays, which
    /// is also what pressing Enter on an untouched box does.
    /// </summary>
    [RelayCommand]
    private void CommitRename()
    {
        if (!IsEditing)
        {
            return;
        }

        IsEditing = false;

        var name = EditedName.Trim();

        if (name.Length == 0 || string.Equals(name, Name, StringComparison.Ordinal))
        {
            return;
        }

        Name = name;
        _changed(this);
    }

    [RelayCommand]
    private void CancelRename() => IsEditing = false;

    [RelayCommand]
    private void MoveUp() => _move(this, -1);

    [RelayCommand]
    private void MoveDown() => _move(this, 1);

    /// <summary>Takes the section away. Its projects stay in the list, loose, where they were.</summary>
    [RelayCommand]
    private void Remove() => _remove(this);

    /// <summary>The tick on the header: every project in the section read, in one go.</summary>
    [RelayCommand]
    private void MarkRead() => _markRead(this);
}
