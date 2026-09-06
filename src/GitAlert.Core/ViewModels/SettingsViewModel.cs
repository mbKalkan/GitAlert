using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Platform;
using GitAlert.Services;

namespace GitAlert.ViewModels;

/// <summary>What the settings window needs from the application shell.</summary>
public interface ISettingsHost
{
    /// <summary>
    /// Takes the saved settings into use. With <paramref name="listReplaced"/>, an import has
    /// replaced the shape of the list too - the order, the sections, the folds - and the list
    /// has to take it from the settings rather than keep what it had.
    /// </summary>
    void ApplySettings(AppSettings settings, IReadOnlyDictionary<string, string> tokens, bool listReplaced);

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
    /// <summary>
    /// The token needs <c>repo</c> for private repositories, <c>notifications</c> for the inbox
    /// and <c>read:project</c> for boards.
    /// </summary>
    public const string TokenUrl =
        "https://github.com/settings/tokens/new?scopes=repo,notifications,read:project&description=GitAlert";

    private readonly SettingsStore _settingsStore;
    private readonly ISecretStore _tokenStore;
    private readonly IStartupRegistrar _startup;
    private readonly ISettingsHost _host;
    private readonly UpdateChecker? _updates;
    private readonly UiThread _ui;

    /// <summary>The settings as loaded, or as imported; Save writes into this.</summary>
    private AppSettings _settings;

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
    private bool _onlyStatusChangesOnBoards = true;

    [ObservableProperty]
    private bool _ignoreOwnActivity;

    [ObservableProperty]
    private bool _showToasts = true;

    [ObservableProperty]
    private bool _playSound = true;

    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>Ask GitHub once a day whether a newer GitAlert is out.</summary>
    [ObservableProperty]
    private bool _checkForUpdates = true;

    /// <summary>What the About page says about the newest version: checked when, found what.</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

    /// <summary>"2.7.0", once a newer release is known; the button that names it opens the release.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    private string _availableUpdate = string.Empty;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

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
        IStartupRegistrar startup,
        UpdateChecker? updates = null,
        MonitorService? monitor = null)
    {
        _settingsStore = settingsStore;
        _tokenStore = tokenStore;
        _host = host;
        _startup = startup;
        _updates = updates;
        _ui = UiThread.Capture();

        Diagnostics = new DiagnosticsViewModel(monitor);

        _settings = settingsStore.Load();
        SettingsMigration.Apply(_settings, tokenStore);

        _probe = new GitHubClient(_http);

        Kinds =
        [
            new KindToggleViewModel(AlertKind.Push, "Pushes", true),
            new KindToggleViewModel(AlertKind.PullRequest, "Pull requests", true),
            new KindToggleViewModel(AlertKind.Review, "Reviews", true),
            new KindToggleViewModel(AlertKind.Issue, "Issues", true),
            new KindToggleViewModel(AlertKind.Comment, "Comments", true),
            new KindToggleViewModel(AlertKind.Mention, "Mentions", true),
            new KindToggleViewModel(AlertKind.Workflow, "CI runs", true),
            new KindToggleViewModel(AlertKind.Release, "Releases", true),
            new KindToggleViewModel(AlertKind.Branch, "Branches and tags", true),
            new KindToggleViewModel(AlertKind.Star, "Stars", true),
            new KindToggleViewModel(AlertKind.Fork, "Forks", true),
            new KindToggleViewModel(AlertKind.Board, "Board changes", true),
        ];

        Load(_settings);
        StartWithWindows = startup.IsEnabled;

        if (_updates is not null)
        {
            _updates.Changed += OnUpdateChanged;
            RefreshUpdateStatus();
        }
    }

    /// <summary>
    /// Fills the window from a settings object: every switch, and one card per account with its
    /// repositories and boards. Run once from the file when the window opens, and again from an
    /// import, which replaces all of it.
    /// </summary>
    private void Load(AppSettings settings)
    {
        PollIntervalMinutes = settings.PollIntervalMinutes;
        WatchWorkflowRuns = settings.WatchWorkflowRuns;
        OnlyFailedWorkflowRuns = settings.OnlyFailedWorkflowRuns;
        OnlyStatusChangesOnBoards = settings.OnlyStatusChangesOnBoards;
        IgnoreOwnActivity = settings.IgnoreOwnActivity;
        ShowToasts = settings.ShowToasts;
        PlaySound = settings.PlaySound;
        CheckForUpdates = settings.CheckForUpdates;
        Theme = settings.Theme;
        DarkPalette = settings.DarkPalette;
        MaxHistory = settings.MaxHistory;
        AutoHideWindow = settings.AutoHideWindow;
        AlwaysOnTop = settings.AlwaysOnTop;

        foreach (var kind in Kinds)
        {
            kind.IsEnabled = !settings.IsMuted(kind.Kind);
        }

        foreach (var account in Accounts)
        {
            account.Dispose();
        }

        Accounts.Clear();

        foreach (var account in settings.Accounts)
        {
            var viewModel = new AccountViewModel(account, _tokenStore.Read(account.Id), _http, RemoveAccount);

            foreach (var repository in settings.RepositoriesFor(account.Id))
            {
                viewModel.Repositories.Add(new RepoItemViewModel(repository));
            }

            foreach (var board in settings.BoardsFor(account.Id))
            {
                viewModel.Boards.Add(new BoardItemViewModel(board));
            }

            Accounts.Add(viewModel);
        }

        OnPropertyChanged(nameof(HasNoAccounts));
    }

    // ---- Moving the settings between machines ----------------------------------

    /// <summary>True once an import replaced the list's shape too, so Save tells the shell to take it.</summary>
    private bool _imported;

    /// <summary>
    /// The settings as they stand in the window - saved or not - as a file. Everything but the
    /// tokens and where the window stood; what this machine has is what the file says.
    /// </summary>
    public string BuildExport()
    {
        var portable = _settings.Clone();
        Collect(portable);
        return SettingsPortability.Export(portable, Version);
    }

    /// <summary>Writes the export to a file and says so; a file that will not take it says that instead.</summary>
    public void ExportTo(string path)
    {
        try
        {
            var portable = _settings.Clone();
            Collect(portable);
            SettingsPortability.ExportTo(path, portable, Version);
            NoteExported(Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Could not write {Path.GetFileName(path)}: {ex.Message}", isError: true);
        }
    }

    public void NoteExported(string fileName) =>
        Report($"Exported the settings to {fileName}. It holds no token; each account needs its own on the other machine.", isError: false);

    public void NoteExportFailed(string fileName, string reason) =>
        Report($"Could not write {fileName}: {reason}", isError: true);

    public void NoteImportFailed(string fileName, string reason) =>
        Report($"Could not read {fileName}: {reason}", isError: true);

    /// <summary>
    /// Replaces everything in the window with a file's contents. Nothing is written until Save,
    /// so a wrong file is undone by Cancel. An account already here by login keeps its token.
    /// </summary>
    public bool Import(string json, string fileName)
    {
        SettingsImport import;

        try
        {
            import = SettingsPortability.Import(json, _settings);
        }
        catch (SettingsImportException ex)
        {
            Report(ex.Message, isError: true);
            return false;
        }

        _settings = import.Settings;
        _removedAccountIds.Clear();
        _imported = true;

        Load(_settings);

        var needing = Accounts.Count(a => !a.HasStoredToken);
        var tokens = needing switch
        {
            0 => string.Empty,
            1 => " One account needs its token: choose Replace token on its card.",
            _ => $" {needing} accounts need their tokens: choose Replace token on each card.",
        };

        Report(
            $"Read {Count(import.Accounts, "account")}, {Count(import.Repositories, "repository", "repositories")}, "
            + $"{Count(import.Boards, "board")} and {Count(import.Sections, "section")} from {fileName}, written by {import.WrittenBy}. "
            + $"Save to keep them.{tokens}",
            isError: false);

        return true;
    }

    /// <summary>Reads the file and imports it; a file that cannot be read says so.</summary>
    public bool ImportFrom(string path)
    {
        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Could not read {Path.GetFileName(path)}: {ex.Message}", isError: true);
            return false;
        }

        return Import(json, Path.GetFileName(path));
    }

    private static string Count(int number, string singular, string? plural = null) =>
        number == 1 ? $"1 {singular}" : $"{number} {plural ?? singular + "s"}";

    /// <summary>Copies every switch and every card into a settings object; what Save writes and what an export carries.</summary>
    private void Collect(AppSettings target)
    {
        target.PollIntervalMinutes = PollIntervalMinutes;
        target.WatchWorkflowRuns = WatchWorkflowRuns;
        target.OnlyFailedWorkflowRuns = OnlyFailedWorkflowRuns;
        target.OnlyStatusChangesOnBoards = OnlyStatusChangesOnBoards;
        target.IgnoreOwnActivity = IgnoreOwnActivity;
        target.ShowToasts = ShowToasts;
        target.PlaySound = PlaySound;
        target.StartWithWindows = StartWithWindows;
        target.CheckForUpdates = CheckForUpdates;
        target.Theme = Theme;
        target.DarkPalette = DarkPalette;
        target.MaxHistory = MaxHistory;
        target.AutoHideWindow = AutoHideWindow;
        target.AlwaysOnTop = AlwaysOnTop;
        target.MutedKinds = [.. Kinds.Where(k => !k.IsEnabled).Select(k => k.Kind)];

        target.Accounts = [.. Accounts.Select(a => a.ToAccount())];
        target.Repositories = [.. Accounts.SelectMany(a => a.ToSubscriptions())];
        target.Boards = [.. Accounts.SelectMany(a => a.ToBoardSubscriptions())];
    }

    public bool HasAvailableUpdate => AvailableUpdate.Length > 0;

    private void OnUpdateChanged(object? sender, EventArgs e) => _ui.Post(RefreshUpdateStatus);

    /// <summary>
    /// One line for the About page, in order of what matters: a check under way, a newer version,
    /// a check that failed, and failing all of those, when the last one was.
    /// </summary>
    private void RefreshUpdateStatus()
    {
        if (_updates is null)
        {
            return;
        }

        AvailableUpdate = _updates.Available?.Version.ToString(3) ?? string.Empty;

        UpdateStatus = _updates switch
        {
            { IsChecking: true } => "Asking GitHub for the newest release…",
            { Available: { } found } => $"{found.Version.ToString(3)} is out; you have {_updates.Current.ToString(3)}.",
            { LastError: { } error } => $"Could not check: {error}",
            { LastCheckedAt: { } at } => $"You have the newest version. Checked {RelativeTime.Format(at)} ago.",
            { IsEnabled: true } => "Not checked yet.",
            _ => "Checks are off; ask with the button.",
        };
    }

    /// <summary>The button on the About page: ask now, whatever the daily switch says.</summary>
    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        if (_updates is null || IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        RefreshUpdateStatus();

        try
        {
            await _updates.CheckAsync().ConfigureAwait(true);
        }
        finally
        {
            IsCheckingForUpdates = false;
            RefreshUpdateStatus();
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => Browser.Open(_updates?.Available?.Url ?? UpdateChecker.ReleasesUrl);

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public ObservableCollection<KindToggleViewModel> Kinds { get; }

    /// <summary>The Diagnostics page: what the monitor last did, read from it rather than from any copy.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    public IReadOnlyList<int> PollIntervalOptions { get; } = [1, 2, 5, 10, 15, 30, 60];

    public IReadOnlyList<int> HistoryOptions { get; } = [100, 200, 300, 500, 1000];

    public IReadOnlyList<AppTheme> ThemeOptions { get; } = [AppTheme.System, AppTheme.Dark, AppTheme.Light];

    public IReadOnlyList<DarkPalette> DarkPaletteOptions { get; } = [DarkPalette.VsCode, DarkPalette.GitHub];

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasNoAccounts => Accounts.Count == 0;

    /// <summary>Where this platform keeps the tokens, in the store's own words.</summary>
    public string TokenStorageNote => _tokenStore.StorageNote;

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
        Collect(_settings);

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

        var startupRefused = StartWithWindows != _startup.IsEnabled && !_startup.SetEnabled(StartWithWindows);

        _host.ApplySettings(_settings, _tokenStore.ReadAll(_settings.Accounts.Select(a => a.Id)), listReplaced: _imported);
        _imported = false;

        // Everything else is saved and applied; the window stays open only so the refusal can be
        // read. Closing it would have taken the message away before it was ever drawn.
        if (startupRefused)
        {
            Report("Could not change the Windows startup entry.", isError: true);
            return;
        }

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
        if (_updates is not null)
        {
            _updates.Changed -= OnUpdateChanged;
        }

        Diagnostics.Dispose();

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
