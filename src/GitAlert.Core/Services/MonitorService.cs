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

/// <summary>A repository being polled, and the account whose token reaches it.</summary>
public sealed record WatchedRepository(string AccountId, string Login, RepoRef Repo)
{
    public string FullName => Repo.FullName;
}

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

    /// <summary>
    /// Held while the configuration, the per-account clients or the resolved logins are touched.
    /// Configure and ClientFor are called from the UI thread while the poll loop is running on
    /// its own, and these are plain dictionaries.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>One client per account, all sharing a single connection pool.</summary>
    private readonly Dictionary<string, GitHubClient> _clients = new(StringComparer.Ordinal);

    /// <summary>Logins resolved from each account's token, used to skip the user's own activity.</summary>
    private readonly Dictionary<string, string> _logins = new(StringComparer.Ordinal);

    /// <summary>
    /// Accounts GitHub has throttled, and when each may be asked again. Guarded by
    /// <see cref="_sync"/>: the poll loop writes it, and a token replaced from the UI clears it.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _backoff = new(StringComparer.Ordinal);

    /// <summary>How long a rate limit without a reset time is waited out.</summary>
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(1);

    /// <summary>The primary budget comes back within the hour; nothing should silence an account longer.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Whether the poll in progress has deserialised anything. Poll thread only.</summary>
    private bool _readSomething;

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
        bool intervalChanged;
        bool credentialsChanged;
        AppSettings applied;

        lock (_sync)
        {
            intervalChanged = _settings.PollIntervalMinutes != settings.PollIntervalMinutes;
            credentialsChanged = !SameTokens(_tokens, tokens);

            _settings = settings.Clone();
            _tokens = new Dictionary<string, string>(tokens, StringComparer.Ordinal);
            applied = _settings;

            SyncClients();
            RefreshWatchedList();
        }

        _alerts.MaxHistory = applied.MaxHistory;

        _state.Prune(
            applied.Repositories.Select(r => r.StateKey),
            applied.Accounts.Select(a => a.Id));

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

        // Configure runs before this and asks for a refresh whenever the credentials changed,
        // which at startup they always have. The loop polls immediately anyway, so that pending
        // request was being consumed by the first wait and everything was polled twice in a row.
        if (_refreshSignal.CurrentCount > 0)
        {
            _refreshSignal.Wait(0);
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

    /// <summary>
    /// Creates and drops per-account clients so the set matches the configured accounts.
    /// Callers hold <see cref="_sync"/>.
    /// </summary>
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

                // A new token is a new budget.
                _backoff.Remove(account.Id);
            }
        }

        var live = _settings.Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var stale in _clients.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _clients[stale].Dispose();
            _clients.Remove(stale);
            _logins.Remove(stale);
            _backoff.Remove(stale);
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

            // Only after a poll that actually read something. With ETags doing their job most
            // polls are a round of 304s that allocate nothing, and a forced full collection every
            // minute for those was a blocking pause in exchange for nothing to hand back.
            if (_readSomething)
            {
                _readSomething = false;
                SettleBeforeIdling();
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

    /// <summary>
    /// Hands back what the poll just finished with, before sitting still for minutes.
    /// </summary>
    /// <remarks>
    /// Inducing a collection is usually a mistake, and this is the case the documentation makes
    /// the exception for: an application about to become idle. A poll deserialises a burst of
    /// JSON and then does nothing for the whole interval. The buffers that burst rents come from
    /// the shared array pools, which only trim on a generation 2 collection - and on an idle
    /// machine with memory to spare, that collection never comes on its own. The pools are
    /// per-core, so the wait is worst on exactly the machines with the most cores to keep.
    ///
    /// It runs on the poll thread with nothing else waiting on it, and it is what keeps a tray
    /// app from sitting in Task Manager at several hundred megabytes it is not using.
    /// </remarks>
    private static void SettleBeforeIdling() =>
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    /// <summary>Passes a response through, noting whether a body came with it.</summary>
    private ConditionalResponse<T> Noted<T>(ConditionalResponse<T> response)
    {
        if (!response.NotModified)
        {
            _readSomething = true;
        }

        return response;
    }

    /// <summary>Keeps the most recent interval GitHub asked for. Poll thread only.</summary>
    private void NoteServerInterval(TimeSpan? requested)
    {
        if (requested is { } interval && interval > TimeSpan.Zero)
        {
            _serverRequestedInterval = interval;
        }
    }

    private TimeSpan NextDelay()
    {
        int minutes;

        lock (_sync)
        {
            minutes = _settings.PollIntervalMinutes;
        }

        var configured = TimeSpan.FromMinutes(Math.Clamp(
            minutes,
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
            // One snapshot for the whole cycle: settings can be replaced from the UI thread part
            // way through, and a poll that changed its mind halfway would report against one
            // configuration what it had gathered under another.
            AppSettings settings;
            List<(GitHubAccount Account, GitHubClient Client)> targets;

            lock (_sync)
            {
                settings = _settings;

                targets =
                [
                    .. settings.Accounts
                        .Where(a => a.Enabled && _tokens.ContainsKey(a.Id))
                        .Select(a => (Account: a, Client: _clients.GetValueOrDefault(a.Id)!))
                        .Where(t => t.Client is not null)
                ];
            }

            var accounts = targets.Select(t => t.Account).ToList();

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

            // Deliberately one repository at a time: GitHub's own guidance for staying clear of
            // the secondary rate limits is to make requests for a single user serially, so the
            // obvious "poll them all at once" would buy latency at the price of being throttled.
            foreach (var (account, client) in targets)
            {
                ct.ThrowIfCancellationRequested();
                await PollAccountAsync(account, client, settings, collected, failures, ct).ConfigureAwait(false);
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
        GitHubClient client,
        AppSettings settings,
        List<Alert> collected,
        List<(string Subject, GitHubException Error)> failures,
        CancellationToken ct)
    {
        // GitHub said no until then. Asking sooner is answered the same way and, once the budget
        // is back, spends the first of it on finding that out. The status still says so.
        if (BackoffFor(account.Id) is { } until)
        {
            failures.Add((Describe(account), Throttled(until)));
            return;
        }

        // The first call also validates the token and tells us who we are.
        if (LoginFor(account.Id) is null)
        {
            try
            {
                var login = (await client.GetAuthenticatedUserAsync(ct).ConfigureAwait(false)).Login;

                lock (_sync)
                {
                    _logins[account.Id] = login;
                }

                if (!string.Equals(account.Login, login, StringComparison.OrdinalIgnoreCase))
                {
                    AccountResolved?.Invoke(this, new AccountIdentity(account.Id, login));
                }
            }
            catch (GitHubException ex)
            {
                failures.Add((Describe(account), ex));
                NoteThrottling(account, ex);
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

                // Every further request with this token is refused the same way, and each one
                // refused counts against the secondary limit that may be the reason.
                if (NoteThrottling(account, ex))
                {
                    return;
                }
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
                NoteThrottling(account, ex);
            }
        }
    }

    /// <summary>When an account is still inside a rate limit, the moment it may be polled again.</summary>
    private DateTimeOffset? BackoffFor(string accountId)
    {
        lock (_sync)
        {
            if (!_backoff.TryGetValue(accountId, out var until))
            {
                return null;
            }

            if (until > DateTimeOffset.UtcNow)
            {
                return until;
            }

            _backoff.Remove(accountId);
            return null;
        }
    }

    /// <summary>
    /// Records a rate limit against the account so it is left alone until GitHub says the
    /// budget is back. Returns true when the rest of this account's cycle should be skipped.
    /// </summary>
    private bool NoteThrottling(GitHubAccount account, GitHubException error)
    {
        if (error.Kind != GitHubErrorKind.RateLimited)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var until = error.RetryAt ?? now + DefaultBackoff;

        if (until <= now)
        {
            // Already reset; nothing to wait for.
            return false;
        }

        if (until > now + MaxBackoff)
        {
            until = now + MaxBackoff;
        }

        lock (_sync)
        {
            _backoff[account.Id] = until;
        }

        return true;
    }

    private static GitHubException Throttled(DateTimeOffset until) =>
        new(GitHubErrorKind.RateLimited, "GitHub rate limit reached.") { RetryAt = until };

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

        var events = Noted(await client.GetRepositoryEventsAsync(reference, state.EventsETag, ct).ConfigureAwait(false));

        // The events endpoint is the one GitHub documents x-poll-interval for; it was only ever
        // read off the inbox, which most accounts do not poll at all.
        NoteServerInterval(events.PollInterval);

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

                if (alert is not null
                    && !IsDefaultBranchEcho(item, state, settings)
                    && ShouldDeliver(alert, account, settings))
                {
                    collected.Add(Stamp(alert, account));
                }
            }

            state.LastEventId = highWater;
            state.Initialised = true;
        }

        // The events timeline is the richer source but GitHub fills it in lazily - for private
        // repositories it can lag by hours or days, and a new repository may have none at all.
        // Polling commits directly is what makes a push show up promptly.
        if (!settings.IsMuted(AlertKind.Push))
        {
            await PollCommitsAsync(client, account, reference, state, settings, collected, ct).ConfigureAwait(false);
        }

        if (settings.WatchWorkflowRuns && !settings.IsMuted(AlertKind.Workflow))
        {
            await PollWorkflowRunsAsync(client, account, reference, state, settings, collected, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a <c>PushEvent</c> is the timeline's copy of a push the commits endpoint covers.
    /// </summary>
    /// <remarks>
    /// Sharing an id by head commit only catches the last push before a poll. Two pushes in one
    /// interval are one alert from the commits endpoint, named after the newer head; the
    /// timeline's copy of the earlier one - hours or days later on a private repository - had a
    /// head nothing had seen, and was announced as fresh news about an old commit. The commits
    /// endpoint is authoritative for the default branch, so while it is being polled the
    /// timeline's pushes to that branch add nothing. Pushes to other branches still come this way.
    /// </remarks>
    private static bool IsDefaultBranchEcho(GhEvent item, RepoState state, AppSettings settings) =>
        !settings.IsMuted(AlertKind.Push)
        && !string.IsNullOrEmpty(state.DefaultBranch)
        && EventTranslator.PushedBranch(item) is { } branch
        && string.Equals(branch, state.DefaultBranch, StringComparison.Ordinal);

    private async Task PollCommitsAsync(
        GitHubClient client,
        GitHubAccount account,
        RepoRef reference,
        RepoState state,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var response = Noted(await client.GetCommitsAsync(reference, state.CommitsETag, ct).ConfigureAwait(false));

        if (response.NotModified || response.Value is not { Count: > 0 } commits)
        {
            return;
        }

        state.CommitsETag = response.ETag;

        // Learn the branch name once so commit alerts read the same as event-derived ones.
        if (state.DefaultBranch is null)
        {
            try
            {
                state.DefaultBranch = (await client.GetRepositoryAsync(reference, ct).ConfigureAwait(false)).DefaultBranch
                    ?? string.Empty;
            }
            catch (GitHubException)
            {
                state.DefaultBranch = string.Empty;
            }
        }

        var previous = state.LastCommitSha;
        var previousDate = state.LastCommitDate;

        state.LastCommitSha = commits[0].Sha;
        state.LastCommitDate = commits[0].Date ?? previousDate;

        // The first poll only records where things stand.
        if (string.IsNullOrEmpty(previous))
        {
            return;
        }

        var fresh = commits
            .TakeWhile(c => !string.Equals(c.Sha, previous, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fresh.Count == commits.Count)
        {
            // The last head is not on the page: a force push, a change of default branch, or
            // more than a page of commits at once. The page is not "everything since" then, it
            // is whatever the branch has now, and most of that is old. Only what was committed
            // after the head last reported is news; when even that cannot be told, the branch
            // is re-baselined rather than the page announced as twenty new commits.
            fresh = previousDate is { } since
                ? fresh.Where(c => c.Date is { } committed && committed > since).ToList()
                : [];
        }

        if (fresh.Count == 0)
        {
            return;
        }

        var alert = EventTranslator.FromCommits(fresh, reference.FullName, state.DefaultBranch, previous);

        if (ShouldDeliver(alert, account, settings))
        {
            collected.Add(Stamp(alert, account));
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
        var runs = Noted(await client.GetWorkflowRunsAsync(reference, state.RunsETag, ct).ConfigureAwait(false));

        if (runs.NotModified || runs.Value is not { } page)
        {
            return;
        }

        state.RunsETag = runs.ETag;

        var floor = state.LastWorkflowRunId;
        var isBaseline = floor == 0;
        var pending = new List<long>(state.PendingWorkflowRunIds);
        var onPage = new HashSet<long>();
        var highWater = floor;

        // Everything on the page moves the mark. Runs finish out of order, so the ones still
        // going are remembered by id and announced when they do finish, rather than by holding
        // the mark back at them - a run waiting days for a deployment approval used to hold
        // every run after it unannounced for as long as it waited.
        foreach (var run in page.WorkflowRuns.OrderBy(r => r.Id))
        {
            onPage.Add(run.Id);
            highWater = Math.Max(highWater, run.Id);

            var awaited = pending.Contains(run.Id);

            if (run.Id <= floor && !awaited)
            {
                continue;
            }

            if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                // One in flight when the repository was added belongs to the baseline, like
                // everything else on that first page.
                if (!isBaseline && !awaited)
                {
                    pending.Add(run.Id);
                }

                continue;
            }

            pending.Remove(run.Id);

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

        // A run that has left the page will not be seen again; its result is whatever it was.
        state.PendingWorkflowRunIds = pending.Where(onPage.Contains).ToList();

        // No runs at all yet: remember that we looked, so the first one is not taken for old.
        state.LastWorkflowRunId = highWater == 0 ? 1 : highWater;
    }

    private async Task PollInboxAsync(
        GitHubClient client,
        GitHubAccount account,
        AppSettings settings,
        List<Alert> collected,
        CancellationToken ct)
    {
        var inbox = _state.InboxFor(account.Id);
        var response = Noted(await client.GetInboxAsync(inbox.ETag, ct).ConfigureAwait(false));

        NoteServerInterval(response.PollInterval);

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
        Account = LoginFor(account.Id) ?? account.Login,
        AccountId = account.Id,
        Actor = alert.Actor,
        Url = alert.Url,
        Timestamp = alert.Timestamp,
        Severity = alert.Severity,
        DiffHead = alert.DiffHead,
        DiffBase = alert.DiffBase,
        PullRequestNumber = alert.PullRequestNumber,
    };

    /// <summary>
    /// The client an account polls with, lent to the detail pane so fetching a diff reuses the
    /// same token and connection pool rather than standing up a second authenticated client.
    /// </summary>
    public GitHubClient? ClientFor(string? accountId)
    {
        if (accountId is null)
        {
            return null;
        }

        lock (_sync)
        {
            return _clients.GetValueOrDefault(accountId);
        }
    }

    /// <summary>The login behind an account's token, once a poll has resolved it.</summary>
    private string? LoginFor(string accountId)
    {
        lock (_sync)
        {
            return _logins.GetValueOrDefault(accountId);
        }
    }

    /// <summary>
    /// The repositories being polled. Browsing history needs the full watch list, not just the
    /// repositories that happen to have produced an alert already.
    /// </summary>
    public IReadOnlyList<WatchedRepository> Watched { get; private set; } = [];

    /// <summary>Callers hold <see cref="_sync"/>.</summary>
    private void RefreshWatchedList() =>
        Watched =
        [
            .. _settings.Repositories
                .Where(r => r.Enabled)
                .Select(r => new WatchedRepository(
                    r.AccountId,
                    _logins.GetValueOrDefault(r.AccountId, _settings.FindAccount(r.AccountId)?.Login ?? string.Empty),
                    new RepoRef(r.Owner, r.Name)))
        ];

    private bool ShouldDeliver(Alert alert, GitHubAccount account, AppSettings settings)
    {
        // A repository with its tick off is not polled, but the inbox can still speak of it.
        if (settings.IsMuted(alert.Kind) || settings.IsSwitchedOff(alert.Repository))
        {
            return false;
        }

        if (!settings.IgnoreOwnActivity || alert.Actor is null)
        {
            return true;
        }

        return LoginFor(account.Id) is not { } login
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
        List<RateLimitStatus> known;

        lock (_sync)
        {
            known = [.. _clients.Values.Select(c => c.RateLimit).Where(r => r.IsKnown)];
        }

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
        LoginFor(account.Id) is { } login ? $"@{login}" : account.DisplayName;

    private void SetStatus(MonitorStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Forgets every high-water mark, so the next poll re-baselines from scratch.</summary>
    public void ResetState()
    {
        _state.Reset();
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

        lock (_sync)
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }

        _http.Dispose();
        _refreshSignal.Dispose();
        _pollGate.Dispose();
    }
}
