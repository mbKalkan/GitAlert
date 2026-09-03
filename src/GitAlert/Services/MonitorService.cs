using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.GitHub;

namespace GitAlert.Services;

public enum ConnectionState
{
    /// <summary>No access token, or no repositories to watch yet.</summary>
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
    string? Login = null);

/// <summary>
/// The polling engine. Runs one background loop that walks every watched repository, translates
/// whatever is new into alerts and raises them. All GitHub access is funnelled through here so the
/// UI never has to think about ETags, rate limits or partial failures.
/// </summary>
public sealed class MonitorService : IAsyncDisposable
{
    private readonly GitHubClient _client;
    private readonly AlertStore _alerts;
    private readonly StateStore _stateStore;
    private readonly MonitorState _state;
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private AppSettings _settings = new();
    private string? _token;
    private string? _login;
    private DateTimeOffset? _lastSuccess;

    /// <summary>Honours GitHub's <c>x-poll-interval</c> when it asks us to slow down.</summary>
    private TimeSpan? _serverRequestedInterval;

    public MonitorService(GitHubClient client, AlertStore alerts, StateStore stateStore)
    {
        _client = client;
        _alerts = alerts;
        _stateStore = stateStore;
        _state = stateStore.Load();
    }

    /// <summary>Raised on a background thread whenever new alerts arrive, newest first.</summary>
    public event EventHandler<IReadOnlyList<Alert>>? AlertsReceived;

    /// <summary>Raised on a background thread whenever the connection status changes.</summary>
    public event EventHandler<MonitorStatus>? StatusChanged;

    public MonitorStatus Status { get; private set; } =
        new(ConnectionState.NotConfigured, "Not configured yet.");

    public void Configure(AppSettings settings, string? token)
    {
        var intervalChanged = _settings.PollIntervalMinutes != settings.PollIntervalMinutes;
        var tokenChanged = !string.Equals(_token, token, StringComparison.Ordinal);

        _settings = settings.Clone();
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _alerts.MaxHistory = _settings.MaxHistory;

        if (tokenChanged)
        {
            _login = null;
            _client.SetToken(_token);
        }

        _state.Prune(_settings.Repositories.Select(r => r.FullName));

        if (intervalChanged || tokenChanged)
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
                SetStatus(new MonitorStatus(ConnectionState.Error, ex.Message, _lastSuccess, _client.RateLimit, _login));
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

            if (_token is null)
            {
                SetStatus(new MonitorStatus(ConnectionState.NotConfigured, "Add a personal access token to start."));
                return;
            }

            var watched = settings.Repositories.Where(r => r.Enabled).ToList();

            if (watched.Count == 0 && !settings.IncludeInbox)
            {
                SetStatus(new MonitorStatus(ConnectionState.NotConfigured, "Add a repository to watch."));
                return;
            }

            SetStatus(new MonitorStatus(ConnectionState.Connecting, "Checking GitHub…", _lastSuccess, _client.RateLimit, _login));

            if (_login is null)
            {
                try
                {
                    _login = (await _client.GetAuthenticatedUserAsync(ct).ConfigureAwait(false)).Login;
                }
                catch (GitHubException ex) when (ex.Kind is GitHubErrorKind.Unauthorized or GitHubErrorKind.Forbidden)
                {
                    SetStatus(new MonitorStatus(ConnectionState.Error, ex.UserMessage, _lastSuccess, _client.RateLimit));
                    return;
                }
            }

            var collected = new List<Alert>();
            var failures = new List<(string Repository, GitHubException Error)>();

            foreach (var repository in watched)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await PollRepositoryAsync(repository, settings, collected, ct).ConfigureAwait(false);
                }
                catch (GitHubException ex)
                {
                    failures.Add((repository.FullName, ex));
                }
            }

            if (settings.IncludeInbox)
            {
                try
                {
                    await PollInboxAsync(settings, collected, ct).ConfigureAwait(false);
                }
                catch (GitHubException ex)
                {
                    failures.Add(("inbox", ex));
                }
            }

            _state.LastSuccessfulPoll = DateTimeOffset.Now;
            _stateStore.Save(_state);

            Publish(collected);
            SetStatus(BuildStatus(watched.Count, failures));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollRepositoryAsync(
        RepoSubscription repository,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var state = _state.For(repository.FullName);
        var reference = repository.Ref;

        var events = await _client.GetRepositoryEventsAsync(reference, state.EventsETag, ct).ConfigureAwait(false);

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
                if (alert is not null && ShouldDeliver(alert, settings))
                {
                    collected.Add(alert);
                }
            }

            state.LastEventId = highWater;
            state.Initialised = true;
        }

        if (settings.WatchWorkflowRuns && !settings.IsMuted(AlertKind.Workflow))
        {
            await PollWorkflowRunsAsync(reference, state, settings, collected, ct).ConfigureAwait(false);
        }
    }

    private async Task PollWorkflowRunsAsync(
        RepoRef reference,
        RepoState state,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var runs = await _client.GetWorkflowRunsAsync(reference, state.RunsETag, ct).ConfigureAwait(false);

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

            if (ShouldDeliver(alert, settings))
            {
                collected.Add(alert);
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

    private async Task PollInboxAsync(AppSettings settings, List<Alert> collected, CancellationToken ct)
    {
        var inbox = await _client.GetInboxAsync(_state.InboxETag, ct).ConfigureAwait(false);

        _serverRequestedInterval = inbox.PollInterval ?? _serverRequestedInterval;

        if (inbox.NotModified || inbox.Value is not { } notifications)
        {
            return;
        }

        _state.InboxETag = inbox.ETag;

        var isBaseline = _state.InboxHighWater is null;
        var highWater = _state.InboxHighWater ?? DateTimeOffset.MinValue;

        foreach (var notification in notifications)
        {
            if (notification.UpdatedAt > highWater)
            {
                highWater = notification.UpdatedAt;
            }

            if (isBaseline || notification.UpdatedAt <= _state.InboxHighWater)
            {
                continue;
            }

            var alert = EventTranslator.FromNotification(notification);
            if (ShouldDeliver(alert, settings))
            {
                collected.Add(alert);
            }
        }

        _state.InboxHighWater = highWater == DateTimeOffset.MinValue ? DateTimeOffset.Now : highWater;
    }

    private bool ShouldDeliver(Alert alert, AppSettings settings)
    {
        if (settings.IsMuted(alert.Kind))
        {
            return false;
        }

        return !settings.IgnoreOwnActivity
            || alert.Actor is null
            || _login is null
            || !string.Equals(alert.Actor, _login, StringComparison.OrdinalIgnoreCase);
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

    private MonitorStatus BuildStatus(int watchedCount, List<(string Repository, GitHubException Error)> failures)
    {
        _lastSuccess = failures.Count == 0 ? DateTimeOffset.Now : _lastSuccess;

        if (failures.Count == 0)
        {
            var message = watchedCount switch
            {
                0 => "Watching your inbox",
                1 => "Watching 1 repository",
                _ => $"Watching {watchedCount} repositories",
            };

            return new MonitorStatus(ConnectionState.Connected, message, _lastSuccess, _client.RateLimit, _login);
        }

        var fatal = failures.FirstOrDefault(f =>
            f.Error.Kind is GitHubErrorKind.Unauthorized or GitHubErrorKind.RateLimited or GitHubErrorKind.Network);

        if (fatal.Error is not null)
        {
            return new MonitorStatus(ConnectionState.Error, fatal.Error.UserMessage, _lastSuccess, _client.RateLimit, _login);
        }

        var summary = failures.Count == 1
            ? failures[0].Error.UserMessage
            : $"{failures.Count} repositories could not be checked.";

        return new MonitorStatus(ConnectionState.Warning, summary, _lastSuccess, _client.RateLimit, _login);
    }

    private void SetStatus(MonitorStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Forgets every high-water mark, so the next poll re-baselines from scratch.</summary>
    public void ResetState()
    {
        _state.Repositories.Clear();
        _state.InboxETag = null;
        _state.InboxHighWater = null;
        _stateStore.Save(_state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

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

        _stateStore.Save(_state);
        _cts.Dispose();
        _refreshSignal.Dispose();
        _pollGate.Dispose();
    }
}
