using CommunityToolkit.Mvvm.ComponentModel;

namespace GitAlert.ViewModels;

/// <summary>One of the filter chips along the top of the flyout.</summary>
public sealed partial class FilterChipViewModel(AlertFilter filter, string label) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    private int _count;

    public AlertFilter Filter { get; } = filter;

    public string Label { get; } = label;

    public bool HasCount => Count > 0;
}
