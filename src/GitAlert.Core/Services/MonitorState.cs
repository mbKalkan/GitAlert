using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitAlert.Core;

namespace GitAlert.Services;

/// <summary>
/// The bookkeeping that makes polling cheap and quiet: one ETag per resource so unchanged
/// responses come back as 304, and one high-water mark per repository so an event is never
/// announced twice - not even after a restart.
/// </summary>
public sealed class MonitorState
{
    /// <summary>Keyed by <see cref="Configuration.RepoSubscription.StateKey"/>: account plus repository.</summary>
    public Dictionary<string, RepoState> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keyed by account id - each account has its own notification inbox.</summary>
    public Dictionary<string, InboxState> Inboxes { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset? LastSuccessfulPoll { get; set; }

    /// <summary>
    /// Held while either dictionary is read or written. The poll loop runs on a background
    /// thread while saving settings, resetting the sync state and serialising this file all
    /// happen on the UI thread, and a plain Dictionary written from one thread while another
    /// reads it does not merely lose an entry: it can spin forever inside a lookup.
    /// </summary>
    [JsonIgnore]
    public object SyncRoot { get; } = new();

    public RepoState For(string stateKey)
    {
        lock (SyncRoot)
        {
            if (!Repositories.TryGetValue(stateKey, out var state))
            {
                state = new RepoState();
                Repositories[stateKey] = state;
            }

            return state;
        }
    }

    public InboxState InboxFor(string accountId)
    {
        lock (SyncRoot)
        {
            if (!Inboxes.TryGetValue(accountId, out var state))
            {
                state = new InboxState();
                Inboxes[accountId] = state;
            }

            return state;
        }
    }

    /// <summary>Forgets every high-water mark, so the next poll re-baselines from scratch.</summary>
    public void Reset()
    {
        lock (SyncRoot)
        {
            Repositories.Clear();
            Inboxes.Clear();
        }
    }

    /// <summary>
    /// Repairs what a load left behind.
    /// </summary>
    /// <remarks>
    /// Two things. A hand-edited or truncated file can leave either dictionary, or any entry in
    /// one, as JSON null, and the poll loop dereferences them without a check - an exception
    /// there is a poll that never completes rather than a duplicate alert. And the deserialiser
    /// builds a fresh dictionary with the default comparer, not the case-insensitive one the
    /// property was declared with, so after a restart a repository re-added as <c>Acme/API</c>
    /// no longer found the state written for <c>acme/api</c>.
    /// </remarks>
    public void Normalise()
    {
        lock (SyncRoot)
        {
            Repositories = Rebuild(Repositories, StringComparer.OrdinalIgnoreCase);
            Inboxes = Rebuild(Inboxes, StringComparer.Ordinal);

            foreach (var repository in Repositories.Values)
            {
                repository.PendingWorkflowRunIds ??= [];
            }
        }
    }

    private static Dictionary<string, T> Rebuild<T>(Dictionary<string, T>? loaded, StringComparer comparer)
        where T : class
    {
        var rebuilt = new Dictionary<string, T>(comparer);

        if (loaded is null)
        {
            return rebuilt;
        }

        foreach (var (key, value) in loaded)
        {
            // Two keys that differ only by case collapse to the first; TryAdd rather than the
            // constructor so that is a lost high-water mark, not a failed load.
            if (key is not null && value is not null)
            {
                rebuilt.TryAdd(key, value);
            }
        }

        return rebuilt;
    }

    /// <summary>Drops bookkeeping for repositories the user has since removed.</summary>
    public void Prune(IEnumerable<string> keepRepositories, IEnumerable<string> keepAccounts)
    {
        var liveRepositories = new HashSet<string>(keepRepositories, StringComparer.OrdinalIgnoreCase);
        var liveAccounts = new HashSet<string>(keepAccounts, StringComparer.Ordinal);

        lock (SyncRoot)
        {
            foreach (var stale in Repositories.Keys.Where(k => !liveRepositories.Contains(k)).ToList())
            {
                Repositories.Remove(stale);
            }

            foreach (var stale in Inboxes.Keys.Where(k => !liveAccounts.Contains(k)).ToList())
            {
                Inboxes.Remove(stale);
            }
        }
    }
}

/// <summary>Per-account inbox bookkeeping.</summary>
public sealed class InboxState
{
    public string? ETag { get; set; }

    /// <summary>Null until the first poll, which only records a baseline.</summary>
    public DateTimeOffset? HighWater { get; set; }
}

public sealed class RepoState
{
    public string? EventsETag { get; set; }

    /// <summary>Numeric GitHub event id; everything at or below this has been seen.</summary>
    public long LastEventId { get; set; }

    /// <summary>Learned once from the repository, so commit alerts can name the branch.</summary>
    public string? DefaultBranch { get; set; }

    public string? CommitsETag { get; set; }

    /// <summary>Newest commit already reported on the default branch; empty until the first poll.</summary>
    public string? LastCommitSha { get; set; }

    /// <summary>
    /// When <see cref="LastCommitSha"/> was committed. Tells old commits from new on a page that
    /// no longer has that commit on it.
    /// </summary>
    public DateTimeOffset? LastCommitDate { get; set; }

    public string? RunsETag { get; set; }

    public long LastWorkflowRunId { get; set; }

    /// <summary>
    /// Runs seen while still going, to be announced when they finish. Kept by id so a run that
    /// waits for an approval does not hold the mark, and every run after it, back with it.
    /// </summary>
    public List<long> PendingWorkflowRunIds { get; set; } = [];

    /// <summary>
    /// False until the first successful poll. The first poll only records a baseline so the user
    /// is not buried under weeks of history the moment they add a repository.
    /// </summary>
    public bool Initialised { get; set; }
}

/// <summary>Reads and writes <see cref="MonitorState"/> atomically.</summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public StateStore(string? path = null) => _path = path ?? AppPaths.StateFile;

    public MonitorState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new MonitorState();
            }

            try
            {
                var state = JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(_path), Options) ?? new MonitorState();
                state.Normalise();
                return state;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new MonitorState();
            }
        }
    }

    public void Save(MonitorState state)
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();

                // Serialising walks both dictionaries, which the poll loop may be adding to.
                string json;

                lock (state.SyncRoot)
                {
                    json = JsonSerializer.Serialize(state, Options);
                }

                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (IOException)
            {
                // Losing the state file costs one duplicate notification, not correctness.
            }
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
