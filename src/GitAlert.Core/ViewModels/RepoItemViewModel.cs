using CommunityToolkit.Mvvm.ComponentModel;
using GitAlert.Configuration;
using GitAlert.Core;

namespace GitAlert.ViewModels;

/// <summary>A watched repository as shown under its account in the settings list.</summary>
public sealed partial class RepoItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isPrivate;

    public RepoItemViewModel(RepoSubscription subscription)
    {
        Owner = subscription.Owner;
        Name = subscription.Name;
        _isEnabled = subscription.Enabled;
        _isPrivate = subscription.IsPrivate;
    }

    public RepoItemViewModel(RepoRef repo, bool isPrivate)
    {
        Owner = repo.Owner;
        Name = repo.Name;
        _isEnabled = true;
        _isPrivate = isPrivate;
    }

    public string Owner { get; }

    public string Name { get; }

    public string FullName => $"{Owner}/{Name}";

    public string Url => $"https://github.com/{Owner}/{Name}";

    /// <summary>The account id is supplied by the owning account when the settings are saved.</summary>
    public RepoSubscription ToSubscription(string accountId) => new()
    {
        AccountId = accountId,
        Owner = Owner,
        Name = Name,
        Enabled = IsEnabled,
        IsPrivate = IsPrivate,
    };
}
