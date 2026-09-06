using System.Net;
using System.Text.RegularExpressions;

namespace GitAlert.Core;

/// <summary>
/// Issue, comment and release bodies arrive as GitHub-flavoured Markdown. The pane is not a
/// Markdown renderer, so a body is boiled down to prose that reads well as plain text: template
/// comments dropped, links and images reduced to their words, emphasis marks and fences stripped,
/// task boxes drawn, and the whole thing cut short - the rest is one click away on GitHub.
/// </summary>
public static partial class PlainText
{
    /// <summary>How much of a body an alert keeps. Enough for the point; not a copy of the thread.</summary>
    public const int MaxLength = 1200;

    /// <summary>When cutting, how far back a word boundary is worth looking for.</summary>
    private const int BoundarySearch = 80;

    public static string? FromMarkdown(string? markdown, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var text = markdown.ReplaceLineEndings("\n");

        // Issue templates leave their instructions in comments; nobody means them to be read.
        text = HtmlComment().Replace(text, string.Empty);
        text = FenceLine().Replace(text, string.Empty);
        text = Image().Replace(text, m => m.Groups["alt"].Value.Trim() is { Length: > 0 } alt ? alt : "[image]");
        text = Link().Replace(text, "${text}");
        text = LineBreakTag().Replace(text, "\n");
        text = Tag().Replace(text, string.Empty);
        text = Heading().Replace(text, string.Empty);
        text = Quote().Replace(text, string.Empty);
        text = TaskBox().Replace(text, m => m.Groups["indent"].Value + (m.Groups["done"].Value.Length > 0 ? "☑ " : "☐ "));
        text = Emphasis().Replace(text, "${text}");
        text = Code().Replace(text, "${text}");
        text = WebUtility.HtmlDecode(text);

        text = string.Join('\n', text.Split('\n').Select(line => line.TrimEnd()));
        text = BlankRun().Replace(text, "\n\n").Trim();

        return text.Length == 0 ? null : Cut(text, maxLength);
    }

    /// <summary>The first line of a body, for the one-line places: a row, a toast.</summary>
    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.ReplaceLineEndings("\n").Split('\n', 2)[0].Trim();
        return line.Length == 0 ? null : line;
    }

    private static string Cut(string text, int maxLength)
    {
        if (maxLength < 2 || text.Length <= maxLength)
        {
            return text;
        }

        var end = maxLength - 1;
        var boundary = text.LastIndexOfAny([' ', '\n'], end);

        if (boundary > end - BoundarySearch)
        {
            end = boundary;
        }

        return text[..end].TrimEnd() + "…";
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^[ \t]*(```|~~~)[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"</?[a-zA-Z][^<>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[ \t]*>[ \t]?", RegexOptions.Multiline)]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*+][ \t]+\[(?<done>[xX])?[ ]?\][ \t]+", RegexOptions.Multiline)]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"(\*\*|__|~~)(?<text>[^\n]+?)\1")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"`(?<text>[^`\n]+)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun();
}
