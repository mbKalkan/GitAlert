using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GitAlert.Core;

namespace GitAlert.ViewModels;

/// <summary>Presentation wrapper around a single <see cref="Alert"/>.</summary>
public sealed partial class AlertViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isRead;

    [ObservableProperty]
    private string _age;

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

    public string Repository => Model.Repository;

    public string? Url => Model.Url;

    public AlertKind Kind => Model.Kind;

    public Geometry Glyph => AlertGlyphs.GlyphFor(Model.Kind);

    public Brush Accent => AlertGlyphs.BrushFor(Model.Kind, Model.Severity);

    /// <summary>The dimmed line under the title: repository, and who caused the event.</summary>
    public string Meta =>
        string.IsNullOrWhiteSpace(Model.Actor)
            ? Model.Repository
            : $"{Model.Repository} · {Model.Actor}";

    /// <summary>Full text for the tooltip, where there is room for everything.</summary>
    public string Tooltip =>
        string.IsNullOrWhiteSpace(Model.Detail)
            ? $"{Model.Title}\n{Meta}\n{Model.Timestamp.ToLocalTime():d MMM yyyy HH:mm}"
            : $"{Model.Title}\n{Model.Detail}\n{Meta}\n{Model.Timestamp.ToLocalTime():d MMM yyyy HH:mm}";

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
    More,
}
