using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitAlert.ViewModels;

/// <summary>
/// The alerts from one repository, shown under a collapsible header. Grouping is what makes a list
/// spanning several projects readable: a busy repository can be folded away without muting it, and
/// the header says how much is waiting inside.
/// </summary>
public sealed partial class AlertGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    public AlertGroupViewModel(string repository, IEnumerable<AlertViewModel> alerts)
    {
        Repository = repository;

        var cut = repository.IndexOf('/');
        Owner = cut > 0 ? repository[..cut] : string.Empty;
        Name = cut > 0 ? repository[(cut + 1)..] : repository;

        foreach (var alert in alerts)
        {
            Alerts.Add(alert);
        }

        UnreadCount = Alerts.Count(a => !a.IsRead);
    }

    public string Repository { get; }

    public string Owner { get; }

    public string Name { get; }

    /// <summary>The owner with its slash, dimmed in front of the name the way GitHub writes it.</summary>
    public string OwnerPrefix => Owner.Length == 0 ? string.Empty : $"{Owner}/";

    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    public int UnreadCount { get; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>What the header shows on the right: unread out of everything in the group.</summary>
    public string CountText => UnreadCount > 0 ? UnreadCount.ToString() : Alerts.Count.ToString();
}
