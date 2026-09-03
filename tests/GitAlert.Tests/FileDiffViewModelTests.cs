using GitAlert.Core;
using GitAlert.GitHub;
using GitAlert.ViewModels;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// One row of the change list, and the diff behind it. Most of what matters here is what GitHub
/// does not send: no patch for a binary, no patch for a diff it considers too large, and a patch
/// long enough that rendering all of it would cost more than it tells anyone.
/// </summary>
public class FileDiffViewModelTests
{
    private static GhFileChange File(
        string filename = "src/Api.cs",
        string? status = "modified",
        string? patch = "@@ -1,1 +1,1 @@\n-old\n+new",
        int changes = 2,
        string? previous = null) =>
        new()
        {
            Filename = filename,
            Status = status,
            Patch = patch,
            Changes = changes,
            Additions = 1,
            Deletions = 1,
            PreviousFilename = previous,
        };

    // ---- What GitHub does not send -----------------------------------------

    /// <summary>
    /// A binary file comes back with counts and no patch. Rendering nothing would look like a
    /// file that changed in no way, which is a different and wrong statement.
    /// </summary>
    [Fact]
    public void A_file_with_no_patch_says_why_rather_than_rendering_an_empty_diff()
    {
        var file = new FileDiffViewModel(File(filename: "docs/logo.png", patch: null, changes: 1));

        Assert.Empty(file.Lines);
        Assert.True(file.HasNote);
        Assert.Contains("binary or too large", file.Note);
    }

    [Fact]
    public void A_file_with_no_patch_and_no_changes_says_that_instead()
    {
        var file = new FileDiffViewModel(File(patch: null, changes: 0));

        Assert.Equal("No textual changes.", file.Note);
    }

    [Fact]
    public void A_patch_within_the_limit_carries_no_note_at_all()
    {
        var file = new FileDiffViewModel(File());

        Assert.False(file.HasNote);
        Assert.Null(file.Note);
    }

    [Fact]
    public void A_patch_past_the_limit_says_how_much_was_left_out()
    {
        var patch = "@@ -1,5000 +1,5000 @@\n"
                  + string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"+line {i}"));

        var file = new FileDiffViewModel(File(patch: patch));

        Assert.Equal(DiffParser.MaxLines, file.Lines.Count);
        Assert.Equal(5001 - DiffParser.MaxLines, file.TruncatedLines);
        Assert.Contains("more lines not shown", file.Note);
    }

    // ---- Only the file being read is parsed --------------------------------

    /// <summary>
    /// A merge can touch three hundred files and the pane shows one of them. Parsing them all up
    /// front built a row object per line of every file before the first line appeared, so the
    /// change list must be able to be built without touching a single patch.
    /// </summary>
    [Fact]
    public void Building_the_change_list_does_not_parse_the_diffs()
    {
        var patch = "@@ -1,2000 +1,2000 @@\n"
                  + string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"+line {i}"));

        var files = Enumerable.Range(0, 200).Select(i => File(filename: $"src/File{i}.cs", patch: patch)).ToList();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var rows = files.Select(f => new FileDiffViewModel(f)).ToList();
        var listCost = GC.GetAllocatedBytesForCurrentThread() - before;

        // Two hundred rows of a name and two counts. Parsing even one of these patches allocates
        // several hundred kilobytes, so the ceiling here is far below parsing them all.
        Assert.True(listCost < 1_000_000, $"building the list allocated {listCost:N0} bytes");

        // And the rows still work when one of them is actually opened.
        var before2 = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(DiffParser.MaxLines, rows[7].Lines.Count);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before2 > listCost);
    }

    [Fact]
    public void Reading_a_diff_twice_parses_it_once_and_hands_back_the_same_rows()
    {
        var file = new FileDiffViewModel(File());

        Assert.Same(file.Lines, file.Lines);
    }

    // ---- How a row reads ---------------------------------------------------

    [Theory]
    [InlineData("added", "A")]
    [InlineData("removed", "D")]
    [InlineData("renamed", "R")]
    [InlineData("copied", "C")]
    [InlineData("modified", "M")]
    [InlineData("changed", "M")]
    [InlineData(null, "M")]
    [InlineData("something GitHub has not invented yet", "M")]
    public void The_status_letter_falls_back_to_modified_rather_than_going_blank(string? status, string expected)
    {
        Assert.Equal(expected, new FileDiffViewModel(File(status: status)).StatusLetter);
    }

    [Fact]
    public void A_copied_file_counts_as_an_addition_and_a_removed_one_as_a_deletion()
    {
        Assert.True(new FileDiffViewModel(File(status: "copied")).IsAdded);
        Assert.True(new FileDiffViewModel(File(status: "added")).IsAdded);
        Assert.True(new FileDiffViewModel(File(status: "removed")).IsDeleted);
        Assert.False(new FileDiffViewModel(File(status: "modified")).IsAdded);
    }

    [Theory]
    [InlineData("src/deep/Api.cs", "src/deep/", "Api.cs")]
    [InlineData("README.md", "", "README.md")]
    [InlineData("src/", "src/", "")]
    [InlineData("/leading", "/", "leading")]
    public void A_path_splits_into_the_folder_and_the_name_the_header_shows(
        string path,
        string folder,
        string name)
    {
        var file = new FileDiffViewModel(File(filename: path));

        Assert.Equal(folder, file.Folder);
        Assert.Equal(name, file.FileName);
    }

    [Fact]
    public void A_rename_remembers_where_the_file_came_from()
    {
        var file = new FileDiffViewModel(File(status: "renamed", previous: "src/Old.cs"));

        Assert.True(file.WasRenamed);
        Assert.Equal("src/Old.cs", file.PreviousPath);
    }

    [Fact]
    public void A_file_that_did_not_move_is_not_a_rename()
    {
        Assert.False(new FileDiffViewModel(File()).WasRenamed);
        Assert.False(new FileDiffViewModel(File(previous: string.Empty)).WasRenamed);
    }

    [Fact]
    public void The_counts_read_the_way_a_diff_writes_them()
    {
        var file = new FileDiffViewModel(new GhFileChange
        {
            Filename = "src/Api.cs",
            Additions = 12,
            Deletions = 3,
        });

        Assert.Equal("+12  -3", file.Counts);
    }
}
