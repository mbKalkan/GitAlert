using System.IO;
using GitAlert.Configuration;
using Xunit;

namespace GitAlert.Tests;

/// <summary>
/// The account id is not user input in any ordinary sense - it is a generated GUID - but it names
/// a file, and it reaches the store through settings.json, which sits in a folder the user can
/// open and edit. These are about what happens when it is not the generated shape.
/// </summary>
public class SecureTokenStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GitAlertTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("6f9619ff8b86d011b42d00c04fc964ff")]
    [InlineData("account-1")]
    [InlineData("account_1")]
    public void The_generated_shape_of_an_id_is_accepted(string id) =>
        Assert.True(SecureTokenStore.IsValidAccountId(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../evil")]
    [InlineData(@"..\..\evil")]
    [InlineData("C:/Windows/System32/evil")]
    [InlineData("account.1")]
    [InlineData("has space")]
    public void An_id_that_could_escape_the_token_folder_is_refused(string? id) =>
        Assert.False(SecureTokenStore.IsValidAccountId(id));

    [Fact]
    public void Writing_under_a_traversing_id_writes_nothing_at_all()
    {
        var store = new SecureTokenStore(_root);
        var escapee = Path.Combine(_root, "escaped.bin");

        Assert.Throws<ArgumentException>(() => store.Write(@"..\escaped", "ghp_secret"));
        Assert.False(File.Exists(escapee));

        // And the folder the token would have gone to is not even created on the way out.
        Assert.False(Directory.Exists(Path.Combine(_root, "tokens")));
    }

    [Fact]
    public void Reading_a_traversing_id_reports_no_token_rather_than_reaching_for_one()
    {
        var store = new SecureTokenStore(_root);

        Assert.Null(store.Read(@"..\..\anything"));
        Assert.False(store.Has(@"..\..\anything"));
    }

    /// <summary>Deleting has to be as quiet as reading, or a bad id would throw during a save.</summary>
    [Fact]
    public void Deleting_a_traversing_id_does_nothing()
    {
        var store = new SecureTokenStore(_root);

        store.Delete("../../anything");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
