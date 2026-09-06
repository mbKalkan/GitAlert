using CommunityToolkit.Mvvm.ComponentModel;
using GitAlert.Core;

namespace GitAlert.ViewModels;

/// <summary>Presentation wrapper around a single <see cref="Alert"/>.</summary>
public sealed partial class AlertViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isRead;

    /// <summary>Whether this is the row the detail pane is currently showing.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _age;

    /// <summary>
    /// Set by the flyout when more than one account is configured. With a single account the
    /// login would be the same on every card, so it is left off.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta))]
    [NotifyPropertyChangedFor(nameof(RowMeta))]
    // The tooltip embeds Meta, so it goes stale with it. Adding a second account is what changes
    // this, and the row updated while its tooltip went on naming nobody.
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _showAccount;

    public AlertViewModel(Alert model)
    {
        Model = model;
        _isRead = model.IsRead;
        _age = RelativeTime.Format(model.Timestamp);
    }

    public Alert Model { get; }

    public string Title => Model.Title;

    public string? Detail => Model.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Model.Detail);

    /// <summary>
    /// The line a row leads with. For a push that is the commit message: "New commit on main" is
    /// the same sentence on every push and says nothing about which commit this is, so it is
    /// demoted to the meta line and the message takes the top.
    /// </summary>
    public string PrimaryText => LeadsWithDetail ? Model.Detail! : Model.Title;

    /// <summary>The supporting line, or null when the row only needs one.</summary>
    public string? SecondaryText => LeadsWithDetail ? null : Model.Detail;

    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText);

    private bool LeadsWithDetail => Model.Kind == AlertKind.Push && HasDetail;

    public string Repository => Model.Repository;

    public string? Url => Model.Url;

    public AlertKind Kind => Model.Kind;

    /// <summary>The 16 x 16 glyph for the kind, as path data for the view to draw.</summary>
    public string GlyphData => AlertGlyphs.PathFor(Model.Kind);

    /// <summary>The palette key the card is coloured from: the severity when it carries one, the kind otherwise.</summary>
    public string AccentKey => AlertGlyphs.AccentKeyFor(Model.Kind, Model.Severity);

    /// <summary>The palette changed under the row: the view reads the brush again.</summary>
    public void RefreshAccent() => OnPropertyChanged(nameof(AccentKey));

    /// <summary>The dimmed line under the title: repository, who caused it, and which account saw it.</summary>
    public string Meta => Describe(withRepository: true);

    /// <summary>
    /// The same line for a row in the list, where the repository is already named by the group
    /// header above it and repeating it on every row is just noise.
    /// </summary>
    public string RowMeta => Describe(withRepository: false);

    private string Describe(bool withRepository, bool withTitle = true)
    {
        var parts = new List<string>();

        if (withRepository)
        {
            parts.Add(Model.Repository);
        }

        // When the message has taken the top line, the headline belongs here instead.
        if (withTitle && LeadsWithDetail)
        {
            parts.Add(Model.Title);
        }

        if (!string.IsNullOrWhiteSpace(Model.Actor))
        {
            parts.Add(Model.Actor!);
        }

        // A board move has nobody to name, so its line says what moved instead.
        if (!string.IsNullOrWhiteSpace(Model.Note))
        {
            parts.Add(Model.Note!);
        }

        // "deniz · via @deniz" is the same name twice. The account is only worth naming when it
        // is not already obvious from who caused the alert.
        if (ShowAccount
            && !string.IsNullOrWhiteSpace(Model.Account)
            && !string.Equals(Model.Account, Model.Actor, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"via @{Model.Account}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Full text for the tooltip, where there is room for everything.</summary>
    /// <remarks>
    /// The headline is the first line here, so the meta line leaves it out: a push used to read
    /// "New commit on main" twice, once at the top and once in the line that names the repository.
    /// </remarks>
    public string Tooltip
    {
        get
        {
            var when = Model.Timestamp.ToLocalTime().ToString("d MMM yyyy HH:mm");
            var meta = Describe(withRepository: true, withTitle: false);

            return string.IsNullOrWhiteSpace(Model.Detail)
                ? $"{Model.Title}\n{meta}\n{when}"
                : $"{Model.Title}\n{Model.Detail}\n{meta}\n{when}";
        }
    }

    private string? _searchText;

    /// <summary>
    /// Everything on the alert a search may match, in one string: the headline, the message, the
    /// repository, who caused it, the note, what was said, and the fields. Built once, since the
    /// model never changes under the row; the read state is not part of it.
    /// </summary>
    public string SearchText => _searchText ??= string.Join(
        '\n',
        new[]
        {
            Model.Title,
            Model.Detail,
            Model.Repository,
            Model.Actor,
            Model.Note,
            Model.Body,
            Model.Account,
            Model.Fields is { Count: > 0 } fields ? string.Join('\n', fields.Select(f => $"{f.Name} {f.Value}")) : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Whether every word of a search is found somewhere on the alert, in any case.</summary>
    public bool Matches(IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            if (!SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public void MarkRead()
    {
        Model.IsRead = true;
        IsRead = true;
    }

    public void RefreshAge() => Age = RelativeTime.Format(Model.Timestamp);

    /// <summary>Which filter chip this alert belongs to.</summary>
    public AlertFilter Group => Model.Kind switch
    {
        AlertKind.Push or AlertKind.Branch => AlertFilter.Push,
        AlertKind.PullRequest or AlertKind.Review => AlertFilter.PullRequests,
        AlertKind.Issue or AlertKind.Comment or AlertKind.Mention => AlertFilter.Issues,
        AlertKind.Workflow => AlertFilter.Ci,
        AlertKind.Board => AlertFilter.Boards,
        _ => AlertFilter.More,
    };
}

public enum AlertFilter
{
    All,
    Push,
    PullRequests,
    Issues,
    Ci,
    Boards,
    More,
}
