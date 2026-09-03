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
    }

    public string Id { get; }

    public ObservableCollection<RepoItemViewModel> Repositories { get; } = [];

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

    public void Dispose() => _client.Dispose();
}
