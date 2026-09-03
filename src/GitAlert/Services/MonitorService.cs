using System.Net;
using System.Net.Http;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;

namespace GitAlert.Services;

public enum ConnectionState
{
    /// <summary>No account, or nothing to watch yet.</summary>
    NotConfigured,

    Connecting,

    Connected,

    /// <summary>Polling works, but something needs attention (an unreachable repository).</summary>
    Warning,

    Error,
}

public sealed record MonitorStatus(
    ConnectionState State,
    string Message,
    DateTimeOffset? LastSuccess = null,
    RateLimitStatus RateLimit = default,
    int AccountCount = 0);

/// <summary>A login GitAlert learned by using an account's token.</summary>
public sealed record AccountIdentity(string AccountId, string Login);

/// <summary>
/// The polling engine. Runs one background loop that walks every account, polls the repositories
/// watched under it with that account's token, and translates whatever is new into alerts. All
/// GitHub access is funnelled through here so the UI never has to think about ETags, rate limits,
/// per-account credentials or partial failures.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
    private readonly AlertStore _alerts;
    private readonly StateStore _stateStore;
    private readonly MonitorState _state;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    /// <summary>One client per account, all sharing a single connection pool.</summary>
    private readonly Dictionary<string, GitHubClient> _clients = new(StringComparer.Ordinal);

    /// <summary>Logins resolved from each account's token, used to skip the user's own activity.</summary>
    private readonly Dictionary<string, string> _logins = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private AppSettings _settings = new();
    private Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastSuccess;

    /// <summary>Honours GitHub's <c>x-poll-interval</c> when it asks us to slow down.</summary>
    private TimeSpan? _serverRequestedInterval;

    public MonitorService(AlertStore alerts, StateStore stateStore, HttpClient? http = null)
    {
        _alerts = alerts;
        _stateStore = stateStore;
        _state = stateStore.Load();

        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
    }

    /// <summary>Raised on a background thread whenever new alerts arrive, newest first.</summary>
    public event EventHandler<IReadOnlyList<Alert>>? AlertsReceived;

    /// <summary>Raised on a background thread whenever the connection status changes.</summary>
    public event EventHandler<MonitorStatus>? StatusChanged;

    /// <summary>
    /// Raised the first time an account's login is learned, so the settings file can record it
    /// and the UI can show a name instead of "Unverified account".
    /// </summary>
    public event EventHandler<AccountIdentity>? AccountResolved;

    public MonitorStatus Status { get; private set; } =
        new(ConnectionState.NotConfigured, "Not configured yet.");

    public void Configure(AppSettings settings, IReadOnlyDictionary<string, string> tokens)
    {
        var intervalChanged = _settings.PollIntervalMinutes != settings.PollIntervalMinutes;
        var credentialsChanged = !SameTokens(_tokens, tokens);

        _settings = settings.Clone();
        _tokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
        _alerts.MaxHistory = _settings.MaxHistory;

        SyncClients();

        _state.Prune(
            _settings.Repositories.Select(r => r.StateKey),
            _settings.Accounts.Select(a => a.Id));

        if (intervalChanged || credentialsChanged)
        {
            RequestRefresh();
        }
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Asks the loop to poll immediately instead of waiting out the interval.</summary>
    public void RequestRefresh()
    {
        // A count of 1 is enough: several rapid requests still mean "poll once, now".
        if (_refreshSignal.CurrentCount == 0)
        {
            try
            {
                _refreshSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>Creates and drops per-account clients so the set matches the configured accounts.</summary>
    private void SyncClients()
    {
        foreach (var account in _settings.Accounts)
        {
            if (!_clients.TryGetValue(account.Id, out var client))
            {
                client = new GitHubClient(_http);
                _clients[account.Id] = client;
            }

            var token = _tokens.GetValueOrDefault(account.Id);

            if (!string.Equals(client.Token, token, StringComparison.Ordinal))
            {
                client.SetToken(token);
                _logins.Remove(account.Id);
            }
        }

        var live = _settings.Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var stale in _clients.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _clients[stale].Dispose();
            _clients.Remove(stale);
            _logins.Remove(stale);
        }
    }

    private static bool SameTokens(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus(new MonitorStatus(ConnectionState.Error, ex.Message, _lastSuccess));
            }

            try
            {
                await _refreshSignal.WaitAsync(NextDelay(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan NextDelay()
    {
        var configured = TimeSpan.FromMinutes(Math.Clamp(
            _settings.PollIntervalMinutes,
            AppSettings.MinimumPollMinutes,
            AppSettings.MaximumPollMinutes));

        // Never poll faster than GitHub asked us to.
        return _serverRequestedInterval is { } requested && requested > configured ? requested : configured;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var settings = _settings;

            var accounts = settings.Accounts
                .Where(a => a.Enabled && _tokens.ContainsKey(a.Id))
                .ToList();

            if (accounts.Count == 0)
            {
                SetStatus(new MonitorStatus(
                    ConnectionState.NotConfigured,
                    settings.Accounts.Count == 0
                        ? "Add a GitHub account to start."
                        : "No account has a usable token."));
                return;
            }

            var watched = settings.Repositories.Where(r => r.Enabled).ToList();

            if (watched.Count == 0 && !accounts.Any(a => a.IncludeInbox))
            {
                SetStatus(new MonitorStatus(ConnectionState.NotConfigured, "Add a repository to watch."));
                return;
            }

            SetStatus(new MonitorStatus(ConnectionState.Connecting, "Checking GitHub…", _lastSuccess, AggregateRateLimit(), accounts.Count));

            var collected = new List<Alert>();
            var failures = new List<(string Subject, GitHubException Error)>();

            foreach (var account in accounts)
            {
                ct.ThrowIfCancellationRequested();
                await PollAccountAsync(account, settings, collected, failures, ct).ConfigureAwait(false);
            }

            _state.LastSuccessfulPoll = DateTimeOffset.Now;
            _stateStore.Save(_state);

            Publish(collected);
            SetStatus(BuildStatus(watched.Count, accounts.Count, failures));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollAccountAsync(
        GitHubAccount account,
        AppSettings settings,
        List<Alert> collected,
        List<(string Subject, GitHubException Error)> failures,
        CancellationToken ct)
    {
        if (!_clients.TryGetValue(account.Id, out var client))
        {
            return;
        }

        // The first call also validates the token and tells us who we are.
        if (!_logins.ContainsKey(account.Id))
        {
            try
            {
                var login = (await client.GetAuthenticatedUserAsync(ct).ConfigureAwait(false)).Login;
                _logins[account.Id] = login;

                if (!string.Equals(account.Login, login, StringComparison.OrdinalIgnoreCase))
                {
                    AccountResolved?.Invoke(this, new AccountIdentity(account.Id, login));
                }
            }
            catch (GitHubException ex)
            {
                failures.Add((Describe(account), ex));
                return;
            }
        }

        foreach (var repository in settings.RepositoriesFor(account.Id).Where(r => r.Enabled))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await PollRepositoryAsync(client, account, repository, settings, collected, ct).ConfigureAwait(false);
            }
            catch (GitHubException ex)
            {
                failures.Add((repository.FullName, ex));
            }
        }

        if (account.IncludeInbox)
        {
            try
            {
                await PollInboxAsync(client, account, settings, collected, ct).ConfigureAwait(false);
            }
            catch (GitHubException ex)
            {
                failures.Add(($"{Describe(account)} inbox", ex));
            }
        }
    }

    private async Task PollRepositoryAsync(
        GitHubClient client,
        GitHubAccount account,
        RepoSubscription repository,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var state = _state.For(repository.StateKey);
        var reference = repository.Ref;

        var events = await client.GetRepositoryEventsAsync(reference, state.EventsETag, ct).ConfigureAwait(false);

        if (!events.NotModified && events.Value is { } timeline)
        {
            state.EventsETag = events.ETag;

            var isBaseline = !state.Initialised;
            var highWater = state.LastEventId;

            foreach (var item in timeline)
            {
                if (!long.TryParse(item.Id, out var id))
                {
                    continue;
                }

                highWater = Math.Max(highWater, id);

                if (isBaseline || id <= state.LastEventId)
                {
                    continue;
                }

                var alert = EventTranslator.FromEvent(item);
                if (alert is not null && ShouldDeliver(alert, account, settings))
                {
                    collected.Add(Stamp(alert, account));
                }
            }

            state.LastEventId = highWater;
            state.Initialised = true;
        }

        if (settings.WatchWorkflowRuns && !settings.IsMuted(AlertKind.Workflow))
        {
            await PollWorkflowRunsAsync(client, account, reference, state, settings, collected, ct).ConfigureAwait(false);
        }
    }

    private async Task PollWorkflowRunsAsync(
        GitHubClient client,
        GitHubAccount account,
        RepoRef reference,
        RepoState state,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var runs = await client.GetWorkflowRunsAsync(reference, state.RunsETag, ct).ConfigureAwait(false);

        if (runs.NotModified || runs.Value is not { } page)
        {
            return;
        }

        state.RunsETag = runs.ETag;

        var floor = state.LastWorkflowRunId;
        var isBaseline = floor == 0;
        var highWater = floor;

        // Oldest first, and stop advancing the high-water mark at the first run that is still
        // going: otherwise a run that finishes after a newer one would never be announced.
        foreach (var run in page.WorkflowRuns.OrderBy(r => r.Id))
        {
            if (run.Id <= floor)
            {
                continue;
            }

            var completed = string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase);
            if (!completed)
            {
                break;
            }

            highWater = run.Id;

            if (isBaseline)
            {
                continue;
            }

            var alert = EventTranslator.FromWorkflowRun(run, reference.FullName);

            if (settings.OnlyFailedWorkflowRuns && alert.Severity is AlertSeverity.Success or AlertSeverity.Normal)
            {
                continue;
            }

            if (ShouldDeliver(alert, account, settings))
            {
                collected.Add(Stamp(alert, account));
            }
        }

        if (isBaseline && highWater == 0)
        {
            // No completed runs at all yet; remember that we looked so the next poll does not
            // treat an old run as brand new.
            highWater = page.WorkflowRuns.Count > 0 ? page.WorkflowRuns.Max(r => r.Id) : 1;
        }

        state.LastWorkflowRunId = highWater;
    }

    private async Task PollInboxAsync(
        GitHubClient client,
        GitHubAccount account,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var inbox = _state.InboxFor(account.Id);
        var response = await client.GetInboxAsync(inbox.ETag, ct).ConfigureAwait(false);

        _serverRequestedInterval = response.PollInterval ?? _serverRequestedInterval;

        if (response.NotModified || response.Value is not { } notifications)
        {
            return;
        }

        inbox.ETag = response.ETag;

        var previous = inbox.HighWater;
        var isBaseline = previous is null;
        var highWater = previous ?? DateTimeOffset.MinValue;

        foreach (var notification in notifications)
        {
            if (notification.UpdatedAt > highWater)
            {
                highWater = notification.UpdatedAt;
            }

            if (isBaseline || notification.UpdatedAt <= previous)
            {
                continue;
            }

            var alert = EventTranslator.FromNotification(notification);
            if (ShouldDeliver(alert, account, settings))
            {
                collected.Add(Stamp(alert, account));
            }
        }

        inbox.HighWater = highWater == DateTimeOffset.MinValue ? DateTimeOffset.Now : highWater;
    }

    /// <summary>
    /// Records which account saw the alert, and makes the id unique per account so the same event
    /// watched under two accounts is not silently swallowed by de-duplication.
    /// </summary>
    private Alert Stamp(Alert alert, GitHubAccount account) => new()
    {
        Id = $"{account.Id}|{alert.Id}",
        Kind = alert.Kind,
        Title = alert.Title,
        Detail = alert.Detail,
        Repository = alert.Repository,
        Account = _logins.GetValueOrDefault(account.Id, account.Login),
        Actor = alert.Actor,
        Url = alert.Url,
        Timestamp = alert.Timestamp,
        Severity = alert.Severity,
    };

    private bool ShouldDeliver(Alert alert, GitHubAccount account, AppSettings settings)
    {
        if (settings.IsMuted(alert.Kind))
        {
            return false;
        }

        if (!settings.IgnoreOwnActivity || alert.Actor is null)
        {
            return true;
        }

        return !_logins.TryGetValue(account.Id, out var login)
            || !string.Equals(alert.Actor, login, StringComparison.OrdinalIgnoreCase);
    }

    private void Publish(List<Alert> collected)
    {
        if (collected.Count == 0)
        {
            return;
        }

        var accepted = _alerts.Add(collected);

        if (accepted.Count > 0)
        {
            _alerts.Save();
            AlertsReceived?.Invoke(this, accepted);
        }
    }

    /// <summary>The tightest remaining budget across accounts, which is the one that will bite first.</summary>
    private RateLimitStatus AggregateRateLimit()
    {
        var known = _clients.Values.Select(c => c.RateLimit).Where(r => r.IsKnown).ToList();

        return known.Count == 0
            ? RateLimitStatus.Unknown
            : known.MinBy(r => r.Remaining);
    }

    private MonitorStatus BuildStatus(
        int watchedCount,
        int accountCount,
        List<(string Subject, GitHubException Error)> failures)
    {
        _lastSuccess = failures.Count == 0 ? DateTimeOffset.Now : _lastSuccess;
        var rateLimit = AggregateRateLimit();

        if (failures.Count == 0)
        {
            var repositories = watchedCount switch
            {
                0 => "Watching your inbox",
                1 => "Watching 1 repository",
                _ => $"Watching {watchedCount} repositories",
            };

            var message = accountCount > 1 ? $"{repositories} across {accountCount} accounts" : repositories;

            return new MonitorStatus(ConnectionState.Connected, message, _lastSuccess, rateLimit, accountCount);
        }

        var fatal = failures.FirstOrDefault(f =>
            f.Error.Kind is GitHubErrorKind.Unauthorized or GitHubErrorKind.RateLimited or GitHubErrorKind.Network);

        if (fatal.Error is not null)
        {
            var prefix = accountCount > 1 ? $"{fatal.Subject}: " : string.Empty;
            return new MonitorStatus(ConnectionState.Error, prefix + fatal.Error.UserMessage, _lastSuccess, rateLimit, accountCount);
        }

        var summary = failures.Count == 1
            ? $"{failures[0].Subject}: {failures[0].Error.UserMessage}"
            : $"{failures.Count} of the things being watched could not be checked.";

        return new MonitorStatus(ConnectionState.Warning, summary, _lastSuccess, rateLimit, accountCount);
    }

    private string Describe(GitHubAccount account) =>
        _logins.TryGetValue(account.Id, out var login) ? $"@{login}" : account.DisplayName;

    private void SetStatus(MonitorStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Forgets every high-water mark, so the next poll re-baselines from scratch.</summary>
    public void ResetState()
    {
        _state.Repositories.Clear();
        _state.Inboxes.Clear();
        _stateStore.Save(_state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
        }

        _stateStore.Save(_state);

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        _http.Dispose();
        _refreshSignal.Dispose();
        _pollGate.Dispose();
    }
}
