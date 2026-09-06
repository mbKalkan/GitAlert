using System.Globalization;
using System.Text;
using GitAlert.Core;
using GitAlert.GitHub;

namespace GitAlert.Services;

/// <summary>How the last check of one thing being watched went.</summary>
public enum PollOutcome
{
    /// <summary>Not checked yet: added a moment ago, or its account is off or throttled.</summary>
    NotYet,

    /// <summary>GitHub said nothing had changed, which cost nothing against the budget.</summary>
    Unchanged,

    /// <summary>Something came back and was read, whether or not it made an alert.</summary>
    Read,

    Failed,
}

/// <summary>One repository, board or inbox, and what the last check of it did.</summary>
public sealed record SubjectDiagnostics(
    string Key,
    string Name,
    string Kind,
    string Account,
    bool Enabled,
    DateTimeOffset? LastPolledAt,
    PollOutcome Outcome,
    string? Error,
    int Requests,
    int AlertsFound,
    int AlertsTotal,
    DateTimeOffset? WatchingSince);

/// <summary>One account: its budget with GitHub, and how much of it the session has spent.</summary>
public sealed record AccountDiagnostics(
    string Login,
    bool Enabled,
    bool HasToken,
    RateLimitStatus RateLimit,
    DateTimeOffset? ThrottledUntil,
    long Requests,
    long NotModified);

/// <summary>
/// What the monitor has been doing, as of a moment: the timing of the checks, each account's
/// budget, and how the last check of every repository, board and inbox went. Made for the page
/// that answers "why did I not hear about that", and for the report copied out of it.
/// </summary>
public sealed record MonitorDiagnostics(
    DateTimeOffset Now,
    ConnectionState State,
    string StatusMessage,
    DateTimeOffset? LastPollStartedAt,
    DateTimeOffset? LastPollFinishedAt,
    DateTimeOffset? LastSuccess,
    DateTimeOffset? NextPollAt,
    TimeSpan Interval,
    TimeSpan? ServerRequestedInterval,
    IReadOnlyList<AccountDiagnostics> Accounts,
    IReadOnlyList<SubjectDiagnostics> Subjects)
{
    /// <summary>
    /// The same, as plain text for a bug report: nothing in it names a token, and the only
    /// personal things are the logins and the repository names the report is about.
    /// </summary>
    public string ToReport(string version, string platform)
    {
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"GitAlert {version} on {platform} · {Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Status: {State} · {StatusMessage}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Last check: {Clock(LastPollStartedAt)} to {Clock(LastPollFinishedAt)} · last success: {Clock(LastSuccess)} · next: {Clock(NextPollAt)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Interval: every {Describe(Interval)}{(ServerRequestedInterval is { } asked ? $" (GitHub asked for at least {Describe(asked)})" : string.Empty)}");

        text.AppendLine();
        text.AppendLine("Accounts");

        foreach (var account in Accounts)
        {
            var budget = account.RateLimit.IsKnown
                ? $"{account.RateLimit.Remaining}/{account.RateLimit.Limit} calls left, resets {Clock(account.RateLimit.ResetsAt)}"
                : "budget not known yet";
            var throttled = account.ThrottledUntil is { } until ? $" · throttled until {Clock(until)}" : string.Empty;
            var state = account switch
            {
                { Enabled: false } => " · paused",
                { HasToken: false } => " · no usable token",
                _ => string.Empty,
            };

            text.AppendLine(CultureInfo.InvariantCulture, $"  {account.Login}: {budget}{throttled} · {account.Requests} requests this session, {account.NotModified} unchanged{state}");
        }

        text.AppendLine();
        text.AppendLine("Repositories, boards and inboxes");

        foreach (var subject in Subjects)
        {
            var outcome = subject switch
            {
                { Enabled: false } => "switched off",
                { Outcome: PollOutcome.NotYet } => "not checked yet",
                { Outcome: PollOutcome.Unchanged } => "unchanged",
                { Outcome: PollOutcome.Read } => "read",
                _ => $"failed: {subject.Error}",
            };

            var since = subject.WatchingSince is { } from ? $" · watching since {from:yyyy-MM-dd HH:mm}" : string.Empty;

            text.AppendLine(CultureInfo.InvariantCulture, $"  {subject.Name} [{subject.Kind}, {subject.Account}]: checked {Clock(subject.LastPolledAt)}, {outcome} · {subject.Requests} requests · {subject.AlertsTotal} alerts, {subject.AlertsFound} in the last check{since}");
        }

        return text.ToString();
    }

    private static string Clock(DateTimeOffset? at) =>
        at is { } moment ? moment.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "never";

    /// <summary>"2 minutes", "90 seconds", "1 hour": what a person would call the span.</summary>
    public static string Describe(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{(int)span.TotalSeconds} seconds",
        { TotalMinutes: 1 } => "minute",
        { TotalMinutes: < 60 } when span.Seconds == 0 => $"{(int)span.TotalMinutes} minutes",
        { TotalMinutes: < 60 } => $"{(int)span.TotalSeconds} seconds",
        { TotalHours: 1 } => "hour",
        _ => $"{span.TotalHours:0.#} hours",
    };
}
