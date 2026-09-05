using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Platform;

namespace GitAlert.ViewModels;

/// <summary>
/// One GitHub account in the settings window, with the repositories watched under it. Each account
/// carries its own token, so a work account and a personal account can be watched side by side.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    private readonly GitHubClient _client;
    private readonly Action<AccountViewModel> _remove;

    private RepoSortOption _selectedSort = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _login;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _includeInbox;

    [ObservableProperty]
    private string _newRepositoryInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isMessageError;

    [ObservableProperty]
    private bool _isReplacingToken;

    /// <summary>Set when the user supplies a new token; written to the store on save.</summary>
    [ObservableProperty]
    private string? _pendingToken;

    [ObservableProperty]
    private bool _isDiscovering;

    [ObservableProperty]
    private bool _hasDiscovered;

    [ObservableProperty]
    private string _discoverySummary = string.Empty;

    /// <summary>Search box over the discovered list, so five hundred repositories stay usable.</summary>
    [ObservableProperty]
    private string _repositoryFilter = string.Empty;

    /// <summary>Everything the token can reach, before the search box and the sort are applied.</summary>
    private readonly List<DiscoveredRepoViewModel> _discovered = [];

    /// <summary>
    /// Coalesces keystrokes in the search box.
    /// </summary>
    /// <remarks>
    /// The box updates its binding on every character, and rebuilding the view emptied and
    /// refilled a collection of up to five hundred rows each time. That is felt as the box
    /// lagging a letter behind the typing rather than as a slow search.
    /// </remarks>
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(140);

    private readonly UiThread _ui = UiThread.Capture();
    private readonly Timer _filterTimer;

    /// <summary>
    /// Guards the two lists against fighting each other: ticking a box edits the watched list,
    /// and editing the watched list re-ticks the boxes.
    /// </summary>
    private bool _syncingWatchState;

    public AccountViewModel(
        GitHubAccount account,
        string? token,
        HttpClient http,
        Action<AccountViewModel> remove)
    {
        Id = account.Id;
        _login = account.Login;
        _isEnabled = account.Enabled;
        _includeInbox = account.IncludeInbox;
        _remove = remove;

        _client = new GitHubClient(http);
        _client.SetToken(token);

        HasStoredToken = !string.IsNullOrEmpty(token);
        _selectedSort = SortOptions[0];

        // The timer fires on the pool; the rebuild is handed back to the UI thread.
        _filterTimer = new Timer(_ => _ui.Post(ApplyRepositoryView), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string Id { get; }

    /// <summary>
    /// Which order the discovered list is in. Declared as a plain property rather than a
    /// generated one so the change handler can re-apply the view without a second notification.
    /// </summary>
    public RepoSortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetProperty(ref _selectedSort, value))
            {
                ApplyRepositoryView();
            }
        }
    }

    public ObservableCollection<RepoItemViewModel> Repositories { get; } = [];

    /// <summary>The repositories this token can reach, filtered and sorted for display.</summary>
    public ObservableCollection<DiscoveredRepoViewModel> DiscoveredRepositories { get; } = [];

    public IReadOnlyList<RepoSortOption> SortOptions { get; } =
    [
        new(RepoSort.RecentlyPushed, "Recently pushed"),
        new(RepoSort.Name, "Name"),
        new(RepoSort.Owner, "Owner"),
        new(RepoSort.Watched, "Watched first"),
    ];

    public string DisplayName => string.IsNullOrWhiteSpace(Login) ? "Unverified account" : $"@{Login}";

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>False when the token could not be decrypted, which needs the user to re-enter it.</summary>
    public bool HasStoredToken { get; private set; }

    public string RepositorySummary => Repositories.Count switch
    {
        0 => "No repositories yet",
        1 => "1 repository",
        _ => $"{Repositories.Count} repositories",
    };

    partial void OnRepositoryFilterChanged(string value)
    {
        _filterTimer.Change(FilterDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Asks GitHub what this token can see. Nothing is fetched until the user asks, because a
    /// settings window that costs API calls just to open is a settings window nobody opens.
    /// </summary>
    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (!HasStoredToken && string.IsNullOrWhiteSpace(PendingToken))
        {
            Report("Add a token for this account first.", isError: true);
            return;
        }

        IsDiscovering = true;

        try
        {
            var found = await _client.GetMyRepositoriesAsync().ConfigureAwait(true);

            _discovered.Clear();
            _discovered.AddRange(found.Select(r => new DiscoveredRepoViewModel(r, OnWatchToggled)));

            HasDiscovered = true;
            DiscoverySummary = found.Count switch
            {
                0 => "This token can reach no repositories.",
                1 => "1 repository available",
                _ => $"{found.Count} repositories available",
            };

            SyncWatchState();
            ApplyRepositoryView();
            Report(string.Empty, isError: false);
        }
        catch (GitHubException ex)
        {
            Report(ex.UserMessage, isError: true);
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>A box was ticked or cleared: start or stop watching that repository.</summary>
    private void OnWatchToggled(DiscoveredRepoViewModel repository, bool watched)
    {
        if (_syncingWatchState)
        {
            return;
        }

        var existing = Repositories.FirstOrDefault(
            r => string.Equals(r.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase));

        if (watched)
        {
            if (existing is null && RepoRef.TryParse(repository.FullName, out var repo))
            {
                // The list came from the token itself, so there is nothing left to verify.
                Repositories.Add(new RepoItemViewModel(repo, repository.IsPrivate));
            }
        }
        else if (existing is not null)
        {
            Repositories.Remove(existing);
        }

        OnPropertyChanged(nameof(RepositorySummary));
    }

    /// <summary>Re-ticks the boxes from the watched list, after it changed some other way.</summary>
    private void SyncWatchState()
    {
        _syncingWatchState = true;

        try
        {
            foreach (var candidate in _discovered)
            {
                candidate.IsWatched = Repositories.Any(
                    r => string.Equals(r.FullName, candidate.FullName, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _syncingWatchState = false;
        }
    }

    private void ApplyRepositoryView()
    {
        var term = RepositoryFilter.Trim();

        IEnumerable<DiscoveredRepoViewModel> view = term.Length == 0
            ? _discovered
            : _discovered.Where(r => r.Matches(term));

        view = SelectedSort.Sort switch
        {
            RepoSort.Name => view.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(r => r.Owner, StringComparer.OrdinalIgnoreCase),
            RepoSort.Owner => view.OrderBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            RepoSort.Watched => view.OrderByDescending(r => r.IsWatched)
                                    .ThenByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue),
            _ => view.OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue),
        };

        DiscoveredRepositories.Clear();

        foreach (var repository in view)
        {
            DiscoveredRepositories.Add(repository);
        }
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        if (!RepoRef.TryParse(NewRepositoryInput, out var repo))
        {
            Report("Paste a GitHub repository link, or type owner/repo.", isError: true);
            return;
        }

        if (Repositories.Any(r => string.Equals(r.FullName, repo.FullName, StringComparison.OrdinalIgnoreCase)))
        {
            Report($"{repo.FullName} is already watched by this account.", isError: true);
            return;
        }

        IsBusy = true;

        try
        {
            // Check this account's token can actually see the repository, so a missing scope
            // surfaces here rather than as a silent failure during polling.
            var details = await _client.GetRepositoryAsync(repo).ConfigureAwait(true);

            Repositories.Add(new RepoItemViewModel(repo, details.IsPrivate));
            NewRepositoryInput = string.Empty;
            OnPropertyChanged(nameof(RepositorySummary));
            SyncWatchState();
            Report($"Watching {repo.FullName}.", isError: false);
        }
        catch (GitHubException ex)
        {
            Report(ex.UserMessage, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RemoveRepository(RepoItemViewModel? repository)
    {
        if (repository is not null && Repositories.Remove(repository))
        {
            OnPropertyChanged(nameof(RepositorySummary));
            SyncWatchState();
        }
    }

    [RelayCommand]
    private static void OpenRepository(RepoItemViewModel? repository) => Browser.Open(repository?.Url);

    [RelayCommand]
    private void BeginReplaceToken()
    {
        PendingToken = string.Empty;
        IsReplacingToken = true;
        Message = string.Empty;
    }

    [RelayCommand]
    private void CancelReplaceToken()
    {
        PendingToken = null;
        IsReplacingToken = false;
    }

    /// <summary>Validates a replacement token and, if it works, keeps it for the save.</summary>
    [RelayCommand]
    private async Task ApplyTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingToken))
        {
            Report("Paste a personal access token first.", isError: true);
            return;
        }

        IsBusy = true;
        var previous = _client.Token;

        try
        {
            _client.SetToken(PendingToken);
            var user = await _client.GetAuthenticatedUserAsync().ConfigureAwait(true);

            Login = user.Login;
            HasStoredToken = true;
            IsReplacingToken = false;
            Report($"Token updated. Signed in as {user.Login}.", isError: false);
        }
        catch (GitHubException ex)
        {
            _client.SetToken(previous);
            Report(ex.UserMessage, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    public GitHubAccount ToAccount() => new()
    {
        Id = Id,
        Login = Login,
        Enabled = IsEnabled,
        IncludeInbox = IncludeInbox,
    };

    public IEnumerable<RepoSubscription> ToSubscriptions() =>
        Repositories.Select(r => r.ToSubscription(Id));

    private void Report(string message, bool isError)
    {
        Message = message;
        IsMessageError = isError;
    }

    public void Dispose()
    {
        _filterTimer.Dispose();
        _client.Dispose();
    }
}
