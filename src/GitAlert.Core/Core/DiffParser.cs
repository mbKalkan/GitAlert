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
public static class DiffParser
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
    /// <remarks>
    /// Walks the patch a line at a time rather than splitting it first. Splitting allocated an
    /// array over the whole patch - every line of a twenty-megabyte generated file - only to read
    /// the first twelve hundred entries of it.
    /// </remarks>
    public static IReadOnlyList<DiffLine> Parse(string? patch, int limit, out int truncated)
    {
        truncated = 0;

        if (string.IsNullOrEmpty(patch) || limit <= 0)
        {
            return [];
        }

        var lines = new List<DiffLine>(Math.Min(limit, 128));

        var oldLine = 0;
        var newLine = 0;
        var cursor = 0;

        while (cursor < patch.Length)
        {
            if (lines.Count >= limit)
            {
                truncated = CountLines(patch.AsSpan(cursor));
                break;
            }

            var newline = patch.IndexOf('\n', cursor);
            var stop = newline < 0 ? patch.Length : newline;

            // Patches carry \n endings, so a \r survives on Windows-authored files.
            var raw = patch.AsSpan(cursor, stop - cursor).TrimEnd('\r');

            cursor = newline < 0 ? patch.Length : newline + 1;

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryReadHunkHeader(raw, out var fromOld, out var fromNew))
                {
                    oldLine = fromOld;
                    newLine = fromNew;
                }

                lines.Add(new DiffLine(DiffLineKind.Hunk, raw.ToString(), null, null));
                continue;
            }

            if (raw.StartsWith("\\", StringComparison.Ordinal))
            {
                // "\ No newline at end of file" belongs to neither side.
                lines.Add(new DiffLine(DiffLineKind.Note, raw.TrimStart(" \\").ToString(), null, null));
                continue;
            }

            if (raw.StartsWith("+", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Added, raw[1..].ToString(), null, newLine++));
                continue;
            }

            if (raw.StartsWith("-", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, raw[1..].ToString(), oldLine++, null));
                continue;
            }

            // A context line is prefixed with a single space, but empty context lines are
            // sometimes emitted with the space stripped.
            var text = raw.Length > 0 ? raw[1..].ToString() : string.Empty;
            lines.Add(new DiffLine(DiffLineKind.Context, text, oldLine++, newLine++));
        }

        return lines;
    }

    /// <summary>How many lines are left in the tail the parser stopped short of.</summary>
    private static int CountLines(ReadOnlySpan<char> rest)
    {
        var count = 0;

        while (!rest.IsEmpty)
        {
            count++;

            var newline = rest.IndexOf('\n');

            if (newline < 0)
            {
                break;
            }

            rest = rest[(newline + 1)..];
        }

        return count;
    }

    /// <summary>
    /// Reads the two starting line numbers out of an <c>@@ -a,b +c,d @@</c> header.
    /// </summary>
    /// <remarks>
    /// By hand rather than by regex, for two reasons. It needs no substring per hunk, and a
    /// count long enough to overflow an int now makes the header unreadable instead of throwing
    /// out of a background parse - the patch is repository content, so its shape is not ours
    /// to trust.
    /// </remarks>
    private static bool TryReadHunkHeader(ReadOnlySpan<char> header, out int oldLine, out int newLine)
    {
        oldLine = 0;
        newLine = 0;

        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');

        return minus >= 0
            && plus > minus
            && TryReadNumber(header[(minus + 1)..], out oldLine)
            && TryReadNumber(header[(plus + 1)..], out newLine);
    }

    private static bool TryReadNumber(ReadOnlySpan<char> text, out int value)
    {
        var end = 0;

        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        value = 0;
        return end > 0 && int.TryParse(text[..end], out value);
    }
}
