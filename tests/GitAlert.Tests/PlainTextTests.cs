using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

/// <summary>A Markdown body boiled down to prose the pane can show as it is.</summary>
public class PlainTextTests
{
    [Fact]
    public void Template_comments_headings_links_and_marks_come_off_and_the_words_stay()
    {
        var markdown = """
            <!-- Please fill in every section. -->
            ## What happened

            The **poller** keeps asking every _two_ minutes, see [the docs](https://docs.github.com/rate-limit)
            and ![the trace](https://example.com/trace.png). Fails on `main` since ~~1.24~~ 1.25.

            > Expected: back off.
            """;

        var text = PlainText.FromMarkdown(markdown);

        Assert.Equal(
            "What happened\n\n"
            + "The poller keeps asking every _two_ minutes, see the docs\n"
            + "and the trace. Fails on main since 1.24 1.25.\n\n"
            + "Expected: back off.",
            text);
    }

    [Fact]
    public void Task_boxes_are_drawn_and_fences_drop_but_their_code_stays()
    {
        var text = PlainText.FromMarkdown("- [ ] write it\n- [x] test it\n```csharp\nvar x = 1;\n```\nDone.");

        Assert.Equal("☐ write it\n☑ test it\nvar x = 1;\nDone.", text);
    }

    [Fact]
    public void Html_tags_go_line_breaks_stay_and_entities_are_read()
    {
        var text = PlainText.FromMarkdown("<details><summary>Trace</summary>a &lt; b<br>c &amp; d</details>");

        Assert.Equal("Tracea < b\nc & d", text);
    }

    [Fact]
    public void Runs_of_blank_lines_collapse_and_edges_are_trimmed()
    {
        Assert.Equal("one\n\ntwo", PlainText.FromMarkdown("\r\n\r\none   \r\n\r\n\r\n\r\ntwo\r\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("<!-- only a comment -->")]
    public void Nothing_worth_reading_is_null(string? markdown)
    {
        Assert.Null(PlainText.FromMarkdown(markdown));
    }

    [Fact]
    public void A_long_body_is_cut_at_a_word_and_says_so()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 400));

        var text = PlainText.FromMarkdown(words, maxLength: 100)!;

        Assert.True(text.Length <= 100);
        Assert.EndsWith("word…", text);
        Assert.DoesNotContain("wor…", text);
    }

    [Fact]
    public void A_body_that_fits_is_left_alone()
    {
        Assert.Equal("short", PlainText.FromMarkdown("short", maxLength: 5));
    }

    [Fact]
    public void The_first_line_is_the_first_line_with_nothing_else()
    {
        Assert.Equal("first", PlainText.FirstLine("  first \nsecond"));
        Assert.Null(PlainText.FirstLine(" \n second"));
        Assert.Null(PlainText.FirstLine(null));
    }
}
