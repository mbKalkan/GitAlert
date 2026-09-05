using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// One changed file: a row in the change list, and the diff shown when that row is picked.
/// </summary>
public sealed partial class FileDiffViewModel : ObservableObject
{
    /// <summary>Whether this is the file whose diff the pane is showing.</summary>
    [ObservableProperty]
    private bool _isSelected;

    private readonly string? _patch;
    private readonly int _changes;

    private IReadOnlyList<DiffLineViewModel>? _lines;
    private string? _note;
    private int _truncated;

    public FileDiffViewModel(GhFileChange file)
    {
        Path = file.Filename;
        Status = Describe(file.Status);
        StatusLetter = Letter(file.Status);
        IsAdded = file.Status is "added" or "copied";
        IsDeleted = file.Status is "removed";
        Additions = file.Additions;
        Deletions = file.Deletions;
        BlobUrl = file.BlobUrl;
        PreviousPath = file.PreviousFilename;

        _patch = file.Patch;
        _changes = file.Changes;
    }

    public string Path { get; }

    /// <summary>The renamed-from path, when this file moved.</summary>
    public string? PreviousPath { get; }

    public bool WasRenamed => !string.IsNullOrEmpty(PreviousPath);

    public string Status { get; }

    /// <summary>The single letter a source control view puts beside a file: M, A, D, R or C.</summary>
    public string StatusLetter { get; }

    /// <summary>Deleted files read as removals, added files as additions, everything else neutral.</summary>
    public bool IsAdded { get; }

    public bool IsDeleted { get; }

    public int Additions { get; }

    public int Deletions { get; }

    public string Counts => $"+{Additions}  -{Deletions}";

    /// <summary>The whole path and the counts, for the row that only has room for the name.</summary>
    public string Tooltip => WasRenamed
        ? $"{Path}\nRenamed from {PreviousPath}\n{Counts}"
        : $"{Path}\n{Counts}";

    public string? BlobUrl { get; }

    /// <summary>
    /// The rendered rows, parsed the first time something asks for them.
    /// </summary>
    /// <remarks>
    /// One click used to parse every file a commit touched, though the pane only ever shows one
    /// of them: a merge across three hundred files built a third of a million row objects before
    /// the first line appeared. The change list binds only the name and the counts, so leaving
    /// the parse until selection means the work follows what is actually being read.
    /// </remarks>
    public IReadOnlyList<DiffLineViewModel> Lines
    {
        get
        {
            EnsureParsed();
            return _lines;
        }
    }

    public int TruncatedLines
    {
        get
        {
            EnsureParsed();
            return _truncated;
        }
    }

    public string? Note
    {
        get
        {
            EnsureParsed();
            return _note;
        }
    }

    public bool HasNote => Note is not null;

    [MemberNotNull(nameof(_lines))]
    private void EnsureParsed()
    {
        if (_lines is not null)
        {
            return;
        }

        var parsed = DiffParser.Parse(_patch, DiffParser.MaxLines, out _truncated);
        _lines = [.. parsed.Select(l => new DiffLineViewModel(l))];

        // GitHub omits the patch for binaries and for diffs it considers too large to inline.
        // Saying so is the honest thing; rendering nothing would look like an empty change.
        _note = _patch is null
            ? _changes == 0
                ? "No textual changes."
                : "GitHub did not return a diff for this file - it is binary or too large to show inline."
            : _truncated > 0
                ? $"{_truncated:N0} more lines not shown. Open the file on GitHub for the whole diff."
                : null;
    }

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

    private static string Letter(string? status) => status switch
    {
        "added" => "A",
        "removed" => "D",
        "renamed" => "R",
        "copied" => "C",
        _ => "M",
    };
}

/// <summary>
/// The selected alert's changes: the files it touched, which unfold under its card in the list,
/// and the diff of the one that is picked, which fills the pane beside the list. Nothing is
/// requested until an alert is actually selected, so browsing the list costs no rate limit.
/// </summary>
public sealed partial class AlertDetailViewModel : ObservableObject, IDisposable
{
    /// <summary>Diffs already fetched, so clicking back and forth does not re-request them.</summary>
    private readonly Dictionary<string, IReadOnlyList<GhFileChange>> _cache = new(StringComparer.Ordinal);

    /// <summary>Insertion order, so the oldest entry is the one that goes when room is needed.</summary>
    private readonly Queue<string> _cacheOrder = new();

    private long _cachedChars;

    private const int CacheLimit = 24;

    /// <summary>
    /// What the cache may hold, counted in patch characters rather than entries. A count of
    /// entries says nothing about what is being held: twenty-four one-line commits are nothing,
    /// and a single commit that regenerated a lock file is tens of megabytes of string on its
    /// own. Sixteen megabytes keeps a working session of diffs without the app growing all day.
    /// </summary>
    private const long CacheBudget = 16L * 1024 * 1024;

    private readonly MonitorService _monitor;

    private CancellationTokenSource? _inFlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanReload))]
    private AlertViewModel? _alert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    [NotifyPropertyChangedFor(nameof(CanReload))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string _summary = string.Empty;

    /// <summary>Shown instead of a diff when the alert points at no files.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    [NotifyPropertyChangedFor(nameof(Caption))]
    private string? _notice;

    /// <summary>
    /// How many changed files are waiting behind "show all". A merge across hundreds of files
    /// would otherwise push every other project off the bottom of the list the files unfold in.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenFiles))]
    [NotifyPropertyChangedFor(nameof(ShowAllFilesLabel))]
    private int _hiddenFileCount;

    /// <summary>
    /// The file whose diff is on screen. The change list under the open alert works the way a
    /// source control view does: every changed file is listed, and picking one shows what
    /// happened to it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    private FileDiffViewModel? _selectedFile;

    /// <summary>Every changed file, whether or not <see cref="Files"/> is showing it yet.</summary>
    private List<FileDiffViewModel> _all = [];

    public AlertDetailViewModel(MonitorService monitor) => _monitor = monitor;

    public ObservableCollection<FileDiffViewModel> Files { get; } = [];

    public bool HasSelection => Alert is not null;

    public bool HasSelectedFile => SelectedFile is not null;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>How many files unfold under the alert before the rest wait behind a click.</summary>
    public const int InlineLimit = 30;

    public bool HasHiddenFiles => HiddenFileCount > 0;

    public string ShowAllFilesLabel => $"Show all {_all.Count} files";

    /// <summary>
    /// The one line under the open alert: the count once it is known, and until then what is
    /// happening instead. The pane beside the list carries the longer version.
    /// </summary>
    public string Caption => this switch
    {
        { IsLoading: true } => "Fetching the changed files…",
        { HasError: true } => "The changed files could not be fetched",
        { HasSummary: true } => Summary,
        { HasNotice: true } => "No changed files",
        _ => string.Empty,
    };

    /// <summary>Only an alert with a diff has anything to fetch again.</summary>
    public bool CanReload => Alert?.Model.HasDiff == true && !IsLoading;

    /// <summary>Shows an alert, fetching its diff if it has one.</summary>
    public async Task ShowAsync(AlertViewModel? alert)
    {
        _inFlight?.Cancel();
        _inFlight = null;

        Alert = alert;
        Files.Clear();
        _all = [];
        HiddenFileCount = 0;
        SelectedFile = null;
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
            Notice = $"A {Describe(model.Kind)} does not point at any changed files. "
                   + "Open it on GitHub to see the rest.";
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
            Forget(alert.Model.Id);
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
    private void SelectFile(FileDiffViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        if (SelectedFile is { } previous)
        {
            previous.IsSelected = false;
        }

        file.IsSelected = true;
        SelectedFile = file;
    }

    private static Task<List<GhFileChange>> FetchAsync(
        GitHubClient client,
        RepoRef repo,
        Alert model,
        CancellationToken ct)
    {
        var target = model.Diff;

        if (target.PullRequest is { } number)
        {
            return client.GetPullRequestFilesAsync(repo, number, ct);
        }

        // A push of several commits is only meaningful as the net change across the range; a
        // single commit is its own diff and costs one request either way.
        return string.IsNullOrEmpty(target.Base)
            ? Single(client.GetCommitAsync(repo, target.Head!, ct))
            : Range(client.GetComparisonAsync(repo, target.Base!, target.Head!, ct));

        static async Task<List<GhFileChange>> Single(Task<GhCommitWithFiles> pending) =>
            (await pending.ConfigureAwait(false)).Files;

        static async Task<List<GhFileChange>> Range(Task<GhComparison> pending) =>
            (await pending.ConfigureAwait(false)).Files;
    }

    private void Render(IReadOnlyList<GhFileChange> files)
    {
        _all = [.. files.Select(f => new FileDiffViewModel(f))];

        Files.Clear();

        foreach (var file in _all.Take(InlineLimit))
        {
            Files.Add(file);
        }

        HiddenFileCount = _all.Count - Files.Count;

        var additions = files.Sum(f => f.Additions);
        var deletions = files.Sum(f => f.Deletions);

        Summary = files.Count == 0
            ? string.Empty
            : $"{files.Count} {(files.Count == 1 ? "file" : "files")} changed  ·  +{additions:N0}  -{deletions:N0}";

        Notice = files.Count == 0
            ? "GitHub reported no changed files here. A merge that brought nothing new does that."
            : null;

        // Landing on the first file means one click gets you to a diff rather than two.
        SelectFile(Files.FirstOrDefault());
    }

    /// <summary>
    /// Lets the rest of a long change list out. Only the tail is added, so the rows already on
    /// screen - the picked one among them - stay exactly where they are.
    /// </summary>
    [RelayCommand]
    private void ShowAllFiles()
    {
        foreach (var file in _all.Skip(Files.Count))
        {
            Files.Add(file);
        }

        HiddenFileCount = 0;
    }

    private static string Describe(AlertKind kind) => kind switch
    {
        AlertKind.Workflow => "CI run",
        AlertKind.Release => "release",
        AlertKind.Issue => "issue",
        AlertKind.Comment => "comment",
        AlertKind.Review => "review",
        AlertKind.Mention => "mention",
        AlertKind.Branch => "branch or tag alert",
        AlertKind.Star => "star",
        AlertKind.Fork => "fork",
        _ => "alert of this kind",
    };

    private void Remember(string id, IReadOnlyList<GhFileChange> files)
    {
        var size = Weigh(files);

        // No point clearing everything to make room for one thing that would not fit anyway.
        if (size > CacheBudget || _cache.ContainsKey(id))
        {
            return;
        }

        _cache[id] = files;
        _cacheOrder.Enqueue(id);
        _cachedChars += size;

        while (_cacheOrder.Count > 0 && (_cacheOrder.Count > CacheLimit || _cachedChars > CacheBudget))
        {
            Forget(_cacheOrder.Dequeue());
        }
    }

    private void Forget(string id)
    {
        if (_cache.Remove(id, out var dropped))
        {
            _cachedChars = Math.Max(0, _cachedChars - Weigh(dropped));
        }
    }

    private static long Weigh(IReadOnlyList<GhFileChange> files) =>
        files.Sum(f => (long)(f.Patch?.Length ?? 0));

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
