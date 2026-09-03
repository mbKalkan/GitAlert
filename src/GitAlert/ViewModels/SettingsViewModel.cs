using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Platform;

namespace GitAlert.ViewModels;

/// <summary>What the settings window needs from the application shell.</summary>
public interface ISettingsHost
{
    void ApplySettings(AppSettings settings, string? token);

    void ResetMonitorState();

    void ClearHistory();

    void CloseSettings();
}

/// <summary>
/// Backs the settings window: the access token, the watched repositories, what counts as an alert
/// and how often GitAlert checks. Nothing is persisted until <c>Save</c>, so cancelling is safe.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>The token needs <c>repo</c> for private repositories and <c>notifications</c> for the inbox.</summary>
    public const string TokenUrl =
        "https://github.com/settings/tokens/new?scopes=repo,notifications&description=GitAlert";

    private readonly SettingsStore _settingsStore;
    private readonly SecureTokenStore _tokenStore;
    private readonly ISettingsHost _host;
    private readonly GitHubClient _client = new();
    private readonly AppSettings _settings;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private string _newRepositoryInput = string.Empty;

    [ObservableProperty]
    private int _pollIntervalMinutes = 2;

    [ObservableProperty]
    private bool _includeInbox = true;

    [ObservableProperty]
    private bool _watchWorkflowRuns = true;

    [ObservableProperty]
    private bool _onlyFailedWorkflowRuns;

    [ObservableProperty]
    private bool _ignoreOwnActivity = true;

    [ObservableProperty]
    private bool _showToasts = true;

    [ObservableProperty]
    private bool _playSound = true;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private int _maxHistory = 300;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isMessageError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn))]
    private string? _signedInAs;

    public SettingsViewModel(SettingsStore settingsStore, SecureTokenStore tokenStore, ISettingsHost host)
    {
        _settingsStore = settingsStore;
        _tokenStore = tokenStore;
        _host = host;

        _settings = settingsStore.Load();

        _token = tokenStore.Read() ?? string.Empty;
        _pollIntervalMinutes = _settings.PollIntervalMinutes;
        _includeInbox = _settings.IncludeInbox;
        _watchWorkflowRuns = _settings.WatchWorkflowRuns;
        _onlyFailedWorkflowRuns = _settings.OnlyFailedWorkflowRuns;
        _ignoreOwnActivity = _settings.IgnoreOwnActivity;
        _showToasts = _settings.ShowToasts;
        _playSound = _settings.PlaySound;
        _startWithWindows = StartupManager.IsEnabled;
        _theme = _settings.Theme;
        _maxHistory = _settings.MaxHistory;

        Repositories = [.. _settings.Repositories.Select(r => new RepoItemViewModel(r))];

        Kinds =
        [
            new KindToggleViewModel(AlertKind.Push, "Pushes", !_settings.IsMuted(AlertKind.Push)),
            new KindToggleViewModel(AlertKind.PullRequest, "Pull requests", !_settings.IsMuted(AlertKind.PullRequest)),
            new KindToggleViewModel(AlertKind.Review, "Reviews", !_settings.IsMuted(AlertKind.Review)),
            new KindToggleViewModel(AlertKind.Issue, "Issues", !_settings.IsMuted(AlertKind.Issue)),
            new KindToggleViewModel(AlertKind.Comment, "Comments", !_settings.IsMuted(AlertKind.Comment)),
            new KindToggleViewModel(AlertKind.Mention, "Mentions", !_settings.IsMuted(AlertKind.Mention)),
            new KindToggleViewModel(AlertKind.Workflow, "CI runs", !_settings.IsMuted(AlertKind.Workflow)),
            new KindToggleViewModel(AlertKind.Release, "Releases", !_settings.IsMuted(AlertKind.Release)),
            new KindToggleViewModel(AlertKind.Branch, "Branches and tags", !_settings.IsMuted(AlertKind.Branch)),
            new KindToggleViewModel(AlertKind.Star, "Stars", !_settings.IsMuted(AlertKind.Star)),
            new KindToggleViewModel(AlertKind.Fork, "Forks", !_settings.IsMuted(AlertKind.Fork)),
        ];
    }

    public ObservableCollection<RepoItemViewModel> Repositories { get; }

    public ObservableCollection<KindToggleViewModel> Kinds { get; }

    public IReadOnlyList<int> PollIntervalOptions { get; } = [1, 2, 5, 10, 15, 30, 60];

    public IReadOnlyList<int> HistoryOptions { get; } = [100, 200, 300, 500, 1000];

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = [AppTheme.System, AppTheme.Dark, AppTheme.Light];

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool IsSignedIn => !string.IsNullOrEmpty(SignedInAs);

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string DataDirectory => AppPaths.DataDirectory;

    [RelayCommand]
    private async Task ValidateTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            Report("Paste a personal access token first.", isError: true);
            SignedInAs = null;
            return;
        }

        IsBusy = true;

        try
        {
            _client.SetToken(Token);
            var user = await _client.GetAuthenticatedUserAsync().ConfigureAwait(true);

            SignedInAs = user.Login;
            Report($"Signed in as {user.Login}.", isError: false);
        }
        catch (GitHubException ex)
        {
            SignedInAs = null;
            Report(ex.UserMessage, isError: true);
        }
        finally
        {
            IsBusy = false;
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
            Report($"{repo.FullName} is already on the list.", isError: true);
            return;
        }

        IsBusy = true;

        try
        {
            // Check the token can actually see the repository before adding it, so a typo or a
            // missing scope surfaces here rather than as a silent failure during polling.
            _client.SetToken(Token);
            var details = await _client.GetRepositoryAsync(repo).ConfigureAwait(true);

            Repositories.Add(new RepoItemViewModel(repo, details.IsPrivate));
            NewRepositoryInput = string.Empty;
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
        if (repository is not null)
        {
            Repositories.Remove(repository);
        }
    }

    [RelayCommand]
    private static void OpenRepository(RepoItemViewModel? repository) => Browser.Open(repository?.Url);

    [RelayCommand]
    private static void CreateToken() => Browser.Open(TokenUrl);

    [RelayCommand]
    private void OpenDataFolder()
    {
        AppPaths.EnsureCreated();
        Browser.OpenFolder(AppPaths.DataDirectory);
    }

    [RelayCommand]
    private void ResetState()
    {
        _host.ResetMonitorState();
        Report("Cleared the sync state. The next check starts from now.", isError: false);
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _host.ClearHistory();
        Report("Cleared the alert history.", isError: false);
    }

    [RelayCommand]
    private void Save()
    {
        _settings.PollIntervalMinutes = PollIntervalMinutes;
        _settings.IncludeInbox = IncludeInbox;
        _settings.WatchWorkflowRuns = WatchWorkflowRuns;
        _settings.OnlyFailedWorkflowRuns = OnlyFailedWorkflowRuns;
        _settings.IgnoreOwnActivity = IgnoreOwnActivity;
        _settings.ShowToasts = ShowToasts;
        _settings.PlaySound = PlaySound;
        _settings.StartWithWindows = StartWithWindows;
        _settings.Theme = Theme;
        _settings.MaxHistory = MaxHistory;
        _settings.Repositories = [.. Repositories.Select(r => r.ToSubscription())];
        _settings.MutedKinds = [.. Kinds.Where(k => !k.IsEnabled).Select(k => k.Kind)];

        _settingsStore.Save(_settings);

        var token = Token.Trim();

        if (string.IsNullOrEmpty(token))
        {
            _tokenStore.Clear();
        }
        else
        {
            _tokenStore.Write(token);
        }

        if (StartWithWindows != StartupManager.IsEnabled && !StartupManager.SetEnabled(StartWithWindows))
        {
            Report("Could not change the Windows startup entry.", isError: true);
        }

        _host.ApplySettings(_settings, string.IsNullOrEmpty(token) ? null : token);
        _host.CloseSettings();
    }

    [RelayCommand]
    private void Cancel() => _host.CloseSettings();

    private void Report(string message, bool isError)
    {
        Message = message;
        IsMessageError = isError;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>One "notify me about this" switch.</summary>
public sealed partial class KindToggleViewModel(AlertKind kind, string label, bool isEnabled) : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    public AlertKind Kind { get; } = kind;

    public string Label { get; } = label;
}
