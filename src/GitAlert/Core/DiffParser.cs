using System.Text.RegularExpressions;

namespace GitAlert.Core;

public enum DiffLineKind
{
    /// <summary>An unchanged line, shown for context on both sides.</summary>
    Context,

    Added,

    Removed,

    /// <summary>An <c>@@ -a,b +c,d @@</c> header separating two hunks.</summary>
    Hunk,

    /// <summary>Anything git says about the patch itself, such as a missing trailing newline.</summary>
    Note,
}

/// <summary>One rendered row of a diff, already carrying the line numbers it belongs to.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLine, int? NewLine)
{
    /// <summary>The numbers as the gutter shows them: blank where the line does not exist on that side.</summary>
    public string OldNumber => OldLine?.ToString() ?? string.Empty;

    public string NewNumber => NewLine?.ToString() ?? string.Empty;
}

/// <summary>
/// Turns the unified diff GitHub returns for a file into rows a view can render. GitHub's per-file
/// patch starts straight at the first hunk header, so there are no <c>---</c>/<c>+++</c> lines to
/// skip; everything else follows the ordinary unified format.
/// </summary>
public static partial class DiffParser
{
    /// <summary>
    /// Rendering every line of a very large patch costs more than it tells anyone, so parsing
    /// stops here and the caller reports the remainder as truncated.
    /// </summary>
    public const int MaxLines = 1200;

    public static IReadOnlyList<DiffLine> Parse(string? patch) => Parse(patch, MaxLines, out _);

    /// <summary>
    /// Parses at most <paramref name="limit"/> rows.
    /// <paramref name="truncated"/> reports how many rows were left unparsed.
    /// </summary>
    public static IReadOnlyList<DiffLine> Parse(string? patch, int limit, out int truncated)
    {
        truncated = 0;

        if (string.IsNullOrEmpty(patch))
        {
            return [];
        }

        var source = patch.Split('\n');
        var lines = new List<DiffLine>(Math.Min(source.Length, limit));

        var oldLine = 0;
        var newLine = 0;

        for (var i = 0; i < source.Length; i++)
        {
            if (lines.Count >= limit)
            {
                truncated = source.Length - i;
                break;
            }

            // Patches carry \n endings, so a \r survives the split on Windows-authored files.
            var raw = source[i].TrimEnd('\r');

            // A trailing newline in the patch produces one empty element that is not a diff row.
            if (raw.Length == 0 && i == source.Length - 1)
            {
                break;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var header = HunkHeader().Match(raw);

                if (header.Success)
                {
                    oldLine = int.Parse(header.Groups[1].Value);
                    newLine = int.Parse(header.Groups[2].Value);
                }

                lines.Add(new DiffLine(DiffLineKind.Hunk, raw, null, null));
                continue;
            }

            if (raw.StartsWith('\\'))
            {
                // "\ No newline at end of file" belongs to neither side.
                lines.Add(new DiffLine(DiffLineKind.Note, raw.TrimStart('\\', ' '), null, null));
                continue;
            }

            if (raw.StartsWith('+'))
            {
                lines.Add(new DiffLine(DiffLineKind.Added, raw[1..], null, newLine++));
                continue;
            }

            if (raw.StartsWith('-'))
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, raw[1..], oldLine++, null));
                continue;
            }

            // A context line is prefixed with a single space, but empty context lines are
            // sometimes emitted with the space stripped.
            lines.Add(new DiffLine(DiffLineKind.Context, raw.Length > 0 ? raw[1..] : raw, oldLine++, newLine++));
        }

        return lines;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}
