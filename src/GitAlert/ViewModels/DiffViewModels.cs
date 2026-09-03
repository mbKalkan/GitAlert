using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.Platform;
using GitAlert.Services;

namespace GitAlert.ViewModels;

/// <summary>One row of a rendered diff.</summary>
public sealed class DiffLineViewModel(DiffLine line)
{
    public DiffLineKind Kind { get; } = line.Kind;

    public string Text { get; } = line.Text;

    public string OldNumber { get; } = line.OldNumber;

    public string NewNumber { get; } = line.NewNumber;

    /// <summary>The character git puts in front of the line, kept for copy-paste fidelity.</summary>
    public string Marker { get; } = line.Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " ",
    };
}

/// <summary>One changed file, collapsible so a large change stays navigable.</summary>
public sealed partial class FileDiffViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    public FileDiffViewModel(GhFileChange file, bool expanded)
    {
        _isExpanded = expanded;

        Path = file.Filename;
        Status = Describe(file.Status);
        Additions = file.Additions;
        Deletions = file.Deletions;
        BlobUrl = file.BlobUrl;
        PreviousPath = file.PreviousFilename;

        var lines = DiffParser.Parse(file.Patch, DiffParser.MaxLines, out var dropped);
        Lines = [.. lines.Select(l => new DiffLineViewModel(l))];
        TruncatedLines = dropped;

        // GitHub omits the patch for binaries and for diffs it considers too large to inline.
        // Saying so is the honest thing; rendering nothing would look like an empty change.
        Note = file.Patch is null
            ? file.Changes == 0
                ? "No textual changes."
                : "GitHub did not return a diff for this file - it is binary or too large to show inline."
            : dropped > 0
                ? $"{dropped:N0} more lines not shown. Open the file on GitHub for the whole diff."
                : null;
    }

    public string Path { get; }

    /// <summary>The renamed-from path, when this file moved.</summary>
    public string? PreviousPath { get; }

    public bool WasRenamed => !string.IsNullOrEmpty(PreviousPath);

    public string Status { get; }

    public int Additions { get; }

    public int Deletions { get; }

    public string Counts => $"+{Additions}  -{Deletions}";

    public string? BlobUrl { get; }

    public IReadOnlyList<DiffLineViewModel> Lines { get; }

    public int TruncatedLines { get; }

    public string? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>The directory part, dimmed in the header so the file name itself stands out.</summary>
    public string Folder
    {
        get
        {
            var cut = Path.LastIndexOf('/');
            return cut < 0 ? string.Empty : Path[..(cut + 1)];
        }
    }

    public string FileName
    {
        get
        {
            var cut = Path.LastIndexOf('/');
            return cut < 0 ? Path : Path[(cut + 1)..];
        }
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void OpenOnGitHub()
    {
        if (!string.IsNullOrEmpty(BlobUrl))
        {
            Browser.Open(BlobUrl);
        }
    }

    private static string Describe(string? status) => status switch
    {
        "added" => "added",
        "removed" => "deleted",
        "renamed" => "renamed",
        "copied" => "copied",
        "changed" => "changed",
        _ => "modified",
    };
}

/// <summary>
/// The right-hand pane: whatever is known about the selected alert, and for anything that touches
/// code, the files it changed with their diffs fetched on demand. Nothing is requested until an
/// alert is actually selected, so browsing the list costs no rate limit.
/// </summary>
public sealed partial class AlertDetailViewModel : ObservableObject, IDisposable
{
    /// <summary>Diffs already fetched, so clicking back and forth does not re-request them.</summary>
    private readonly Dictionary<string, IReadOnlyList<GhFileChange>> _cache = new(StringComparer.Ordinal);

    private const int CacheLimit = 24;

    private readonly MonitorService _monitor;

    private CancellationTokenSource? _inFlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AlertViewModel? _alert;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _summary = string.Empty;

    /// <summary>Shown instead of a diff when the alert is not about code at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public AlertDetailViewModel(MonitorService monitor) => _monitor = monitor;

    public ObservableCollection<FileDiffViewModel> Files { get; } = [];

    public bool HasSelection => Alert is not null;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>Shows an alert, fetching its diff if it has one.</summary>
    public async Task ShowAsync(AlertViewModel? alert)
    {
        _inFlight?.Cancel();
        _inFlight = null;

        Alert = alert;
        Files.Clear();
        Error = null;
        Summary = string.Empty;
        Notice = null;
        IsLoading = false;

        if (alert is null)
        {
            return;
        }

        var model = alert.Model;

        if (!model.HasDiff)
        {
            Notice = "This alert is not about a code change, so there is nothing to diff.";
            return;
        }

        if (!RepoRef.TryParse(model.Repository, out var repo))
        {
            Error = $"Cannot work out which repository {model.Repository} refers to.";
            return;
        }

        if (_cache.TryGetValue(model.Id, out var cached))
        {
            Render(cached);
            return;
        }

        var client = _monitor.ClientFor(AccountIdOf(model));

        if (client is null)
        {
            Error = "The account this alert arrived through is no longer configured, "
                  + "so its token cannot be used to fetch the change.";
            return;
        }

        var cts = new CancellationTokenSource();
        _inFlight = cts;
        IsLoading = true;

        try
        {
            var files = await FetchAsync(client, repo, model, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            Remember(model.Id, files);
            Render(files);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitHubException ex)
        {
            Error = ex.UserMessage;
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cts))
            {
                IsLoading = false;
                _inFlight = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>Drops the cached diff for the current alert and fetches it again.</summary>
    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (Alert is { } alert)
        {
            _cache.Remove(alert.Model.Id);
            await ShowAsync(alert).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenOnGitHub()
    {
        if (Alert?.Url is { } url)
        {
            Browser.Open(url);
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var file in Files)
        {
            file.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var file in Files)
        {
            file.IsExpanded = false;
        }
    }

    private static Task<List<GhFileChange>> FetchAsync(
        GitHubClient client,
        RepoRef repo,
        Alert model,
        CancellationToken ct)
    {
        if (model.PullRequestNumber is { } number)
        {
            return client.GetPullRequestFilesAsync(repo, number, ct);
        }

        var head = model.DiffHead!;

        // A push of several commits is only meaningful as the net change across the range; a
        // single commit is its own diff and costs one request either way.
        return string.IsNullOrEmpty(model.DiffBase)
            ? Single(client.GetCommitAsync(repo, head, ct))
            : Range(client.GetComparisonAsync(repo, model.DiffBase!, head, ct));

        static async Task<List<GhFileChange>> Single(Task<GhCommitWithFiles> pending) =>
            (await pending.ConfigureAwait(false)).Files;

        static async Task<List<GhFileChange>> Range(Task<GhComparison> pending) =>
            (await pending.ConfigureAwait(false)).Files;
    }

    private void Render(IReadOnlyList<GhFileChange> files)
    {
        Files.Clear();

        // Expanding everything on a large change makes the pane unusable, so past a handful of
        // files they arrive collapsed and the header says how to open them.
        var expandAll = files.Count <= 4;

        foreach (var file in files)
        {
            Files.Add(new FileDiffViewModel(file, expandAll));
        }

        var additions = files.Sum(f => f.Additions);
        var deletions = files.Sum(f => f.Deletions);

        Summary = files.Count == 0
            ? "No files changed."
            : $"{files.Count} {(files.Count == 1 ? "file" : "files")} changed  ·  +{additions:N0}  -{deletions:N0}";

        Notice = files.Count == 0
            ? "GitHub reported no file changes for this commit. It may be a merge with no net change."
            : null;
    }

    private void Remember(string id, IReadOnlyList<GhFileChange> files)
    {
        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        _cache[id] = files;
    }

    /// <summary>
    /// Alerts stored before diffs existed carry no account id of their own, but every stamped id
    /// is prefixed with the account that saw it, so it can still be recovered.
    /// </summary>
    private static string? AccountIdOf(Alert alert)
    {
        if (!string.IsNullOrEmpty(alert.AccountId))
        {
            return alert.AccountId;
        }

        var cut = alert.Id.IndexOf('|');
        return cut > 0 ? alert.Id[..cut] : null;
    }

    public void Dispose()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = null;
    }
}
