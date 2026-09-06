using System.Net.Http;
using GitAlert.Core;
using GitAlert.GitHub;

namespace GitAlert.Services;

/// <summary>A newer GitAlert than the one running, and where to get it.</summary>
public sealed record AvailableUpdate(Version Version, string Url, string? Name);

/// <summary>
/// Asks GitHub, once a day, whether a newer GitAlert has been released. The question is one
/// unauthenticated request for the repository's latest release - the same host the alerts come
/// from, no token, nothing about the user in it - and the answer is only ever shown, never acted
/// on: the release page opens in the browser when asked, and that is all.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>Where GitAlert is published; the releases page is what an update opens.</summary>
    public static readonly RepoRef Repository = new("mbKalkan", "GitAlert");

    public const string ReleasesUrl = "https://github.com/mbKalkan/GitAlert/releases/latest";

    /// <summary>A moment after starting, so the first poll of the repositories goes first.</summary>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly GitHubClient _client;
    private readonly Timer _timer;
    private readonly object _sync = new();

    private bool _enabled;
    private int _checking;

    public UpdateChecker(HttpClient? http = null, Version? current = null)
    {
        _client = new GitHubClient(http);
        Current = current ?? CurrentVersion;
        _timer = new Timer(_ => RunScheduledCheck(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The version this build carries, as three numbers: what a release tag names.</summary>
    public static Version CurrentVersion { get; } =
        Trim(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(1, 0, 0));

    /// <summary>The version being compared against; the build's own unless a test says otherwise.</summary>
    public Version Current { get; }

    /// <summary>Raised on a background thread whenever a check finishes or the switch is thrown.</summary>
    public event EventHandler? Changed;

    /// <summary>The newer release found by the last check, or null when this is the newest.</summary>
    public AvailableUpdate? Available { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>Why the last check could not answer, in plain words; null after a check that could.</summary>
    public string? LastError { get; private set; }

    public bool IsChecking => Volatile.Read(ref _checking) == 1;

    /// <summary>Whether the daily check is on. Off, nothing is asked until something asks by hand.</summary>
    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _enabled;
            }
        }
    }

    /// <summary>Turns the daily check on or off. On, the first check follows shortly.</summary>
    public void Configure(bool enabled)
    {
        lock (_sync)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;
            _timer.Change(enabled ? FirstCheckDelay : Timeout.InfiniteTimeSpan, enabled ? CheckInterval : Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks GitHub now. A second call while one is in flight simply returns what is known; the one
    /// in flight will announce itself. Every way this can fail is kept as <see cref="LastError"/>
    /// rather than thrown: a missed check is nothing to interrupt anyone over.
    /// </summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return Available;
        }

        try
        {
            var release = await _client.GetLatestReleaseAsync(Repository, ct).ConfigureAwait(false);

            Available = Compare(release, Current);
            LastError = null;
        }
        catch (GitHubException ex)
        {
            LastError = ex.UserMessage;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            LastError = "Cannot reach GitHub.";
        }
        finally
        {
            LastCheckedAt = DateTimeOffset.Now;
            Volatile.Write(ref _checking, 0);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Available;
    }

    /// <summary>
    /// Whether a release is newer than the version running. A tag reads <c>v2.6.0</c>; the
    /// leading letter goes, and only three numbers count, since that is all a release tag has.
    /// A draft or a pre-release is not on offer, whatever its number.
    /// </summary>
    public static AvailableUpdate? Compare(GhRelease release, Version current)
    {
        if (release.Draft || release.Prerelease || !TryParseVersion(release.TagName, out var version))
        {
            return null;
        }

        return version > Trim(current)
            ? new AvailableUpdate(version, release.HtmlUrl is { Length: > 0 } url ? url : ReleasesUrl, release.Name)
            : null;
    }

    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        if (!Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = Trim(parsed);
        return true;
    }

    /// <summary>Three numbers, with a missing one read as zero: <c>2.6</c> is <c>2.6.0</c>.</summary>
    private static Version Trim(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private void RunScheduledCheck()
    {
        lock (_sync)
        {
            if (!_enabled)
            {
                return;
            }
        }

        // A timer thread has nobody to hand an exception to; CheckAsync keeps its own, and what
        // is left is a fault worth nothing more than a missed check.
        _ = CheckAsync().ContinueWith(
            static task => _ = task.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _client.Dispose();
    }
}
