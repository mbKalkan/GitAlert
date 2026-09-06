using System.Text.Json;
using GitAlert.Configuration;
using GitAlert.Core;
using GitAlert.Services;

namespace GitAlert.GitHub;

/// <summary>
/// Turns two readings of a project board into alerts. GitHub keeps no history of a board's items
/// for the API to hand out, so the only way to notice a card moving is to remember where every
/// card stood and look again: an item that is new, an item whose Status changed, an item archived
/// or gone. Nothing here knows who moved the card, because GitHub does not say.
/// </summary>
public static class BoardTranslator
{
    /// <summary>The field the board's columns are made of, by the name GitHub gives it.</summary>
    public const string StatusFieldName = "Status";

    /// <summary>How many of an item's fields ride along on an alert, for the card in the detail pane.</summary>
    private const int MaxFieldsOnAlert = 12;

    /// <summary>
    /// The field whose value is the column: the single select called Status, or failing that the
    /// first single select the board has.
    /// </summary>
    public static GhProjectField? StatusFieldOf(IEnumerable<GhProjectField> fields)
    {
        var selects = fields.Where(f => f.IsSingleSelect).ToList();

        return selects.FirstOrDefault(f => string.Equals(f.Name, StatusFieldName, StringComparison.OrdinalIgnoreCase))
            ?? selects.FirstOrDefault();
    }

    /// <summary>What an item looks like right now, boiled down to what a later reading is compared against.</summary>
    public static BoardItemState Snapshot(GhProjectItem item, long? statusFieldId)
    {
        var content = item.Content;
        var state = new BoardItemState
        {
            ContentType = item.ContentType,
            Number = content.GetIntOrNull("number"),
            Title = content.GetStringOrNull("title") ?? TitleField(item),
            Url = content.GetStringOrNull("html_url"),
            Body = PlainText.FromMarkdown(content.GetStringOrNull("body")),
            UpdatedAt = item.UpdatedAt,
            IsArchived = item.ArchivedAt is not null,
        };

        foreach (var field in item.Fields)
        {
            if (string.Equals(field.DataType, "title", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = DescribeValue(field.Value);

            if (statusFieldId is { } id && field.Id == id
                || statusFieldId is null && string.Equals(field.Name, StatusFieldName, StringComparison.OrdinalIgnoreCase))
            {
                state.Status = value;
            }

            if (!string.IsNullOrWhiteSpace(field.Name) && value is not null && state.Fields.Count < MaxFieldsOnAlert)
            {
                state.Fields[field.Name] = value;
            }
        }

        return state;
    }

    private static string? TitleField(GhProjectItem item)
    {
        var title = item.Fields.FirstOrDefault(f => string.Equals(f.DataType, "title", StringComparison.OrdinalIgnoreCase));
        return title is null ? null : DescribeValue(title.Value);
    }

    /// <summary>
    /// A field's value as one line of text, whatever its type. Single selects and iterations carry
    /// a name, people a login, labels a name each, dates and numbers themselves.
    /// </summary>
    public static string? DescribeValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return Trimmed(value.GetString());

            case JsonValueKind.Number:
                return value.GetRawText();

            case JsonValueKind.True:
                return "yes";

            case JsonValueKind.False:
                return "no";

            case JsonValueKind.Array:
                var parts = value.EnumerateArray().Select(DescribeValue).Where(p => p is not null).ToList();
                return parts.Count == 0 ? null : string.Join(", ", parts);

            case JsonValueKind.Object:
                foreach (var property in new[] { "name", "title", "raw", "login", "text", "value" })
                {
                    if (value.TryGetChild(property, out var child) && DescribeValue(child) is { } described)
                    {
                        return described;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Everything that changed between two readings, as alerts. With <paramref name="onlyStatus"/>
    /// a card is news when it arrives, moves column, is archived or goes away; without it, any
    /// field changing is news too. <paramref name="complete"/> says whether the later reading saw
    /// the whole board: when it did not, an item missing from it is not reported as gone.
    /// </summary>
    public static IReadOnlyList<Alert> Diff(
        BoardSubscription board,
        IReadOnlyDictionary<string, BoardItemState> before,
        IReadOnlyDictionary<string, BoardItemState> after,
        bool onlyStatus,
        bool complete,
        DateTimeOffset now)
    {
        var alerts = new List<Alert>();

        foreach (var (id, item) in after)
        {
            if (!before.TryGetValue(id, out var previous))
            {
                alerts.Add(Build(board, id, item, "added", $"Added to {board.Title}", Line(StatusFieldName, item.Status), item.UpdatedAt));
                continue;
            }

            if (item.IsArchived && !previous.IsArchived)
            {
                alerts.Add(Build(board, id, item, "archived", "Archived", Line(StatusFieldName, item.Status), item.UpdatedAt));
                continue;
            }

            if (!string.Equals(previous.Status, item.Status, StringComparison.Ordinal))
            {
                var title = item.Status is null ? "Status cleared" : $"Moved to {item.Status}";
                alerts.Add(Build(board, id, item, "moved", title, Transition(StatusFieldName, previous.Status, item.Status), item.UpdatedAt));
                continue;
            }

            if (onlyStatus)
            {
                continue;
            }

            var changed = item.Fields
                .Where(f => !string.Equals(previous.Fields.GetValueOrDefault(f.Key), f.Value, StringComparison.Ordinal))
                .ToList();

            if (changed.Count == 0)
            {
                continue;
            }

            var headline = changed.Count == 1 ? $"{changed[0].Key} changed" : $"{changed.Count} fields changed";
            var note = string.Join(", ", changed.Select(f => Transition(f.Key, previous.Fields.GetValueOrDefault(f.Key), f.Value)));

            alerts.Add(Build(board, id, item, "edited", headline, note, item.UpdatedAt));
        }

        if (complete)
        {
            foreach (var (id, item) in before.Where(pair => !after.ContainsKey(pair.Key)))
            {
                alerts.Add(Build(board, id, item, "removed", $"Removed from {board.Title}", Line(StatusFieldName, item.Status), now));
            }
        }

        return alerts;
    }

    private static Alert Build(
        BoardSubscription board,
        string itemId,
        BoardItemState item,
        string change,
        string title,
        string? note,
        DateTimeOffset when) =>
        new()
        {
            Id = $"board:{board.Number}:{itemId}:{change}:{when.ToUnixTimeSeconds()}",
            Kind = AlertKind.Board,
            Title = title,
            Detail = Describe(item),
            Note = note,
            Repository = board.Key,
            Actor = null,
            Url = item.Url ?? board.Url,
            Timestamp = when,
            Fields = item.Fields.Select(f => new AlertField(f.Key, f.Value)).ToList(),
            Body = item.Body,
        };

    /// <summary>The card's own line: the issue or pull request with its number, or a draft's title.</summary>
    public static string Describe(BoardItemState item)
    {
        var title = string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title!;

        return item.ContentType switch
        {
            "PullRequest" when item.Number is { } pr => $"PR #{pr} {title}",
            "Issue" when item.Number is { } issue => $"#{issue} {title}",
            "DraftIssue" => $"Draft: {title}",
            _ => title,
        };
    }

    private static string? Line(string field, string? value) => value is null ? null : $"{field} · {value}";

    private static string Transition(string field, string? from, string? to) =>
        $"{field} · {from ?? "none"} → {to ?? "none"}";
}
