using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
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
    void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens);

    void ResetMonitorState();

    void ClearHistory();

    /// <summary>
    /// Closes the settings window. After a save GitAlert comes back into view, so the change is
    /// seen where it shows; after a cancel nothing else moves.
    /// </summary>
    void CloseSettings(bool saved);
}

/// <summary>
/// Backs the settings window: the GitHub accounts and the repositories watched under each of them,
/// what counts as an alert, and how often GitAlert checks. Nothing is persisted until <c>Save</c>,
/// so cancelling is safe.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>The token needs <c>repo</c> for private repositories and <c>notifications</c> for the inbox.</summary>
    public const string TokenUrl =
        "https://github.com/settings/tokens/new?scopes=repo,notifications&description=GitAlert";

    private readonly SettingsStore _settingsStore;
    private readonly ISecretStore _tokenStore;
    private readonly IStartupRegistrar _startup;
    private readonly ISettingsHost _host;
    private readonly AppSettings _settings;

    /// <summary>Shared by every account's validation client, so they pool one set of connections.</summary>
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly GitHubClient _probe;
    private readonly List<string> _removedAccountIds = [];

    [ObservableProperty]
    private bool _isAddingAccount;

    [ObservableProperty]
    private string _newAccountToken = string.Empty;

    [ObservableProperty]
    private int _pollIntervalMinutes = 2;

    [ObservableProperty]
    private bool _watchWorkflowRuns = true;

    [ObservableProperty]
    private bool _onlyFailedWorkflowRuns;

    [ObservableProperty]
    private bool _ignoreOwnActivity;

    [ObservableProperty]
    private bool _showToasts = true;

    [ObservableProperty]
    private bool _playSound = true;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private DarkPalette _darkPalette = DarkPalette.VsCode;

    [ObservableProperty]
    private int _maxHistory = 300;

    [ObservableProperty]
    private bool _autoHideWindow;

    [ObservableProperty]
    private bool _alwaysOnTop;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isMessageError;

    public SettingsViewModel(
        SettingsStore settingsStore,
        ISecretStore tokenStore,
        ISettingsHost host,
        IStartupRegistrar startup)
    {
        _settingsStore = settingsStore;
        _tokenStore = tokenStore;
        _host = host;
        _startup = startup;

        _settings = settingsStore.Load();
        SettingsMigration.Apply(_settings, tokenStore);

        _probe = new GitHubClient(_http);

        _pollIntervalMinutes = _settings.PollIntervalMinutes;
        _watchWorkflowRuns = _settings.WatchWorkflowRuns;
        _onlyFailedWorkflowRuns = _settings.OnlyFailedWorkflowRuns;
        _ignoreOwnActivity = _settings.IgnoreOwnActivity;
        _showToasts = _settings.ShowToasts;
        _playSound = _settings.PlaySound;
        _startWithWindows = startup.IsEnabled;
        _theme = _settings.Theme;
        _darkPalette = _settings.DarkPalette;
        _maxHistory = _settings.MaxHistory;
        _autoHideWindow = _settings.AutoHideWindow;
        _alwaysOnTop = _settings.AlwaysOnTop;

        foreach (var account in _settings.Accounts)
        {
            var viewModel = new AccountViewModel(account, tokenStore.Read(account.Id), _http, RemoveAccount);

            foreach (var repository in _settings.RepositoriesFor(account.Id))
            {
                viewModel.Repositories.Add(new RepoItemViewModel(repository));
            }

            Accounts.Add(viewModel);
        }

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

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public ObservableCollection<KindToggleViewModel> Kinds { get; }

    public IReadOnlyList<int> PollIntervalOptions { get; } = [1, 2, 5, 10, 15, 30, 60];

    public IReadOnlyList<int> HistoryOptions { get; } = [100, 200, 300, 500, 1000];

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = [AppTheme.System, AppTheme.Dark, AppTheme.Light];

    public IReadOnlyList<DarkPalette> DarkPaletteOptions { get; } = [DarkPalette.VsCode, DarkPalette.GitHub];

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasNoAccounts => Accounts.Count == 0;

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string DataDirectory => AppPaths.DataDirectory;

    [RelayCommand]
    private void BeginAddAccount()
    {
        NewAccountToken = string.Empty;
        IsAddingAccount = true;
        Message = string.Empty;
    }

    [RelayCommand]
    private void CancelAddAccount()
    {
        NewAccountToken = string.Empty;
        IsAddingAccount = false;
    }

    /// <summary>Validates the pasted token, then adds the account it belongs to.</summary>
    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAccountToken))
        {
            Report("Paste a personal access token first.", isError: true);
            return;
        }

        IsBusy = true;

        try
        {
            _probe.SetToken(NewAccountToken);
            var user = await _probe.GetAuthenticatedUserAsync().ConfigureAwait(true);

            if (Accounts.Any(a => string.Equals(a.Login, user.Login, StringComparison.OrdinalIgnoreCase)))
            {
                Report($"@{user.Login} is already added. Use Replace token to update its token.", isError: true);
                return;
            }

            var account = GitHubAccount.Create(user.Login);
            var viewModel = new AccountViewModel(account, NewAccountToken.Trim(), _http, RemoveAccount)
            {
                PendingToken = NewAccountToken.Trim(),
            };

            Accounts.Add(viewModel);
            OnPropertyChanged(nameof(HasNoAccounts));

            NewAccountToken = string.Empty;
            IsAddingAccount = false;
            Report($"Added @{user.Login}. Now add the repositories you want to watch.", isError: false);
        }
        catch (GitHubException ex)
        {
            Report(ex.UserMessage, isError: true);
        }
        finally
        {
            // The probe exists to answer one question. Holding the token after that only widens
            // where a credential lives for no benefit.
            _probe.SetToken(null);
            IsBusy = false;
        }
    }

    /// <summary>
    /// The monitor learned an account's login while this window was open. The window loaded its
    /// own copy of the settings before that, so without this its Save wrote the empty login it
    /// had straight back over the one just learned, and the card said "Unverified account"
    /// until the next restart.
    /// </summary>
    public void ApplyResolvedLogin(string accountId, string login)
    {
        if (_settings.FindAccount(accountId) is { } stored)
        {
            stored.Login = login;
        }

        if (Accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal)) is { } shown
            && string.IsNullOrWhiteSpace(shown.Login))
        {
            shown.Login = login;
        }
    }

    private void RemoveAccount(AccountViewModel account)
    {
        if (!Accounts.Remove(account))
        {
            return;
        }

        // The token file is only deleted on save, so cancelling leaves everything untouched.
        _removedAccountIds.Add(account.Id);
        account.Dispose();

        OnPropertyChanged(nameof(HasNoAccounts));
        Report($"Removed {account.DisplayName} and the repositories watched under it.", isError: false);
    }

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
        _settings.WatchWorkflowRuns = WatchWorkflowRuns;
        _settings.OnlyFailedWorkflowRuns = OnlyFailedWorkflowRuns;
        _settings.IgnoreOwnActivity = IgnoreOwnActivity;
        _settings.ShowToasts = ShowToasts;
        _settings.PlaySound = PlaySound;
        _settings.StartWithWindows = StartWithWindows;
        _settings.Theme = Theme;
        _settings.DarkPalette = DarkPalette;
        _settings.MaxHistory = MaxHistory;
        _settings.AutoHideWindow = AutoHideWindow;
        _settings.AlwaysOnTop = AlwaysOnTop;
        _settings.MutedKinds = [.. Kinds.Where(k => !k.IsEnabled).Select(k => k.Kind)];

        _settings.Accounts = [.. Accounts.Select(a => a.ToAccount())];
        _settings.Repositories = [.. Accounts.SelectMany(a => a.ToSubscriptions())];

        if (!_settingsStore.Save(_settings))
        {
            // Nothing else is touched: tokens are only deleted or written for a settings file
            // that names them, and the window stays open so the change is not lost.
            Report(
                $"Could not write settings.json under {AppPaths.DataDirectory}. "
                + "Another program may have the file open; try again in a moment.",
                isError: true);
            return;
        }

        foreach (var id in _removedAccountIds)
        {
            _tokenStore.Delete(id);
        }

        _removedAccountIds.Clear();

        foreach (var account in Accounts.Where(a => !string.IsNullOrWhiteSpace(a.PendingToken)))
        {
            _tokenStore.Write(account.Id, account.PendingToken!);
            account.PendingToken = null;
        }

        _tokenStore.Prune(_settings.Accounts.Select(a => a.Id));

        if (StartWithWindows != _startup.IsEnabled && !_startup.SetEnabled(StartWithWindows))
        {
            Report("Could not change the Windows startup entry.", isError: true);
        }

        _host.ApplySettings(_settings, _tokenStore.ReadAll(_settings.Accounts.Select(a => a.Id)));
        _host.CloseSettings(saved: true);
    }

    [RelayCommand]
    private void Cancel() => _host.CloseSettings(saved: false);

    private void Report(string message, bool isError)
    {
        Message = message;
        IsMessageError = isError;
    }

    public void Dispose()
    {
        foreach (var account in Accounts)
        {
            account.Dispose();
        }

        _probe.Dispose();
        _http.Dispose();
    }
}

/// <summary>One "notify me about this" switch.</summary>
public sealed partial class KindToggleViewModel(AlertKind kind, string label, bool isEnabled) : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    public AlertKind Kind { get; } = kind;

    public string Label { get; } = label;
}
