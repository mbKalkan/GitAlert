using CommunityToolkit.Mvvm.ComponentModel;

namespace GitAlert.ViewModels;

/// <summary>
/// One project chip: a repository to narrow the list down to, or the "all projects" chip that
/// clears the narrowing. Shaped like <see cref="FilterChipViewModel"/> so both share a style.
/// </summary>
public sealed partial class ProjectChipViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private int _count;

    public ProjectChipViewModel(string? repository, string label)
    {
        Repository = repository;
        Label = label;
        Tooltip = repository ?? "Every watched repository";
    }

    /// <summary>Null on the chip that means "do not narrow by project".</summary>
    public string? Repository { get; }

    public string Label { get; }

    public string Tooltip { get; }

    /// <summary>The full slug, shown under the name in the picker.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    public bool HasCount => Count > 0;
}
