using System.Net;
using System.Net.Http;
using GitAlert.Core;
using GitAlert.GitHub;
using Xunit;

namespace GitAlert.Tests;

/// <summary>The client's side of the Projects endpoints: where it asks, with what, and how it pages.</summary>
public class BoardClientTests
{
    private static readonly BoardRef Org = new("acme", BoardOwnerKind.Organization, 12);
    private static readonly BoardRef User = new("deniz", BoardOwnerKind.User, 3);

    private static (GitHubClient Client, StubHandler Handler) Build(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var client = new GitHubClient(new HttpClient(handler));
        client.SetToken("ghp_token");
        return (client, handler);
    }

    [Fact]
    public async Task A_board_is_asked_for_on_the_endpoint_its_owner_kind_names_under_the_projects_api_version()
    {
        var (client, handler) = Build(_ => Responses.Ok("""{"id": 1, "number": 12, "title": "Roadmap", "state": "open"}"""));

        var org = await client.GetProjectAsync(Org);
        var user = await client.GetProjectAsync(User);

        Assert.Equal("Roadmap", org.Title);
        Assert.Equal(12, user.Number);
        Assert.Equal("/orgs/acme/projectsV2/12", handler.Requests[0].Path);
        Assert.Equal("/users/deniz/projectsV2/3", handler.Requests[1].Path);
        Assert.All(handler.Requests, r => Assert.Equal("2026-03-10", r.ApiVersion));
    }

    [Fact]
    public async Task The_cards_are_asked_for_with_the_fields_named_and_the_last_tag_sent_back()
    {
        var (client, handler) = Build(_ => Responses.Ok("[]", ("ETag", "\"tag-1\"")));

        var page = await client.GetProjectItemsAsync(Org, [1, 3, 4], etag: "\"tag-0\"");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/orgs/acme/projectsV2/12/items", request.Path);
        Assert.Contains("per_page=100", request.Query);
        Assert.Contains("fields=1,3,4", request.Query);
        Assert.Equal("\"tag-0\"", request.IfNoneMatch);
        Assert.False(page.NotModified);
        Assert.Equal("\"tag-1\"", page.ETag);
        Assert.Empty(page.Value!.Items);
        Assert.Null(page.Value.NextCursor);
    }

    [Fact]
    public async Task An_untouched_board_comes_back_as_not_modified()
    {
        var (client, _) = Build(_ => Responses.Status(HttpStatusCode.NotModified));

        var page = await client.GetProjectItemsAsync(Org, [3], etag: "\"tag-0\"");

        Assert.True(page.NotModified);
        Assert.Equal("\"tag-0\"", page.ETag);
    }

    [Fact]
    public async Task The_next_page_is_found_in_the_link_header_and_asked_for_by_its_cursor()
    {
        var (client, handler) = Build(request => request.Query.Contains("after=")
            ? Responses.Ok("""[{"id": 2, "content_type": "Issue", "content": null, "updated_at": "2026-09-06T10:00:00Z", "archived_at": null, "fields": []}]""")
            : Responses.Ok(
                """[{"id": 1, "content_type": "Issue", "content": null, "updated_at": "2026-09-06T10:00:00Z", "archived_at": null, "fields": []}]""",
                ("Link", "<https://api.github.com/orgs/acme/projectsV2/12/items?per_page=100&fields=3&after=Y3Vyc29y%3D>; rel=\"next\", <https://api.github.com/orgs/acme/projectsV2/12/items?per_page=100>; rel=\"first\"")));

        var first = await client.GetProjectItemsAsync(Org, [3], etag: null);
        var second = await client.GetProjectItemsAsync(Org, [3], etag: null, after: first.Value!.NextCursor);

        Assert.Equal("Y3Vyc29y=", first.Value.NextCursor);
        Assert.Equal(1, first.Value.Items[0].Id);
        Assert.Contains("after=Y3Vyc29y%3D", handler.Requests[1].Query);
        Assert.Null(second.Value!.NextCursor);
        Assert.Equal(2, second.Value.Items[0].Id);
    }

    [Fact]
    public async Task The_fields_of_a_board_are_read_with_their_options()
    {
        var (client, handler) = Build(_ => Responses.Ok(
            """[{"id": 3, "name": "Status", "data_type": "single_select", "options": [{"id": "o1", "name": {"raw": "Todo", "html": "Todo"}}]}, {"id": 1, "name": "Title", "data_type": "title"}]"""));

        var fields = await client.GetProjectFieldsAsync(User);

        Assert.Equal("/users/deniz/projectsV2/3/fields", handler.Requests[0].Path);
        Assert.Equal(2, fields.Count);
        Assert.True(fields[0].IsSingleSelect);
        Assert.Equal("o1", fields[0].Options![0].Id);
        Assert.False(fields[1].IsSingleSelect);
    }

    [Fact]
    public async Task The_boards_of_an_owner_and_the_organisations_of_the_user_are_listed()
    {
        var (client, handler) = Build(request => request.Path == "/user/orgs"
            ? Responses.Ok("""[{"login": "acme"}]""")
            : Responses.Ok("""[{"id": 1, "number": 12, "title": "Roadmap", "state": "open"}, {"id": 2, "number": 9, "title": "Old", "state": "closed"}]"""));

        var orgs = await client.GetMyOrganizationsAsync();
        var boards = await client.GetProjectsAsync("acme", BoardOwnerKind.Organization);
        var own = await client.GetProjectsAsync("deniz", BoardOwnerKind.User);

        Assert.Equal("acme", Assert.Single(orgs).Login);
        Assert.Equal("/orgs/acme/projectsV2", handler.Requests[1].Path);
        Assert.Equal("/users/deniz/projectsV2", handler.Requests[2].Path);
        Assert.Equal(2, boards.Count);
        Assert.True(boards[1].IsClosed);
        Assert.Equal(2, own.Count);
    }

    /// <summary>The name in a refusal is the board's, the way the user watches it, not the path.</summary>
    [Fact]
    public async Task A_refusal_names_the_board_rather_than_the_path()
    {
        var (client, _) = Build(_ => Responses.Status(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.GetProjectItemsAsync(Org, [3], etag: null));

        Assert.Equal(GitHubErrorKind.NotFound, error.Kind);
        Assert.StartsWith("acme/#12 was not found", error.Message);
    }
}
