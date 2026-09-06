using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Services;

namespace GitAlert.ViewModels;

/// <summary>
/// The Diagnostics page: what the monitor last did with GitHub, so "why did I not hear about
/// that" can be answered from the window rather than from the state file. Reads a snapshot from
/// the monitor after every check and every half minute in between, so the ages stay honest.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly MonitorService? _monitor;
    private readonly UiThread _ui;
    private readonly Timer _timer;

    private MonitorDiagnostics? _snapshot;

    [ObservableProperty]
    private string _statusText = "GitAlert is not checking anything.";

    [ObservableProperty]
    private string _lastCheckText = string.Empty;

    [ObservableProperty]
    private string _nextCheckText = string.Empty;

    /// <summary>What the last button press did, under the buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>With one account, naming it on every row is noise.</summary>
    [ObservableProperty]
    private bool _showAccounts;

    public DiagnosticsViewModel(MonitorService? monitor)
    {
        _monitor = monitor;
        _ui = UiThread.Capture();

        if (_monitor is not null)
        {
            _monitor.StatusChanged += OnStatusChanged;
        }

        Refresh();

        _timer = new Timer(_ => _ui.Post(Refresh), null, RefreshInterval, RefreshInterval);
    }

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    public ObservableCollection<SubjectRowViewModel> Subjects { get; } = [];

    public bool HasMessage => Message.Length > 0;

    public bool HasSubjects => Subjects.Count > 0;

    /// <summary>The page as plain text, for pasting into an issue. Names no token.</summary>
    public string Report =>
        (_snapshot ?? _monitor?.Diagnose())?.ToReport(UpdateChecker.CurrentVersion.ToString(3), Platform)
        ?? "GitAlert is not checking anything.";

    private static string Platform => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})";

    private void OnStatusChanged(object? sender, MonitorStatus status) => _ui.Post(Refresh);

    /// <summary>Reads the monitor again and brings every line and row up to date, in place.</summary>
    [RelayCommand]
    public void Refresh()
    {
        if (_monitor is null)
        {
            return;
        }

        var snapshot = _monitor.Diagnose();
        _snapshot = snapshot;

        var now = snapshot.Now;

        StatusText = $"{Describe(snapshot.State)} · {snapshot.StatusMessage}";
        LastCheckText = LastCheck(snapshot, now);
        NextCheckText = NextCheck(snapshot, now);
        ShowAccounts = snapshot.Accounts.Count > 1;

        Sync(Accounts, snapshot.Accounts, a => a.Login, () => new AccountRowViewModel(), (row, account) => row.Apply(account, now));
        Sync(Subjects, snapshot.Subjects, s => s.Key, () => new SubjectRowViewModel(), (row, subject) => row.Apply(subject, now, ShowAccounts));

        OnPropertyChanged(nameof(HasSubjects));
        OnPropertyChanged(nameof(Report));
    }

    public void NoteCopied() => Message = "Copied the report. Paste it into an issue, or keep it for later.";

    public void NoteCopyFailed() => Message = "Could not reach the clipboard; the report is in the data folder's log instead.";

    private static string Describe(ConnectionState state) => state switch
    {
        ConnectionState.NotConfigured => "Not configured",
        ConnectionState.Connecting => "Checking",
        ConnectionState.Connected => "Connected",
        ConnectionState.Warning => "Connected, with a problem",
        _ => "Error",
    };

    private static string LastCheck(MonitorDiagnostics snapshot, DateTimeOffset now)
    {
        if (snapshot.LastPollStartedAt is not { } started)
        {
            return "No check has run yet.";
        }

        if (snapshot.LastPollFinishedAt is not { } finished || finished < started)
        {
            return $"A check began {Ago(started, now)} and is still going.";
        }

        var took = finished - started;
        var duration = took.TotalSeconds < 1 ? "under a second" : $"{took.TotalSeconds:0.#} seconds";
        var success = snapshot.LastSuccess is { } ok && ok >= started
            ? "Everything answered."
            : snapshot.LastSuccess is { } last
                ? $"Something could not be checked; the last check with no problem was {Ago(last, now)}."
                : "Something could not be checked; no check has gone through cleanly yet.";

        return $"The last check finished {Ago(finished, now)} and took {duration}. {success}";
    }

    private static string NextCheck(MonitorDiagnostics snapshot, DateTimeOffset now)
    {
        var interval = $"every {MonitorDiagnostics.Describe(snapshot.Interval)}";

        if (snapshot.ServerRequestedInterval is { } asked && asked > TimeSpan.Zero)
        {
            interval += $"; GitHub asked for at least {MonitorDiagnostics.Describe(asked)} between checks";
        }

        if (snapshot.NextPollAt is not { } next || next <= now)
        {
            return $"The next check is due any moment, {interval}.";
        }

        return $"The next check is in {RelativeTime.Format(now, next)}, {interval}.";
    }

    /// <summary>"2m ago", or "just now" - "now ago" is not English.</summary>
    internal static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = RelativeTime.Format(at, now);
        return age == "now" ? "just now" : $"{age} ago";
    }

    /// <summary>
    /// Brings a list of rows to a list of records by key, updating what is there rather than
    /// rebuilding it: a page refreshed every half minute must not lose its scroll position.
    /// </summary>
    private static void Sync<TRow, TItem>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> items,
        Func<TItem, string> keyOf,
        Func<TRow> create,
        Action<TRow, TItem> apply)
        where TRow : class, IKeyedRow
    {
        var wanted = items.Select(keyOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(rows[i].Key))
            {
                rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            var key = keyOf(items[i]);
            var index = -1;

            for (var j = 0; j < rows.Count; j++)
            {
                if (string.Equals(rows[j].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    index = j;
                    break;
                }
            }

            TRow row;

            if (index < 0)
            {
                row = create();
                row.Key = key;
                rows.Insert(Math.Min(i, rows.Count), row);
            }
            else
            {
                row = rows[index];

                if (index != i)
                {
                    rows.Move(index, Math.Min(i, rows.Count - 1));
                }
            }

            apply(row, items[i]);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();

        if (_monitor is not null)
        {
            _monitor.StatusChanged -= OnStatusChanged;
        }
    }
}

/// <summary>A row the page keeps between refreshes, found again by its key.</summary>
public interface IKeyedRow
{
    string Key { get; set; }
}

/// <summary>One account on the Diagnostics page: its login, its budget, what the session spent.</summary>
public sealed partial class AccountRowViewModel : ObservableObject, IKeyedRow
{
    public string Key { get; set; } = string.Empty;

    [ObservableProperty]
    private string _login = string.Empty;

    /// <summary>"4,812 of 5,000 calls left; resets at 15:00", or that the budget is not known yet.</summary>
    [ObservableProperty]
    private string _budgetText = string.Empty;

    [ObservableProperty]
    private string _requestsText = string.Empty;

    /// <summary>"Throttled until 14:32", "Paused", "No usable token"; empty when nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problemText = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    public void Apply(AccountDiagnostics account, DateTimeOffset now)
    {
        Login = account.Login;

        BudgetText = account.RateLimit.IsKnown
            ? $"{account.RateLimit.Remaining:N0} of {account.RateLimit.Limit:N0} calls left this hour; resets at {account.RateLimit.ResetsAt.ToLocalTime():HH:mm}"
            : "Budget not known yet; GitHub says with the first answer.";

        RequestsText = account.Requests switch
        {
            0 => "No requests yet this session.",
            1 => "1 request this session.",
            _ => $"{account.Requests:N0} requests this session, {account.NotModified:N0} of them answered unchanged at no cost.",
        };

        ProblemText = account switch
        {
            { Enabled: false } => "Paused in settings.",
            { HasToken: false } => "No usable token; choose Replace token on the Accounts page.",
            { ThrottledUntil: { } until } => $"Throttled by GitHub until {until.ToLocalTime():HH:mm}; nothing is asked before then.",
            _ => string.Empty,
        };
    }
}

/// <summary>One repository, board or inbox on the Diagnostics page, and how its last check went.</summary>
public sealed partial class SubjectRowViewModel : ObservableObject, IKeyedRow
{
    public string Key { get; set; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>"board" or "inbox" after the name; nothing on a repository, which is the usual case.</summary>
    [ObservableProperty]
    private string _tag = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAccount))]
    private string _account = string.Empty;

    [ObservableProperty]
    private string _checkedText = string.Empty;

    [ObservableProperty]
    private string _outcomeText = string.Empty;

    [ObservableProperty]
    private bool _isRead;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private bool _isIdle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _detailText = string.Empty;

    public bool ShowAccount => Account.Length > 0;

    public bool HasError => Error.Length > 0;

    public void Apply(SubjectDiagnostics subject, DateTimeOffset now, bool showAccount)
    {
        Name = subject.Name;
        Tag = subject.Kind switch
        {
            "Board" => "board",
            "Inbox" => "inbox",
            _ => string.Empty,
        };
        Account = showAccount ? subject.Account : string.Empty;

        CheckedText = subject switch
        {
            { Enabled: false } => "Switched off in settings",
            { LastPolledAt: { } at } => $"Checked {DiagnosticsViewModel.Ago(at, now)}",
            _ => "Not checked yet",
        };

        (OutcomeText, IsRead, IsFailed, IsIdle) = subject switch
        {
            { Enabled: false } => ("off", false, false, true),
            { Outcome: PollOutcome.Read } => ("read", true, false, false),
            { Outcome: PollOutcome.Unchanged } => ("unchanged", false, false, false),
            { Outcome: PollOutcome.Failed } => ("failed", false, true, false),
            _ => ("waiting", false, false, true),
        };

        Error = subject.Outcome == PollOutcome.Failed && subject.Enabled ? subject.Error ?? "Could not be checked." : string.Empty;

        var parts = new List<string>();

        if (subject.LastPolledAt is not null)
        {
            parts.Add(subject.Requests == 1 ? "1 request" : $"{subject.Requests} requests");
            parts.Add(subject.AlertsTotal switch
            {
                0 => "no alerts yet",
                1 => subject.AlertsFound == 1 ? "1 alert, found in the last check" : "1 alert",
                _ => subject.AlertsFound > 0
                    ? $"{subject.AlertsTotal} alerts, {subject.AlertsFound} in the last check"
                    : $"{subject.AlertsTotal} alerts",
            });
        }

        if (subject.WatchingSince is { } since)
        {
            parts.Add($"watching since {since.ToLocalTime():d MMM HH:mm}");
        }

        DetailText = string.Join(" · ", parts);
    }
}
