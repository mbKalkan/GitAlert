using System.IO;
using System.Text.Json;
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

    public RepoState For(string stateKey)
    {
        if (!Repositories.TryGetValue(stateKey, out var state))
        {
            state = new RepoState();
            Repositories[stateKey] = state;
        }

        return state;
    }

    public InboxState InboxFor(string accountId)
    {
        if (!Inboxes.TryGetValue(accountId, out var state))
        {
            state = new InboxState();
            Inboxes[accountId] = state;
        }

        return state;
    }

    /// <summary>Drops bookkeeping for repositories the user has since removed.</summary>
    public void Prune(IEnumerable<string> keepRepositories, IEnumerable<string> keepAccounts)
    {
        var liveRepositories = new HashSet<string>(keepRepositories, StringComparer.OrdinalIgnoreCase);

        foreach (var stale in Repositories.Keys.Where(k => !liveRepositories.Contains(k)).ToList())
        {
            Repositories.Remove(stale);
        }

        var liveAccounts = new HashSet<string>(keepAccounts, StringComparer.Ordinal);

        foreach (var stale in Inboxes.Keys.Where(k => !liveAccounts.Contains(k)).ToList())
        {
            Inboxes.Remove(stale);
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

    public string? RunsETag { get; set; }

    public long LastWorkflowRunId { get; set; }

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
                return JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(_path), Options) ?? new MonitorState();
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

                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
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
