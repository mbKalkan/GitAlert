using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class DiffParserTests
{
    private const string Patch =
        "@@ -3,6 +3,7 @@ public class Api\n" +
        "     private readonly Client _client;\n" +
        "-    private int _retries = 3;\n" +
        "+    private int _retries = 5;\n" +
        "+    private bool _verbose;\n" +
        " \n" +
        "     public Api()";

    [Fact]
    public void Line_numbers_run_from_the_hunk_header_down_each_side()
    {
        var lines = DiffParser.Parse(Patch);

        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);

        // The context line before the change is line 3 on both sides.
        Assert.Equal(DiffLineKind.Context, lines[1].Kind);
        Assert.Equal(3, lines[1].OldLine);
        Assert.Equal(3, lines[1].NewLine);

        // A removed line exists only on the left, an added line only on the right.
        Assert.Equal(DiffLineKind.Removed, lines[2].Kind);
        Assert.Equal(4, lines[2].OldLine);
        Assert.Null(lines[2].NewLine);

        Assert.Equal(DiffLineKind.Added, lines[3].Kind);
        Assert.Null(lines[3].OldLine);
        Assert.Equal(4, lines[3].NewLine);

        Assert.Equal(DiffLineKind.Added, lines[4].Kind);
        Assert.Equal(5, lines[4].NewLine);

        // Afterwards both sides advance together again, one line apart: two additions against
        // one removal leaves the right-hand side a net line ahead.
        Assert.Equal(DiffLineKind.Context, lines[6].Kind);
        Assert.Equal(6, lines[6].OldLine);
        Assert.Equal(7, lines[6].NewLine);
    }

    [Fact]
    public void The_marker_character_is_stripped_from_the_code()
    {
        var lines = DiffParser.Parse(Patch);

        Assert.Equal("    private int _retries = 5;", lines[3].Text);
        Assert.Equal("    private readonly Client _client;", lines[1].Text);
    }

    [Fact]
    public void A_second_hunk_restarts_the_counters()
    {
        var lines = DiffParser.Parse("@@ -1,2 +1,2 @@\n a\n@@ -40,3 +52,3 @@\n b");

        Assert.Equal(1, lines[1].OldLine);
        Assert.Equal(40, lines[3].OldLine);
        Assert.Equal(52, lines[3].NewLine);
    }

    [Fact]
    public void A_missing_trailing_newline_is_a_note_belonging_to_neither_side()
    {
        var lines = DiffParser.Parse("@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file");

        var note = lines[^1];
        Assert.Equal(DiffLineKind.Note, note.Kind);
        Assert.Equal("No newline at end of file", note.Text);
        Assert.Null(note.OldLine);
        Assert.Null(note.NewLine);
    }

    [Fact]
    public void Windows_line_endings_do_not_leak_into_the_rendered_text()
    {
        var lines = DiffParser.Parse("@@ -1 +1 @@\r\n+added\r\n");

        Assert.Equal("added", lines[1].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_file_with_no_patch_produces_no_rows(string? patch)
    {
        Assert.Empty(DiffParser.Parse(patch));
    }

    [Fact]
    public void A_very_large_patch_stops_at_the_limit_and_reports_the_rest()
    {
        var patch = "@@ -1,400 +1,400 @@\n" + string.Join('\n', Enumerable.Range(0, 400).Select(i => $" line {i}"));

        var lines = DiffParser.Parse(patch, limit: 50, out var truncated);

        Assert.Equal(50, lines.Count);
        Assert.Equal(351, truncated);
    }

    /// <summary>
    /// A patch is repository content, and a repository can be anyone's. A line number too large
    /// for an int used to throw straight out of the parse, which reached the UI as an unhandled
    /// exception rather than as one unreadable file.
    /// </summary>
    [Theory]
    [InlineData("@@ -99999999999999999999,1 +1,1 @@\n+x")]
    [InlineData("@@ -1,1 +99999999999999999999,1 @@\n+x")]
    [InlineData("@@ nonsense @@\n+x")]
    [InlineData("@@\n+x")]
    public void An_unreadable_hunk_header_is_a_row_rather_than_a_crash(string patch)
    {
        var lines = DiffParser.Parse(patch);

        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);
        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
    }

    /// <summary>
    /// The gutter shows a blank rather than a zero on the side a line does not exist on, which is
    /// what keeps an added line from looking like it replaced line 0.
    /// </summary>
    [Fact]
    public void The_gutter_is_blank_on_the_side_a_line_is_missing_from()
    {
        var lines = DiffParser.Parse("@@ -7,1 +7,2 @@\n+brand new");

        Assert.Equal(string.Empty, lines[1].OldNumber);
        Assert.Equal("7", lines[1].NewNumber);
    }
}
