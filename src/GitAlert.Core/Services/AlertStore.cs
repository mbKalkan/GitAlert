using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitAlert.Core;

namespace GitAlert.Services;

/// <summary>
/// The alert history shown in the flyout, persisted so the list survives a restart.
/// De-duplicates by <see cref="Alert.Id"/> and keeps at most <c>MaxHistory</c> entries.
/// </summary>
public sealed class AlertStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Alert> _alerts = [];
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);

    private HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private int _maxHistory = 300;

    public AlertStore(string? path = null) => _path = path ?? AppPaths.HistoryFile;

    public int MaxHistory
    {
        get => _maxHistory;
        set
        {
            _maxHistory = Math.Clamp(value, 20, 2000);
            lock (_gate)
            {
                Trim();
            }
        }
    }

    /// <summary>Newest first, without the repositories that are switched off.</summary>
    public IReadOnlyList<Alert> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _alerts.Where(IsShown).ToArray();
            }
        }
    }

    /// <summary>What is unread among the alerts that show; the tray and the window count from here.</summary>
    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return _alerts.Count(a => !a.IsRead && IsShown(a));
            }
        }
    }

    /// <summary>
    /// Repositories whose tick is off in settings. Their alerts stay in the history, out of sight
    /// and out of every count, until the tick comes back: switching off is not removing.
    /// </summary>
    /// <remarks>
    /// They used to stay on show, on the grounds that a switched-off project is still one the user
    /// watches. The user switched one off to make it go away, and it did not.
    /// </remarks>
    public void Hide(IEnumerable<string> repositories)
    {
        var hidden = new HashSet<string>(repositories, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _hidden = hidden;
        }
    }

    /// <summary>Callers hold <see cref="_gate"/>.</summary>
    private bool IsShown(Alert alert) => !_hidden.Contains(alert.Repository);

    /// <summary>
    /// Adds alerts that have not been seen before and returns them newest first, so the caller
    /// knows exactly what is new and worth a toast.
    /// </summary>
    public IReadOnlyList<Alert> Add(IEnumerable<Alert> candidates)
    {
        var accepted = new List<Alert>();

        lock (_gate)
        {
            foreach (var alert in candidates)
            {
                if (_seenIds.Add(alert.Id))
                {
                    accepted.Add(alert);
                }
            }

            if (accepted.Count == 0)
            {
                return [];
            }

            _alerts.AddRange(accepted);
            _alerts.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));
            Trim();
        }

        accepted.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return accepted;
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            foreach (var alert in _alerts)
            {
                alert.IsRead = true;
            }
        }
    }

    public void MarkRead(string id)
    {
        lock (_gate)
        {
            var alert = _alerts.FirstOrDefault(a => a.Id == id);
            if (alert is not null)
            {
                alert.IsRead = true;
            }
        }
    }

    /// <summary>Marks several alerts read under one lock, so reading a whole project is one save.</summary>
    public void MarkRead(IEnumerable<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var alert in _alerts.Where(a => wanted.Contains(a.Id)))
            {
                alert.IsRead = true;
            }
        }
    }

    /// <summary>
    /// Forgets alerts about repositories that are no longer being watched, and reports how many
    /// went.
    /// </summary>
    /// <remarks>
    /// Removing a repository in settings should take its notifications with it. Leaving them
    /// behind kept the project in the list, with a count beside it, for something the user had
    /// just said they were finished with.
    ///
    /// Inbox alerts are exempt. They come from the account's own notification inbox rather than
    /// from the watch list, and can perfectly well be about a repository that was never on it -
    /// somebody mentioning you in a thread somewhere is still worth keeping.
    /// </remarks>
    public int RemoveUnwatched(IEnumerable<string> watched)
    {
        var live = new HashSet<string>(watched, StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            // The ids stay in the seen set. Nothing would re-fetch these anyway - removing a
            // repository drops its sync state too, so adding it back starts from now.
            return _alerts.RemoveAll(a => !IsFromInbox(a) && !live.Contains(a.Repository));
        }
    }

    /// <summary>
    /// Whether an alert came from the notification inbox rather than from a watched repository.
    /// Stamping prefixes the account onto the id, so the source is whatever follows the bar.
    /// </summary>
    private static bool IsFromInbox(Alert alert) =>
        alert.Id.AsSpan(alert.Id.LastIndexOf('|') + 1).StartsWith("inbox:", StringComparison.Ordinal);

    public void Clear()
    {
        lock (_gate)
        {
            _alerts.Clear();

            // Ids stay in the seen set: a cleared history must not resurrect old alerts on the
            // next poll. The set is bounded by the same trim rule below.
            TrimSeenIds();
        }
    }

    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return;
            }

            try
            {
                var stored = JsonSerializer.Deserialize<List<Alert>>(File.ReadAllText(_path), Options);
                if (stored is null)
                {
                    return;
                }

                _alerts.Clear();
                _seenIds.Clear();

                foreach (var alert in stored)
                {
                    // `required` only guards against a property that is missing; an explicit
                    // null in a hand-edited file walks straight through, and the first thing
                    // done with a loaded alert is to read its id and its repository.
                    if (alert is null
                        || string.IsNullOrEmpty(alert.Id)
                        || alert.Repository is null
                        || alert.Title is null)
                    {
                        continue;
                    }

                    if (_seenIds.Add(alert.Id))
                    {
                        _alerts.Add(alert);
                    }
                }

                _alerts.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));
                Trim();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _alerts.Clear();
                _seenIds.Clear();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();

                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_alerts, Options));
                File.Move(temp, _path, overwrite: true);
            }
            catch (IOException)
            {
                // History is a convenience, never worth crashing over.
            }
        }
    }

    private void Trim()
    {
        if (_alerts.Count > _maxHistory)
        {
            _alerts.RemoveRange(_maxHistory, _alerts.Count - _maxHistory);
        }

        TrimSeenIds();
    }

    private void TrimSeenIds()
    {
        // Keep a generous tail of ids beyond the visible history so trimmed-away alerts are not
        // re-announced while GitHub still lists them in its 50-event window.
        const int SeenIdBudgetMultiplier = 4;

        var budget = Math.Max(500, _maxHistory * SeenIdBudgetMultiplier);
        if (_seenIds.Count <= budget)
        {
            return;
        }

        var keep = new HashSet<string>(_alerts.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var id in _seenIds.Where(id => !keep.Contains(id)).Take(_seenIds.Count - budget).ToList())
        {
            _seenIds.Remove(id);
        }
    }
}
