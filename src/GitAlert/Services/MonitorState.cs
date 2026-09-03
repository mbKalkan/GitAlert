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
    public Dictionary<string, RepoState> Repositories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? InboxETag { get; set; }

    public DateTimeOffset? InboxHighWater { get; set; }

    public DateTimeOffset? LastSuccessfulPoll { get; set; }

    public RepoState For(string fullName)
    {
        if (!Repositories.TryGetValue(fullName, out var state))
        {
            state = new RepoState();
            Repositories[fullName] = state;
        }

        return state;
    }

    /// <summary>Drops bookkeeping for repositories the user has since removed.</summary>
    public void Prune(IEnumerable<string> keep)
    {
        var live = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);

        foreach (var stale in Repositories.Keys.Where(k => !live.Contains(k)).ToList())
        {
            Repositories.Remove(stale);
        }
    }
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
