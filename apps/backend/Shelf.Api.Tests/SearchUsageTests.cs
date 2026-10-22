using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shelf.Api.Tests;

public sealed class SearchUsageTests : TestInstance
{
    private static async Task<Guid[]> Search(HttpClient client, string query) =>
        [.. (await Json(await client.GetAsync("/elements/search?query=" + Uri.EscapeDataString(query)))).EnumerateArray().Select(Id).Order()];

    [Fact]
    public async Task Search_matches_literal_substrings_across_visible_spaces_and_live_received_subtrees_only()
    {
        var (_, owner) = await User("alice");
        var (recipientId, recipient) = await User("bob");
        var personal = Id(await Upload(recipient, "My PHOTO.jpg"));
        var common = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photo common", space = "common" })));
        var hidden = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photo private" })));
        var shared = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "Photo shared", parentId = hidden })));
        await Json(await owner.PutAsJsonAsync($"/elements/{shared}/shares/{recipientId}", new { level = "read" }));
        var nested = Id(await Json(await owner.PostAsJsonAsync("/folders", new { name = "nested", parentId = shared })));
        var file = Id(await Upload(owner, "a photo.jpg", nested));
        // Overlapping grants must not duplicate results, and a file grant does not expose its ancestors.
        await Json(await owner.PutAsJsonAsync($"/elements/{file}/shares/{recipientId}", new { level = "write" }));
        var direct = Id(await Upload(owner, "direct photo.jpg", hidden));
        await Json(await owner.PutAsJsonAsync($"/elements/{direct}/shares/{recipientId}", new { level = "read" }));
        Assert.Equal(new[] { personal, common, shared, file, direct }.Order(), await Search(recipient, "pHoTo"));
        Assert.Equal(new[] { common }, await Search(Admin, "photo"));

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{shared}/shares/{recipientId}")).StatusCode);
        Assert.Equal(new[] { personal, common, file, direct }.Order(), await Search(recipient, "photo"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{shared}")).StatusCode);
        Assert.Equal(new[] { personal, common, direct }.Order(), await Search(recipient, "photo"));
        Assert.Equal(HttpStatusCode.NoContent, (await recipient.DeleteAsync($"/elements/{personal}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/elements/{common}")).StatusCode);
        Assert.Equal(new[] { direct }, await Search(recipient, "photo"));

        var literal = Id(await Upload(recipient, "100%_real.jpg"));
        await Upload(recipient, "100xxreal.jpg");
        Assert.Equal(new[] { literal }, await Search(recipient, "%_"));
        Assert.Empty(await Search(recipient, "' OR 1=1 --"));
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.GetAsync("/elements/search?query=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.GetAsync("/elements/search")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/elements/search?query=photo")).StatusCode);
    }
}
